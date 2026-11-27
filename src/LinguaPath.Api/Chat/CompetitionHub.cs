using LinguaPath.Application.Features.Competitions;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LinguaPath.Api.Chat;

/// <summary>Event names the app listens for on /hubs/competition.</summary>
public static class CompetitionEvents
{
    public const string QuestionStarted = "QuestionStarted";
    public const string OpponentAnswered = "OpponentAnswered";
    public const string QuestionResult = "QuestionResult";
    public const string MatchUpdated = "MatchUpdated";
    public const string MatchEnded = "MatchEnded";
    public const string ChallengeReceived = "ChallengeReceived";
}

/// <summary>
/// Live quiz play (FR-Q03). The server keeps time and checks answers (FR-Q05); the same moves are also available over REST
/// (/api/v1/competitions/{id}/answers), so a dropped socket never loses an answer.
/// </summary>
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class CompetitionHub(ICompetitionService competitions) : Hub
{
    private Guid UserId => Guid.TryParse(Context.UserIdentifier, out var id) ? id : throw new HubException("Not signed in.");

    /// <summary>Returns the current state, including the open question, so a reconnecting player picks up where they were.</summary>
    public async Task<CompetitionDto?> JoinMatch(int competitionId)
    {
        var result = await competitions.GetAsync(UserId, competitionId, Context.ConnectionAborted);
        return result.IsSuccess ? result.Value : null;
    }

    public async Task<AnswerAckDto> SubmitAnswer(int competitionId, SubmitAnswerRequest request)
    {
        var result = await competitions.SubmitAnswerAsync(UserId, competitionId, request, Context.ConnectionAborted);
        return result.IsSuccess ? result.Value : new AnswerAckDto(false, result.Error!.Code, result.Error.Message, null, null, false);
    }
}

public sealed class SignalRCompetitionNotifier(IHubContext<CompetitionHub> hub) : ICompetitionNotifier
{
    private static IReadOnlyList<string> Ids(IEnumerable<Guid> ids) => ids.Distinct().Select(i => i.ToString()).ToList();

    public Task QuestionStartedAsync(IReadOnlyCollection<Guid> userIds, CompetitionQuestionDto question, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(CompetitionEvents.QuestionStarted, question, ct);

    public Task OpponentAnsweredAsync(IReadOnlyCollection<Guid> userIds, OpponentAnsweredDto progress, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(CompetitionEvents.OpponentAnswered, progress, ct);

    public Task QuestionResultAsync(IReadOnlyCollection<Guid> userIds, QuestionResultDto result, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(CompetitionEvents.QuestionResult, result, ct);

    public Task MatchUpdatedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(CompetitionEvents.MatchUpdated, competitionId, ct);

    public Task MatchEndedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(CompetitionEvents.MatchEnded, competitionId, ct);

    public Task ChallengeReceivedAsync(Guid userId, CompetitionSummaryDto challenge, CancellationToken ct = default) =>
        hub.Clients.User(userId.ToString()).SendAsync(CompetitionEvents.ChallengeReceived, challenge, ct);
}

/// <summary>
/// The competition clock: every half second closes questions that ran out of time and opens the next, starts countdowns and lobbies,
/// expires challenges, moves tournaments on; once an hour it closes last week's leagues.
/// </summary>
public sealed class CompetitionTicker(IServiceScopeFactory scopes, ILogger<CompetitionTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextLeagues = DateTimeOffset.UtcNow;
        var nextTournaments = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICompetitionService>().TickAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextTournaments)
                {
                    nextTournaments = DateTimeOffset.UtcNow.AddSeconds(2);
                    using var t = scopes.CreateScope();
                    await t.ServiceProvider.GetRequiredService<ITournamentService>().TickAsync(stoppingToken);
                }
                if (DateTimeOffset.UtcNow >= nextLeagues)
                {
                    nextLeagues = DateTimeOffset.UtcNow.AddHours(1);
                    using var l = scopes.CreateScope();
                    await l.ServiceProvider.GetRequiredService<ILeagueService>().CloseDueAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Competition clock tick failed");
            }
        }
    }
}
