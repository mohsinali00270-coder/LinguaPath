using System.Collections.Concurrent;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Application.Features.Social;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Competitions;

/// <summary>Timings for the live engine (configuration section "Competitions"). Tests set the waits to 0.</summary>
public sealed class CompetitionOptions
{
    public const string Section = "Competitions";
    /// <summary>Countdown before the first question.</summary>
    public int CountdownSeconds { get; set; } = 5;
    /// <summary>Time to show each question's result before the next one opens.</summary>
    public int ResultPauseSeconds { get; set; } = 3;
    /// <summary>Allowance for network delay after a question closes.</summary>
    public int GraceMilliseconds { get; set; } = 1500;
    /// <summary>Quick match search before the bot is offered.</summary>
    public int QuickMatchWaitSeconds { get; set; } = 30;
    public int ChallengeExpiryHours { get; set; } = 24;
    public int GroupBattleMinPlayers { get; set; } = 3;
    public int GroupBattleMaxPlayers { get; set; } = 50;
    /// <summary>Break between tournament rounds.</summary>
    public int TournamentBreakSeconds { get; set; } = 30;
}

/// <summary>Live events on /hubs/competition. The API implements it with SignalR.</summary>
public interface ICompetitionNotifier
{
    Task QuestionStartedAsync(IReadOnlyCollection<Guid> userIds, CompetitionQuestionDto question, CancellationToken ct = default);
    Task OpponentAnsweredAsync(IReadOnlyCollection<Guid> userIds, OpponentAnsweredDto progress, CancellationToken ct = default);
    Task QuestionResultAsync(IReadOnlyCollection<Guid> userIds, QuestionResultDto result, CancellationToken ct = default);
    /// <summary>Lobby, players or status changed: clients reload the competition.</summary>
    Task MatchUpdatedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default);
    Task MatchEndedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default);
    Task ChallengeReceivedAsync(Guid userId, CompetitionSummaryDto challenge, CancellationToken ct = default);
}

public sealed class NullCompetitionNotifier : ICompetitionNotifier
{
    public Task QuestionStartedAsync(IReadOnlyCollection<Guid> userIds, CompetitionQuestionDto question, CancellationToken ct = default) => Task.CompletedTask;
    public Task OpponentAnsweredAsync(IReadOnlyCollection<Guid> userIds, OpponentAnsweredDto progress, CancellationToken ct = default) => Task.CompletedTask;
    public Task QuestionResultAsync(IReadOnlyCollection<Guid> userIds, QuestionResultDto result, CancellationToken ct = default) => Task.CompletedTask;
    public Task MatchUpdatedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MatchEndedAsync(IReadOnlyCollection<Guid> userIds, int competitionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChallengeReceivedAsync(Guid userId, CompetitionSummaryDto challenge, CancellationToken ct = default) => Task.CompletedTask;
}

public interface ICompetitionService
{
    Task<Result<CompetitionDto>> ChallengeAsync(Guid userId, ChallengeRequest request, CancellationToken ct = default);
    Task<Result<CompetitionDto>> AcceptAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result> DeclineAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CompetitionDto>> QuickMatchAsync(Guid userId, QuickMatchRequest request, CancellationToken ct = default);
    /// <summary>No opponent found: play the bot (practice, unranked).</summary>
    Task<Result<CompetitionDto>> PlayBotAsync(Guid userId, int id, CancellationToken ct = default);
    /// <summary>Stop searching, or withdraw my challenge before it's accepted.</summary>
    Task<Result> CancelAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CompetitionDto>> GroupBattleAsync(Guid userId, int conversationId, GroupBattleRequest request, CancellationToken ct = default);
    Task<Result<CompetitionDto>> JoinAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CompetitionDto>> StartNowAsync(Guid userId, int id, CancellationToken ct = default);
    /// <summary>Your-own-time challenge: start my run.</summary>
    Task<Result<CompetitionDto>> PlayAsync(Guid userId, int id, CancellationToken ct = default);

    Task<Result<CompetitionDto>> GetAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<AnswerAckDto>> SubmitAnswerAsync(Guid userId, int id, SubmitAnswerRequest request, CancellationToken ct = default);
    Task<Result> LeaveAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CompetitionResultDto>> ResultAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CompetitionDto>> RematchAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result> ShareAsync(Guid userId, int id, int conversationId, CancellationToken ct = default);

    Task<IReadOnlyList<CompetitionSummaryDto>> ActiveAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<CompetitionSummaryDto>> HistoryAsync(Guid userId, int page, CancellationToken ct = default);
    Task<CompetitionStatsDto> StatsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The clock: moves every competition that has something due. Returns how many it touched.</summary>
    Task<int> TickAsync(CancellationToken ct = default);
    Task AdvanceAsync(int id, CancellationToken ct = default);
}

/// <summary>Used by tournaments: create a live round for the given players.</summary>
public interface ICompetitionEngine
{
    Task<Result<int>> CreateRoundAsync(Tournament tournament, int round, IReadOnlyList<Guid> userIds, DateTimeOffset startsAt, CancellationToken ct = default);
}

internal sealed class CompetitionService(
    IApplicationDbContext db,
    ISocialService social,
    IChatService chat,
    IModerationService moderation,
    IPointsService points,
    IPushSender push,
    ICompetitionNotifier notifier,
    ISettingsService settings,
    IOptions<CompetitionOptions> options,
    TimeProvider clock,
    ILogger<CompetitionService> logger) : ICompetitionService, ICompetitionEngine
{
    /// <summary>One engine step at a time per competition (ticker and players can arrive together). One API instance; see README.</summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();

    private CompetitionOptions O => options.Value;

    // ================================================================== creating

    public async Task<Result<CompetitionDto>> ChallengeAsync(Guid userId, ChallengeRequest r, CancellationToken ct = default)
    {
        if (CompetitionRules.ValidateSettings(r.QuestionCount, r.SecondsPerQuestion) is { } bad) return Error.Validation("QuestionCount", bad);
        if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
        var allowed = await social.CanContactAsync(userId, r.OpponentId, challenge: true, ct);
        if (allowed.IsFailure) return allowed.Error!;
        if (await RankedLimitAsync(userId, ct) is { } limit) return limit;

        var level = r.Level ?? await LevelOfAsync(userId, r.LanguageId, ct);
        if (level is null) return Error.Validation("Level", "Choose a level.");
        var now = clock.GetUtcNow();
        var c = new Competition
        {
            Mode = CompetitionMode.Challenge, Status = CompetitionStatus.Pending, LanguageId = r.LanguageId, Level = level.Value, ModuleId = r.ModuleId,
            QuestionCount = r.QuestionCount, SecondsPerQuestion = r.SecondsPerQuestion, IsLive = r.Live, IsRanked = true, CreatedBy = userId,
            CreatedAt = now, ExpiresAt = now.AddHours(O.ChallengeExpiryHours)
        };
        var picked = await PickQuestionsAsync(c, ct);
        if (picked.IsFailure) return picked.Error!;
        c.Players.Add(new CompetitionPlayer { UserId = userId, State = PlayerState.Joined, JoinedAt = now });
        c.Players.Add(new CompetitionPlayer { UserId = r.OpponentId, State = PlayerState.Invited, JoinedAt = now });
        c.NextEventAt = c.ExpiresAt;
        db.Competitions.Add(c);
        await db.SaveChangesAsync(ct);

        var dto = await BuildAsync(r.OpponentId, c.Id, ct);
        try
        {
            await notifier.ChallengeReceivedAsync(r.OpponentId, dto.Summary, ct);
            var from = await NameAsync(userId, ct);
            await push.SendAsync([r.OpponentId], new PushMessage("⚔️ New challenge", $"{from} challenged you to a {dto.Summary.LanguageName} {c.Level} quiz.",
                new Dictionary<string, string> { ["type"] = "competition", ["competitionId"] = c.Id.ToString() }), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Challenge notification failed");
        }
        return await BuildAsync(userId, c.Id, ct);
    }

    public async Task<Result<CompetitionDto>> AcceptAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            var me = c?.Players.FirstOrDefault(p => p.UserId == userId);
            if (c is null || me is null) return Error.NotFound("Competition");
            var now = clock.GetUtcNow();
            if (c.Status != CompetitionStatus.Pending || me.State != PlayerState.Invited)
                return Error.Conflict("not_pending", "This challenge isn't waiting for you any more.");
            if (c.ExpiresAt <= now) return Error.Conflict("challenge_expired", "This challenge has expired.");
            if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
            if (await RankedLimitAsync(userId, ct) is { } limit) return limit;

            me.State = PlayerState.Joined;
            me.JoinedAt = now;
            if (c.IsLive)
            {
                c.Status = CompetitionStatus.Scheduled;
                c.StartsAt = now.AddSeconds(O.CountdownSeconds);
                c.ExpiresAt = null;
            }
            else
            {
                // Your own time: both have 24 hours from now to play.
                c.Status = CompetitionStatus.Running;
                c.ExpiresAt = now.AddHours(O.ChallengeExpiryHours);
            }
            Bump(c);
            c.NextEventAt = NextEvent(c);
            await db.SaveChangesAsync(ct);
            await AdvanceCoreAsync(c, now, ct);
            await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
        }
        finally
        {
            gate.Release();
        }
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result> DeclineAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct);
        var me = c?.Players.FirstOrDefault(p => p.UserId == userId);
        if (c is null || me is null) return Error.NotFound("Competition");
        if (c.Status != CompetitionStatus.Pending || me.State != PlayerState.Invited) return Error.Conflict("not_pending", "Nothing to decline.");
        me.State = PlayerState.Declined;
        c.Status = CompetitionStatus.Declined;
        c.EndsAt = clock.GetUtcNow();
        c.NextEventAt = null;
        Bump(c);
        await db.SaveChangesAsync(ct);
        await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
        return Result.Success();
    }

    public async Task<Result<CompetitionDto>> QuickMatchAsync(Guid userId, QuickMatchRequest r, CancellationToken ct = default)
    {
        if (CompetitionRules.ValidateSettings(r.QuestionCount, r.SecondsPerQuestion) is { } bad) return Error.Validation("QuestionCount", bad);
        if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
        if (await RankedLimitAsync(userId, ct) is { } limit) return limit;
        var level = r.Level ?? await LevelOfAsync(userId, r.LanguageId, ct);
        if (level is null) return Error.Validation("Level", "Choose a level.");
        if (!await db.Languages.AnyAsync(l => l.Id == r.LanguageId, ct)) return Error.Validation("LanguageId", "Unknown language.");

        var now = clock.GetUtcNow();
        // Already searching? Return that search.
        var mine = await db.Competitions.AsNoTracking()
            .Where(c => c.Mode == CompetitionMode.QuickMatch && c.Status == CompetitionStatus.Waiting && c.CreatedBy == userId)
            .Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (mine is { } existing) return await BuildAsync(userId, existing, ct);

        var blocked = db.UserBlocks.Where(b => b.BlockerId == userId || b.BlockedId == userId).Select(b => b.BlockerId == userId ? b.BlockedId : b.BlockerId);
        var candidates = await db.Competitions
            .Where(c => c.Mode == CompetitionMode.QuickMatch && c.Status == CompetitionStatus.Waiting && c.LanguageId == r.LanguageId
                        && c.CreatedBy != userId && c.ExpiresAt > now && !blocked.Contains(c.CreatedBy))
            .OrderBy(c => c.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var candidate in candidates.Where(c => CompetitionRules.LevelsMatch(c.Level, level.Value)))
        {
            var gate = Lock(candidate.Id);
            await gate.WaitAsync(ct);
            try
            {
                var c = await LoadAsync(candidate.Id, ct);
                if (c is null || c.Status != CompetitionStatus.Waiting) continue;
                var picked = await PickQuestionsAsync(c, ct);
                if (picked.IsFailure) continue;
                c.Players.Add(new CompetitionPlayer { UserId = userId, State = PlayerState.Joined, JoinedAt = now });
                c.Status = CompetitionStatus.Scheduled;
                c.StartsAt = now.AddSeconds(O.CountdownSeconds);
                c.ExpiresAt = null;
                c.IsRanked = true;
                Bump(c);
                c.NextEventAt = NextEvent(c);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Someone else took this one: try the next.
                    DetachAll(c);
                    continue;
                }
                await AdvanceCoreAsync(c, now, ct);
                await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
                return await BuildAsync(userId, c.Id, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        // Nobody waiting: wait for the next learner, or play the bot after the search time.
        var waiting = new Competition
        {
            Mode = CompetitionMode.QuickMatch, Status = CompetitionStatus.Waiting, LanguageId = r.LanguageId, Level = level.Value,
            QuestionCount = r.QuestionCount, SecondsPerQuestion = r.SecondsPerQuestion, IsLive = true, IsRanked = true, CreatedBy = userId,
            CreatedAt = now, ExpiresAt = now.AddSeconds(O.QuickMatchWaitSeconds)
        };
        waiting.Players.Add(new CompetitionPlayer { UserId = userId, State = PlayerState.Joined, JoinedAt = now });
        waiting.NextEventAt = NextEvent(waiting);
        db.Competitions.Add(waiting);
        await db.SaveChangesAsync(ct);
        return await BuildAsync(userId, waiting.Id, ct);
    }

    public async Task<Result<CompetitionDto>> PlayBotAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            if (c is null || c.CreatedBy != userId || c.Mode != CompetitionMode.QuickMatch) return Error.NotFound("Competition");
            if (c.Status != CompetitionStatus.Waiting) return Error.Conflict("not_waiting", "An opponent was already found.");
            var picked = await PickQuestionsAsync(c, ct);
            if (picked.IsFailure) return picked.Error!;
            var now = clock.GetUtcNow();
            c.Players.Add(new CompetitionPlayer
            {
                IsBot = true, BotName = CompetitionRules.BotNames[c.Id % CompetitionRules.BotNames.Length], State = PlayerState.Joined, JoinedAt = now
            });
            // Practice: no rating and no win points (SRS 11.4).
            c.IsRanked = false;
            c.Status = CompetitionStatus.Scheduled;
            c.StartsAt = now.AddSeconds(O.CountdownSeconds);
            c.ExpiresAt = null;
            Bump(c);
            c.NextEventAt = NextEvent(c);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Error.Conflict("not_waiting", "An opponent was just found.");
            }
            await AdvanceCoreAsync(c, now, ct);
        }
        finally
        {
            gate.Release();
        }
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result> CancelAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            if (c is null || c.CreatedBy != userId) return Error.NotFound("Competition");
            if (c.Status is not (CompetitionStatus.Pending or CompetitionStatus.Waiting))
                return Error.Conflict("already_started", "It has already started. Leave the match instead.");
            c.Status = CompetitionStatus.Cancelled;
            c.EndsAt = clock.GetUtcNow();
            c.NextEventAt = null;
            Bump(c);
            await db.SaveChangesAsync(ct);
            await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("already_started", "An opponent was just found.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<CompetitionDto>> GroupBattleAsync(Guid userId, int conversationId, GroupBattleRequest r, CancellationToken ct = default)
    {
        if (CompetitionRules.ValidateSettings(r.QuestionCount, r.SecondsPerQuestion) is { } bad) return Error.Validation("QuestionCount", bad);
        if (r.StartInSeconds is < 0 or > 600) return Error.Validation("StartInSeconds", "Start within 10 minutes.");
        var member = await db.ConversationMembers.AsNoTracking().Include(m => m.Conversation)
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId && m.IsActive && !m.IsPending, ct);
        if (member is null || member.Conversation.Type != ConversationType.Group || !member.Conversation.IsActive) return Error.NotFound("Group");
        // FR-C28: group admins start battles.
        if (member.Role == MemberRole.Member) return Error.Forbidden("not_group_admin", "Only the group owner and admins can start a group battle.");
        if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
        var members = await db.ConversationMembers.CountAsync(m => m.ConversationId == conversationId && m.IsActive && !m.IsPending, ct);
        if (members < O.GroupBattleMinPlayers) return Error.Validation("ConversationId", $"Group battles need at least {O.GroupBattleMinPlayers} members.");
        if (await db.Competitions.AnyAsync(c => c.ConversationId == conversationId && c.Mode == CompetitionMode.GroupBattle
                                                && (c.Status == CompetitionStatus.Waiting || c.Status == CompetitionStatus.Running
                                                    || c.Status == CompetitionStatus.Scheduled), ct))
            return Error.Conflict("battle_running", "This group already has a battle going.");

        var languageId = r.LanguageId ?? member.Conversation.LanguageId;
        if (languageId is null) return Error.Validation("LanguageId", "Choose the language for the battle (the group has none set).");
        var level = r.Level ?? await LevelOfAsync(userId, languageId.Value, ct) ?? CefrLevel.A1;

        var now = clock.GetUtcNow();
        var c = new Competition
        {
            Mode = CompetitionMode.GroupBattle, Status = CompetitionStatus.Waiting, LanguageId = languageId.Value, Level = level, ModuleId = r.ModuleId,
            QuestionCount = r.QuestionCount, SecondsPerQuestion = r.SecondsPerQuestion, IsLive = true, ConversationId = conversationId,
            CreatedBy = userId, CreatedAt = now, StartsAt = now.AddSeconds(Math.Max(10, r.StartInSeconds))
        };
        var picked = await PickQuestionsAsync(c, ct);
        if (picked.IsFailure) return picked.Error!;
        c.Players.Add(new CompetitionPlayer { UserId = userId, State = PlayerState.Joined, JoinedAt = now });
        c.NextEventAt = NextEvent(c);
        db.Competitions.Add(c);
        await db.SaveChangesAsync(ct);

        var language = await db.Languages.Where(l => l.Id == c.LanguageId).Select(l => l.Name).FirstAsync(ct);
        var card = await chat.PostCompetitionCardAsync(userId, conversationId, c.Id,
            $"⚔️ Group battle · {language} {c.Level} · {c.QuestionCount} questions — starts {c.StartsAt:HH:mm} UTC. Tap to join!", ct);
        if (card.IsFailure) logger.LogInformation("Battle card not posted: {Error}", card.Error!.Message);
        return await BuildAsync(userId, c.Id, ct);
    }

    public async Task<Result<CompetitionDto>> JoinAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            if (c is null || c.Mode != CompetitionMode.GroupBattle || c.ConversationId is null) return Error.NotFound("Competition");
            if (!await chat.IsMemberAsync(userId, c.ConversationId.Value, ct)) return Error.NotFound("Competition");
            if (c.Players.Any(p => p.UserId == userId)) return await BuildAsync(userId, id, ct);
            if (c.Status != CompetitionStatus.Waiting) return Error.Conflict("already_started", "This battle has already started.");
            if (c.Players.Count >= O.GroupBattleMaxPlayers) return Error.Conflict("battle_full", $"Group battles have up to {O.GroupBattleMaxPlayers} players.");
            if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
            c.Players.Add(new CompetitionPlayer { UserId = userId, State = PlayerState.Joined, JoinedAt = clock.GetUtcNow() });
            Bump(c);
            await db.SaveChangesAsync(ct);
            await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
        }
        finally
        {
            gate.Release();
        }
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result<CompetitionDto>> StartNowAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            if (c is null || c.Mode != CompetitionMode.GroupBattle || c.ConversationId is null) return Error.NotFound("Competition");
            if (!await IsGroupAdminAsync(userId, c.ConversationId.Value, ct) && c.CreatedBy != userId)
                return Error.Forbidden("not_group_admin", "Only group admins can start the battle.");
            if (c.Status != CompetitionStatus.Waiting) return Error.Conflict("already_started", "This battle has already started.");
            if (c.Players.Count < O.GroupBattleMinPlayers)
                return Error.Conflict("not_enough_players", $"Wait for at least {O.GroupBattleMinPlayers} players to join.");
            var now = clock.GetUtcNow();
            c.Status = CompetitionStatus.Scheduled;
            c.StartsAt = now.AddSeconds(O.CountdownSeconds);
            Bump(c);
            c.NextEventAt = NextEvent(c);
            await db.SaveChangesAsync(ct);
            await AdvanceCoreAsync(c, now, ct);
            await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
        }
        finally
        {
            gate.Release();
        }
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result<CompetitionDto>> PlayAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            var me = c?.Players.FirstOrDefault(p => p.UserId == userId);
            if (c is null || me is null) return Error.NotFound("Competition");
            if (c.IsLive) return Error.Conflict("live_match", "This is a live match: it starts for both players together.");
            if (c.Status is not (CompetitionStatus.Pending or CompetitionStatus.Running) || me.State != PlayerState.Joined)
                return Error.Conflict("cannot_play", me.State == PlayerState.Invited ? "Accept the challenge first." : "You've already played this one.");
            if (await RestrictionAsync(userId, ct) is { } restricted) return restricted;
            var now = clock.GetUtcNow();
            me.State = PlayerState.Playing;
            me.StartedAt = now;
            OpenPlayerQuestion(c, me, 0, now.AddSeconds(Math.Min(3, O.CountdownSeconds)));
            Bump(c);
            c.NextEventAt = NextEvent(c);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result<int>> CreateRoundAsync(Tournament t, int round, IReadOnlyList<Guid> userIds, DateTimeOffset startsAt, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var c = new Competition
        {
            Mode = CompetitionMode.Tournament, Status = CompetitionStatus.Scheduled, LanguageId = t.LanguageId, Level = t.Level, ModuleId = t.ModuleId,
            QuestionCount = t.QuestionCount, SecondsPerQuestion = t.SecondsPerQuestion, IsLive = true, TournamentId = t.Id, Round = round,
            CreatedBy = userIds[0], CreatedAt = now, StartsAt = startsAt
        };
        var picked = await PickQuestionsAsync(c, ct);
        if (picked.IsFailure) return picked.Error!;
        foreach (var u in userIds) c.Players.Add(new CompetitionPlayer { UserId = u, State = PlayerState.Joined, JoinedAt = now });
        c.NextEventAt = NextEvent(c);
        db.Competitions.Add(c);
        await db.SaveChangesAsync(ct);
        await AdvanceAsync(c.Id, ct);
        await notifier.MatchUpdatedAsync(userIds.ToList(), c.Id, ct);
        try
        {
            await push.SendAsync(userIds.ToList(), new PushMessage($"🏆 {t.Title}", $"Round {round} is starting — open the app to play!",
                new Dictionary<string, string> { ["type"] = "competition", ["competitionId"] = c.Id.ToString() }), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Round push failed");
        }
        return c.Id;
    }

    // ================================================================== playing

    public async Task<Result<CompetitionDto>> GetAsync(Guid userId, int id, CancellationToken ct = default)
    {
        await AdvanceAsync(id, ct);
        var c = await db.Competitions.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.ConversationId, x.Mode, IsPlayer = x.Players.Any(p => p.UserId == userId) }).FirstOrDefaultAsync(ct);
        if (c is null) return Error.NotFound("Competition");
        // Group battle lobbies are visible to group members; everything else only to players.
        if (!c.IsPlayer && !(c.Mode == CompetitionMode.GroupBattle && c.ConversationId is { } conv && await chat.IsMemberAsync(userId, conv, ct)))
            return Error.NotFound("Competition");
        return await BuildAsync(userId, id, ct);
    }

    public async Task<Result<AnswerAckDto>> SubmitAnswerAsync(Guid userId, int id, SubmitAnswerRequest r, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var c = await LoadAsync(id, ct);
            var me = c?.Players.FirstOrDefault(p => p.UserId == userId);
            if (c is null || me is null) return Error.NotFound("Competition");
            // Close anything that is already over before judging this answer.
            await AdvanceCoreAsync(c, now, ct);
            if (me.State != PlayerState.Playing) return Ack(false, "not_playing", "You're not playing this one right now.");

            int index;
            DateTimeOffset opens, closes;
            if (c.IsLive)
            {
                if (c.Status != CompetitionStatus.Running) return Ack(false, "not_running", "The match isn't running.");
                (index, opens, closes) = (c.CurrentIndex, c.QuestionOpensAt!.Value, c.QuestionClosesAt!.Value);
            }
            else
            {
                (index, opens, closes) = (me.CurrentIndex, me.QuestionOpensAt!.Value, me.QuestionClosesAt!.Value);
            }
            if (r.QuestionIndex != index) return Ack(false, "wrong_question", "That question is closed.");
            if (now < opens) return Ack(false, "not_open", "Wait for the question to open.");
            // FR-Q05: the server keeps time; a short allowance covers network delay.
            if (now > closes.AddMilliseconds(O.GraceMilliseconds)) return Ack(false, "too_late", "Time's up for this question.");
            if (await db.CompetitionAnswers.AnyAsync(a => a.PlayerId == me.Id && a.QuestionIndex == index, ct))
                return Ack(false, "already_answered", "You've already answered.");

            var question = await QuestionAsync(c, index, ct);
            var input = new AnswerInput(r.Text, r.OptionId is { } o ? [o] : [], new Dictionary<int, string>(), null);
            var grade = AnswerGrader.Grade(question, input);
            var ms = (int)Math.Clamp((now - opens).TotalMilliseconds, 0, c.SecondsPerQuestion * 1000);
            var correct = grade.IsCorrect == true;
            db.CompetitionAnswers.Add(new CompetitionAnswer
            {
                CompetitionId = c.Id, PlayerId = me.Id, QuestionIndex = index, QuestionId = question.Id, OptionId = r.OptionId,
                AnswerText = r.Text is { Length: > 500 } t ? t[..500] : r.Text, IsCorrect = correct, AnswerMs = ms,
                Score = CompetitionRules.Score(correct, ms, c.SecondsPerQuestion), AnsweredAt = now
            });
            await db.SaveChangesAsync(ct);

            var answeredBefore = await db.CompetitionAnswers.CountAsync(a => a.PlayerId == me.Id, ct);
            await notifier.OpponentAnsweredAsync(Humans(c).Where(u => u != userId).ToList(),
                new OpponentAnsweredDto(c.Id, me.Id, userId, index, answeredBefore), ct);

            // Everyone answered (or it's my own run): close at once.
            var results = await AdvanceCoreAsync(c, now, ct, closeOwnRunOf: c.IsLive ? null : me);
            var result = results.FirstOrDefault(x => x.Index == index);
            var finished = me.State is PlayerState.Finished || c.Status == CompetitionStatus.Finished;
            CompetitionQuestionDto? next = null;
            if (!finished && result is not null)
                next = await CurrentQuestionAsync(c, me, ct);
            return new AnswerAckDto(true, null, null, result, next, finished);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result> LeaveAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            var me = c?.Players.FirstOrDefault(p => p.UserId == userId);
            if (c is null || me is null) return Error.NotFound("Competition");
            var now = clock.GetUtcNow();
            if (c.Status == CompetitionStatus.Waiting && c.Mode == CompetitionMode.GroupBattle && c.CreatedBy != userId)
            {
                c.Players.Remove(me);
                db.CompetitionPlayers.Remove(me);
            }
            else if (me.State is PlayerState.Joined or PlayerState.Playing)
            {
                // FR-Q06: leaving early is a loss.
                me.LeftEarly = true;
                me.State = PlayerState.Left;
            }
            else return Result.Success();
            Bump(c);
            await db.SaveChangesAsync(ct);
            await AdvanceCoreAsync(c, now, ct);
            await notifier.MatchUpdatedAsync(Humans(c), c.Id, ct);
            return Result.Success();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<CompetitionResultDto>> ResultAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var dto = await GetAsync(userId, id, ct);
        if (dto.IsFailure) return dto.Error!;
        var me = await db.CompetitionPlayers.AsNoTracking().FirstOrDefaultAsync(p => p.CompetitionId == id && p.UserId == userId, ct);
        var questions = await db.CompetitionQuestions.AsNoTracking().Where(q => q.CompetitionId == id).OrderBy(q => q.SortOrder)
            .Select(q => new { q.SortOrder, q.QuestionId }).ToListAsync(ct);
        var ids = questions.Select(q => q.QuestionId).ToList();
        var bank = await db.Questions.AsNoTracking().Include(q => q.Options).Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, ct);
        var mine = me is null ? [] : await db.CompetitionAnswers.AsNoTracking().Where(a => a.PlayerId == me.Id).ToListAsync(ct);
        var status = dto.Value.Summary.Status;
        var myIndex = dto.Value.CurrentQuestion?.Index ?? int.MaxValue;

        // Answers are only shown for questions that are closed for me (FR-Q05).
        var review = questions
            .Where(q => status == CompetitionStatus.Finished || q.SortOrder < myIndex)
            .Select(q =>
            {
                var b = bank[q.QuestionId];
                var a = mine.FirstOrDefault(x => x.QuestionIndex == q.SortOrder);
                var my = a is null ? null : a.OptionId is { } o ? b.Options.FirstOrDefault(x => x.Id == o)?.Text : a.AnswerText;
                return new ReviewItemDto(q.SortOrder, b.Stem, my, a?.IsCorrect == true, AnswerGrader.CorrectAnswerText(b), a?.Score ?? 0, a?.AnswerMs);
            }).ToList();
        var canRematch = status == CompetitionStatus.Finished && dto.Value.Summary.Mode is CompetitionMode.Challenge or CompetitionMode.QuickMatch
                         && dto.Value.Players.Count(p => !p.IsBot) == 2;
        return new CompetitionResultDto(dto.Value, review, canRematch);
    }

    public async Task<Result<CompetitionDto>> RematchAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var c = await db.Competitions.AsNoTracking().Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || c.Players.All(p => p.UserId != userId)) return Error.NotFound("Competition");
        var other = c.Players.FirstOrDefault(p => p.UserId != userId && !p.IsBot)?.UserId;
        if (c.Status != CompetitionStatus.Finished || other is null || c.Mode is not (CompetitionMode.Challenge or CompetitionMode.QuickMatch))
            return Error.Conflict("no_rematch", "Rematches are for finished 1 v 1 matches against a person.");
        var result = await ChallengeAsync(userId, new ChallengeRequest(other.Value, c.LanguageId, c.Level, c.ModuleId, c.QuestionCount, c.SecondsPerQuestion, true), ct);
        if (result.IsSuccess)
        {
            var created = await db.Competitions.FirstAsync(x => x.Id == result.Value.Summary.Id, ct);
            created.RematchOfId = id;
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    public async Task<Result> ShareAsync(Guid userId, int id, int conversationId, CancellationToken ct = default)
    {
        var dto = await GetAsync(userId, id, ct);
        if (dto.IsFailure) return dto.Error!;
        var c = dto.Value;
        if (c.Summary.Status != CompetitionStatus.Finished) return Error.Conflict("not_finished", "Share the result when the match is over.");
        var ranked = c.Players.Where(p => p.Rank is not null).OrderBy(p => p.Rank).Take(3).ToList();
        var line = c.Players.Count == 2
            ? $"{ranked[0].DisplayName} {ranked[0].Score:N0} – {ranked[^1].Score:N0} {ranked[^1].DisplayName}" +
              (ranked[0].Result == CompetitionResult.Draw ? " · draw" : $" · {ranked[0].DisplayName} won")
            : string.Join(" · ", ranked.Select(p => $"{p.Rank}. {p.DisplayName} {p.Score:N0}"));
        var text = $"🏆 {c.Summary.Title} · {c.Summary.LanguageName} {c.Summary.Level} — {line}";
        var card = await chat.PostCompetitionCardAsync(userId, conversationId, id, text, ct);
        return card.IsSuccess ? Result.Success() : card.Error!;
    }

    // ================================================================== lists and stats (FR-Q08)

    public async Task<IReadOnlyList<CompetitionSummaryDto>> ActiveAsync(Guid userId, CancellationToken ct = default)
    {
        CompetitionStatus[] open = [CompetitionStatus.Pending, CompetitionStatus.Waiting, CompetitionStatus.Scheduled, CompetitionStatus.Running];
        var ids = await db.CompetitionPlayers.AsNoTracking()
            .Where(p => p.UserId == userId && open.Contains(p.Competition.Status) && p.State != PlayerState.Declined)
            .OrderByDescending(p => p.CompetitionId).Select(p => p.CompetitionId).Take(30).ToListAsync(ct);
        foreach (var id in ids) await AdvanceAsync(id, ct);
        var list = new List<CompetitionSummaryDto>();
        foreach (var id in ids)
        {
            var s = (await BuildAsync(userId, id, ct)).Summary;
            if (open.Contains(s.Status)) list.Add(s);
        }
        return list;
    }

    public async Task<IReadOnlyList<CompetitionSummaryDto>> HistoryAsync(Guid userId, int page, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var ids = await db.CompetitionPlayers.AsNoTracking()
            .Where(p => p.UserId == userId && p.Competition.Status == CompetitionStatus.Finished)
            .OrderByDescending(p => p.Competition.EndsAt).Skip((page - 1) * 20).Take(20).Select(p => p.CompetitionId).ToListAsync(ct);
        var list = new List<CompetitionSummaryDto>();
        foreach (var id in ids) list.Add((await BuildAsync(userId, id, ct)).Summary);
        return list;
    }

    public async Task<CompetitionStatsDto> StatsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.CompetitionRatings.AsNoTracking().Where(r => r.UserId == userId)
            .Join(db.Languages, r => r.LanguageId, l => l.Id, (r, l) => new { r, l.Name }).ToListAsync(ct);
        var ratings = rows.OrderByDescending(x => x.r.Played)
            .Select(x => new RatingDto(x.r.LanguageId, x.Name, x.r.Rating, x.r.Played, x.r.Won, x.r.Drawn, x.r.Lost,
                CompetitionRules.WinRate(x.r.Played, x.r.Won), x.r.CurrentStreak, x.r.BestStreak)).ToList();
        var played = ratings.Sum(r => r.Played);
        var won = ratings.Sum(r => r.Won);
        var perDay = await settings.GetIntAsync(SettingKeys.CompetitionMaxRankedPerDay, 20, ct);
        return new CompetitionStatsDto(played, won, CompetitionRules.WinRate(played, won), ratings.Count == 0 ? 0 : ratings.Max(r => r.BestStreak),
            await RankedTodayAsync(userId, ct), perDay, ratings);
    }

    // ================================================================== the clock

    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        CompetitionStatus[] open = [CompetitionStatus.Pending, CompetitionStatus.Waiting, CompetitionStatus.Scheduled, CompetitionStatus.Running];
        var due = await db.Competitions.AsNoTracking().Where(c => open.Contains(c.Status) && c.NextEventAt != null && c.NextEventAt <= now)
            .OrderBy(c => c.NextEventAt).Select(c => c.Id).Take(100).ToListAsync(ct);
        foreach (var id in due)
        {
            try
            {
                await AdvanceAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Competition {Id} could not advance", id);
            }
        }
        return due.Count;
    }

    public async Task AdvanceAsync(int id, CancellationToken ct = default)
    {
        var gate = Lock(id);
        await gate.WaitAsync(ct);
        try
        {
            var c = await LoadAsync(id, ct);
            if (c is null) return;
            await AdvanceCoreAsync(c, clock.GetUtcNow(), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The engine. Moves a competition forward to <paramref name="now"/>: expiries, the countdown, closing questions on time or when everyone
    /// has answered, opening the next one, and the final result. Safe to call any number of times. Returns the question results it produced.
    /// </summary>
    private async Task<List<QuestionResultDto>> AdvanceCoreAsync(Competition c, DateTimeOffset now, CancellationToken ct, CompetitionPlayer? closeOwnRunOf = null)
    {
        var results = new List<QuestionResultDto>();
        var started = new List<CompetitionQuestionDto>();
        var ended = false;
        var updated = false;

        switch (c.Status)
        {
            case CompetitionStatus.Pending when c.ExpiresAt <= now:
                c.Status = CompetitionStatus.Expired;
                c.EndsAt = now;
                updated = true;
                break;

            case CompetitionStatus.Waiting when c.Mode == CompetitionMode.QuickMatch && c.ExpiresAt?.AddMinutes(5) <= now:
                c.Status = CompetitionStatus.Cancelled;
                c.EndsAt = now;
                updated = true;
                break;

            case CompetitionStatus.Waiting when c.Mode == CompetitionMode.GroupBattle && c.StartsAt <= now:
                if (c.Players.Count >= O.GroupBattleMinPlayers)
                {
                    c.Status = CompetitionStatus.Scheduled;
                    c.StartsAt = now;
                }
                else
                {
                    c.Status = CompetitionStatus.Cancelled;
                    c.EndsAt = now;
                }
                updated = true;
                break;
        }

        if (c.Status == CompetitionStatus.Scheduled && c.StartsAt <= now)
        {
            c.Status = CompetitionStatus.Running;
            foreach (var p in c.Players.Where(p => p.State == PlayerState.Joined)) p.State = PlayerState.Playing;
            started.Add(await OpenLiveQuestionAsync(c, 0, c.StartsAt!.Value, ct));
            updated = true;
        }

        if (c.Status == CompetitionStatus.Running && c.IsLive)
        {
            while (c.Status == CompetitionStatus.Running && c.CurrentIndex >= 0)
            {
                var index = c.CurrentIndex;
                var closes = c.QuestionClosesAt!.Value;
                var active = c.Players.Where(p => p.State == PlayerState.Playing).ToList();
                var answered = await db.CompetitionAnswers.Where(a => a.CompetitionId == c.Id && a.QuestionIndex == index).Select(a => a.PlayerId).ToListAsync(ct);
                var everyone = active.Count == 0 || active.All(p => answered.Contains(p.Id));
                if (now < closes && !(everyone && now >= c.QuestionOpensAt)) break;

                var closeAt = now < closes ? now : closes;
                results.Add(await CloseQuestionAsync(c, active, index, ct));
                if (index >= c.QuestionCount - 1 || active.All(p => p.IsBot) || active.Count == 0)
                {
                    await FinalizeAsync(c, closeAt, ct);
                    ended = true;
                    break;
                }
                started.Add(await OpenLiveQuestionAsync(c, index + 1, closeAt.AddSeconds(O.ResultPauseSeconds), ct));
            }
        }
        else if (c.Status is CompetitionStatus.Running or CompetitionStatus.Pending && !c.IsLive)
        {
            foreach (var p in c.Players.Where(p => p.State == PlayerState.Playing).ToList())
            {
                while (p.State == PlayerState.Playing)
                {
                    var index = p.CurrentIndex;
                    var closes = p.QuestionClosesAt!.Value;
                    var hasAnswer = await db.CompetitionAnswers.AnyAsync(a => a.PlayerId == p.Id && a.QuestionIndex == index, ct);
                    if (now < closes && !(hasAnswer && p == closeOwnRunOf)) break;
                    var closeAt = now < closes ? now : closes;
                    results.Add(await CloseQuestionAsync(c, [p], index, ct));
                    if (index >= c.QuestionCount - 1) p.State = PlayerState.Finished;
                    else OpenPlayerQuestion(c, p, index + 1, closeAt.AddSeconds(O.ResultPauseSeconds));
                    updated = true;
                }
            }
            // Out of time to play: whoever didn't finish lost.
            if (c.Status == CompetitionStatus.Running && c.ExpiresAt <= now)
                foreach (var p in c.Players.Where(p => p.State is PlayerState.Joined or PlayerState.Playing or PlayerState.Invited))
                {
                    p.State = PlayerState.Left;
                    p.LeftEarly = true;
                }
            if (c.Status == CompetitionStatus.Running && c.Players.All(p => p.State is PlayerState.Finished or PlayerState.Left))
            {
                await FinalizeAsync(c, now, ct);
                ended = true;
            }
        }

        // Everyone left a live match before it ended.
        if (!ended && c.Status == CompetitionStatus.Running && c.IsLive && c.Players.Where(p => !p.IsBot).All(p => p.State == PlayerState.Left))
        {
            await FinalizeAsync(c, now, ct);
            ended = true;
        }

        var next = NextEvent(c);
        if (updated || results.Count > 0 || started.Count > 0 || ended || next != c.NextEventAt)
        {
            c.NextEventAt = next;
            Bump(c);
            await db.SaveChangesAsync(ct);
        }

        var humans = Humans(c);
        try
        {
            if (c.IsLive)
            {
                foreach (var r in results) await notifier.QuestionResultAsync(humans, r, ct);
                foreach (var q in started.Where(q => q.Index < c.QuestionCount && c.Status == CompetitionStatus.Running && q.Index == c.CurrentIndex))
                    await notifier.QuestionStartedAsync(humans, q with { ServerNow = clock.GetUtcNow() }, ct);
            }
            if (ended) await notifier.MatchEndedAsync(humans, c.Id, ct);
            else if (updated) await notifier.MatchUpdatedAsync(humans, c.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Competition events failed");
        }
        return results;
    }

    private async Task<CompetitionQuestionDto> OpenLiveQuestionAsync(Competition c, int index, DateTimeOffset opens, CancellationToken ct)
    {
        c.CurrentIndex = index;
        c.QuestionOpensAt = opens;
        c.QuestionClosesAt = opens.AddSeconds(c.SecondsPerQuestion);
        // Bots answer the moment the question is set (their time is simulated).
        foreach (var bot in c.Players.Where(p => p.IsBot && p.State == PlayerState.Playing))
        {
            var (correct, ms) = CompetitionRules.BotAnswer(c.Id, index, c.Level, c.SecondsPerQuestion);
            var q = await QuestionAsync(c, index, ct);
            db.CompetitionAnswers.Add(new CompetitionAnswer
            {
                CompetitionId = c.Id, PlayerId = bot.Id, QuestionIndex = index, QuestionId = q.Id, IsCorrect = correct, AnswerMs = ms,
                AnswerText = correct ? AnswerGrader.CorrectAnswerText(q) : null,
                Score = CompetitionRules.Score(correct, ms, c.SecondsPerQuestion), AnsweredAt = opens.AddMilliseconds(ms)
            });
        }
        if (c.Players.Any(p => p.IsBot)) await db.SaveChangesAsync(ct);
        return await QuestionDtoAsync(c, index, opens, c.QuestionClosesAt.Value, ct);
    }

    private static void OpenPlayerQuestion(Competition c, CompetitionPlayer p, int index, DateTimeOffset opens)
    {
        p.CurrentIndex = index;
        p.QuestionOpensAt = opens;
        p.QuestionClosesAt = opens.AddSeconds(c.SecondsPerQuestion);
    }

    /// <summary>Adds question <paramref name="index"/> to the players' totals and builds the result screen.</summary>
    private async Task<QuestionResultDto> CloseQuestionAsync(Competition c, IReadOnlyList<CompetitionPlayer> players, int index, CancellationToken ct)
    {
        var answers = await db.CompetitionAnswers.AsNoTracking().Where(a => a.CompetitionId == c.Id && a.QuestionIndex == index).ToListAsync(ct);
        var full = c.SecondsPerQuestion * 1000;
        var scores = new Dictionary<int, (bool Answered, bool Correct, int Score)>();
        foreach (var p in players)
        {
            var a = answers.FirstOrDefault(x => x.PlayerId == p.Id);
            if (a is null)
            {
                p.TotalAnswerMs += full;
                scores[p.Id] = (false, false, 0);
                continue;
            }
            p.Answered++;
            if (a.IsCorrect) p.Correct++;
            p.Score += a.Score;
            p.TotalAnswerMs += a.AnswerMs;
            scores[p.Id] = (true, a.IsCorrect, a.Score);
        }

        var question = await QuestionAsync(c, index, ct);
        var standings = c.Players.Where(p => p.State != PlayerState.Declined && p.State != PlayerState.Invited).ToList();
        var ranks = CompetitionRules.Rank(standings.Select(p => new Standing(p.Id, p.Score, p.TotalAnswerMs, p.LeftEarly)));
        var board = standings.OrderBy(p => ranks[p.Id])
            .Select(p => new PlayerScoreDto(p.Id, p.UserId, PlayerName(p), scores.GetValueOrDefault(p.Id).Answered, scores.GetValueOrDefault(p.Id).Correct,
                scores.GetValueOrDefault(p.Id).Score, p.Score, ranks[p.Id]))
            .ToList();
        // Your-own-time runs: other players' live scores aren't shown until the end.
        if (!c.IsLive) board = board.Where(b => players.Any(p => p.Id == b.PlayerId)).ToList();
        return new QuestionResultDto(c.Id, index, AnswerGrader.CorrectAnswerText(question), question.Explanation, board);
    }

    /// <summary>Ranks, results, ratings and points (FR-Q04, FR-Q06, FR-Q08, FR-Q09, FR-Q10, BR-29).</summary>
    private async Task FinalizeAsync(Competition c, DateTimeOffset at, CancellationToken ct)
    {
        c.Status = CompetitionStatus.Finished;
        c.EndsAt = at;
        c.NextEventAt = null;
        var players = c.Players.Where(p => p.State is not (PlayerState.Declined or PlayerState.Invited)).ToList();
        foreach (var p in players.Where(p => p.State == PlayerState.Playing)) p.State = PlayerState.Finished;
        var ranks = CompetitionRules.Rank(players.Select(p => new Standing(p.Id, p.Score, p.TotalAnswerMs, p.LeftEarly)));
        var atOne = ranks.Values.Count(r => r == 1);
        foreach (var p in players)
        {
            p.Rank = ranks[p.Id];
            p.Result = CompetitionRules.Result(p.Rank.Value, atOne, p.LeftEarly);
        }
        await db.SaveChangesAsync(ct);

        var humans = players.Where(p => !p.IsBot && p.UserId is not null).ToList();
        var vsBot = players.Any(p => p.IsBot);
        var reference = c.Id.ToString();

        // Ratings: human 1 v 1 only (FR-Q08).
        if (c.IsRanked && !vsBot && humans.Count == 2 && c.Mode is CompetitionMode.Challenge or CompetitionMode.QuickMatch)
        {
            var a = await RatingAsync(humans[0].UserId!.Value, c.LanguageId, ct);
            var b = await RatingAsync(humans[1].UserId!.Value, c.LanguageId, ct);
            humans[0].RatingBefore = a.Rating;
            humans[1].RatingBefore = b.Rating;
            (a.Rating, b.Rating) = CompetitionRules.Elo(a.Rating, b.Rating, humans[0].Result!.Value);
            humans[0].RatingAfter = a.Rating;
            humans[1].RatingAfter = b.Rating;
        }

        foreach (var p in humans)
        {
            var userId = p.UserId!.Value;
            if (!vsBot && c.Mode != CompetitionMode.Tournament)
            {
                var stats = await RatingAsync(userId, c.LanguageId, ct);
                stats.Played++;
                switch (p.Result)
                {
                    case CompetitionResult.Win: stats.Won++; break;
                    case CompetitionResult.Draw: stats.Drawn++; break;
                    default: stats.Lost++; break;
                }
                (stats.CurrentStreak, stats.BestStreak) = CompetitionRules.Streak(stats.CurrentStreak, stats.BestStreak, p.Result!.Value);
                stats.UpdatedAt = at;
            }

            if (p.LeftEarly) continue;
            if (c.Mode == CompetitionMode.Tournament) continue; // prizes are paid by the tournament
            if (vsBot)
                p.PointsAwarded = await points.AwardAsync(userId, c.LanguageId, PointAction.PracticeSession, "competition", reference, ct: ct);
            else if (c.Mode == CompetitionMode.GroupBattle)
                p.PointsAwarded = p.Rank <= 3
                    ? await GroupBonusAsync(userId, c, p.Rank!.Value, ct)
                    : await points.AwardAsync(userId, c.LanguageId, PointAction.CompetitionLoss, "competition", reference, ct: ct);
            else if (p.Result == CompetitionResult.Win)
                p.PointsAwarded = await WinsCountAsync(c, p, ct)
                    ? await points.AwardAsync(userId, c.LanguageId, PointAction.CompetitionWin, "competition", reference, ct: ct)
                    : 0;
            else
                p.PointsAwarded = await points.AwardAsync(userId, c.LanguageId,
                    p.Result == CompetitionResult.Draw ? PointAction.CompetitionDraw : PointAction.CompetitionLoss, "competition", reference, ct: ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> GroupBonusAsync(Guid userId, Competition c, int rank, CancellationToken ct)
    {
        var first = await db.PointRules.Where(r => r.Action == PointAction.GroupBattleBonus).Select(r => r.Points).FirstOrDefaultAsync(ct);
        var bonus = CompetitionRules.GroupBonus(rank, first > 0 ? first : 30);
        return await points.AwardAsync(userId, c.LanguageId, PointAction.GroupBattleBonus, "competition", c.Id.ToString(), bonus, ct);
    }

    /// <summary>BR-29: win points against the same opponent at most N times a day.</summary>
    private async Task<bool> WinsCountAsync(Competition c, CompetitionPlayer winner, CancellationToken ct)
    {
        var opponent = c.Players.FirstOrDefault(p => p.Id != winner.Id && !p.IsBot)?.UserId;
        if (opponent is null) return true;
        var max = await settings.GetIntAsync(SettingKeys.CompetitionMaxWinsVsSameOpponent, 3, ct);
        var dayStart = await DayStartAsync(winner.UserId!.Value, ct);
        var wins = await db.CompetitionPlayers.CountAsync(p => p.UserId == winner.UserId && p.CompetitionId != c.Id && p.Result == CompetitionResult.Win
                                                               && p.PointsAwarded > 0 && p.Competition.EndsAt >= dayStart
                                                               && p.Competition.Players.Any(o => o.UserId == opponent), ct);
        return wins < max;
    }

    private async Task<CompetitionRating> RatingAsync(Guid userId, int languageId, CancellationToken ct)
    {
        var local = db.CompetitionRatings.Local.FirstOrDefault(r => r.UserId == userId && r.LanguageId == languageId);
        if (local is not null) return local;
        var row = await db.CompetitionRatings.FirstOrDefaultAsync(r => r.UserId == userId && r.LanguageId == languageId, ct);
        if (row is null)
        {
            row = new CompetitionRating { UserId = userId, LanguageId = languageId, Rating = CompetitionRules.StartRating, UpdatedAt = clock.GetUtcNow() };
            db.CompetitionRatings.Add(row);
        }
        return row;
    }

    private static DateTimeOffset? NextEvent(Competition c) => c.Status switch
    {
        CompetitionStatus.Pending when !c.IsLive => Min(c.ExpiresAt, c.Players.Where(p => p.State == PlayerState.Playing).Min(p => p.QuestionClosesAt)),
        CompetitionStatus.Pending => c.ExpiresAt,
        CompetitionStatus.Waiting => c.Mode == CompetitionMode.QuickMatch ? c.ExpiresAt?.AddMinutes(5) : c.StartsAt,
        CompetitionStatus.Scheduled => c.StartsAt,
        CompetitionStatus.Running when c.IsLive => c.QuestionClosesAt,
        CompetitionStatus.Running => Min(c.ExpiresAt, c.Players.Where(p => p.State == PlayerState.Playing).Min(p => p.QuestionClosesAt)),
        _ => null
    };

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    // ================================================================== building DTOs

    private async Task<CompetitionDto> BuildAsync(Guid userId, int id, CancellationToken ct)
    {
        var c = await db.Competitions.AsNoTracking()
            .Include(x => x.Players).ThenInclude(p => p.User)
            .Include(x => x.Language)
            .FirstAsync(x => x.Id == id, ct);
        var now = clock.GetUtcNow();
        var me = c.Players.FirstOrDefault(p => p.UserId == userId);
        var answered = await db.CompetitionAnswers.AsNoTracking().Where(a => a.CompetitionId == id)
            .GroupBy(a => a.PlayerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        CompetitionQuestionDto? current = null;
        var iAnswered = false;
        if (me is not null && me.State == PlayerState.Playing)
        {
            current = await CurrentQuestionAsync(c, me, ct);
            if (current is not null)
                iAnswered = await db.CompetitionAnswers.AnyAsync(a => a.PlayerId == me.Id && a.QuestionIndex == current.Index, ct);
        }

        var finished = c.Status == CompetitionStatus.Finished;
        var players = c.Players.Where(p => p.State != PlayerState.Declined)
            .OrderBy(p => p.Rank ?? int.MaxValue).ThenByDescending(p => p.Score).ThenBy(p => p.JoinedAt)
            .Select(p => new PlayerDto(p.Id, p.UserId, PlayerName(p), p.IsBot ? null : p.User?.PhotoUrl, p.IsBot, p.UserId == userId && me is not null, p.State,
                // Your-own-time: the opponent's score stays hidden until both have played.
                !c.IsLive && !finished && p.UserId != userId ? 0 : p.Score,
                !c.IsLive && !finished && p.UserId != userId ? 0 : p.Correct,
                answered.GetValueOrDefault(p.Id), p.TotalAnswerMs, p.Rank, p.Result, p.PointsAwarded,
                p.RatingAfter is { } after && p.RatingBefore is { } before ? after - before : null))
            .ToList();

        var isGroupMember = c.ConversationId is { } conv && c.Mode == CompetitionMode.GroupBattle && await chat.IsMemberAsync(userId, conv, ct);
        var canJoin = c.Mode == CompetitionMode.GroupBattle && c.Status == CompetitionStatus.Waiting && me is null && isGroupMember
                      && c.Players.Count < O.GroupBattleMaxPlayers;
        var canStart = c.Mode == CompetitionMode.GroupBattle && c.Status == CompetitionStatus.Waiting && c.Players.Count >= O.GroupBattleMinPlayers
                       && (c.CreatedBy == userId || (c.ConversationId is { } cv && await IsGroupAdminAsync(userId, cv, ct)));
        var canAccept = c.Status == CompetitionStatus.Pending && me?.State == PlayerState.Invited && c.ExpiresAt > now;
        var canPlay = !c.IsLive && me?.State == PlayerState.Joined && c.Status is CompetitionStatus.Pending or CompetitionStatus.Running;
        var canBot = c.Mode == CompetitionMode.QuickMatch && c.Status == CompetitionStatus.Waiting && c.CreatedBy == userId && c.ExpiresAt <= now;

        var summary = Summary(c, me, userId, canAccept || canPlay || canJoin);
        return new CompetitionDto(summary, players, current, iAnswered, canJoin, canStart, canAccept, canPlay, canBot, c.ConversationId, c.TournamentId,
            c.Round, now);
    }

    private CompetitionSummaryDto Summary(Competition c, CompetitionPlayer? me, Guid userId, bool needsAction)
    {
        var opponent = c.Players.FirstOrDefault(p => p.UserId != userId && p.State != PlayerState.Declined);
        var title = c.Mode switch
        {
            CompetitionMode.Challenge => opponent is null ? "Challenge" : $"Challenge · {PlayerName(opponent)}",
            CompetitionMode.QuickMatch => opponent is null ? "Quick match · searching…" : $"Quick match · {PlayerName(opponent)}",
            CompetitionMode.GroupBattle => "Group battle",
            _ => $"Tournament · round {c.Round}"
        };
        return new CompetitionSummaryDto(c.Id, c.Mode, c.Status, c.Language.Name, c.Level, c.QuestionCount, c.SecondsPerQuestion, c.IsLive, c.IsRanked,
            title, c.Players.Count(p => p.State != PlayerState.Declined) == 2 ? opponent is null ? null : PlayerName(opponent) : null,
            c.CreatedAt, c.StartsAt, c.ExpiresAt, me?.Result, me?.Rank, me?.Score, me?.PointsAwarded ?? 0, needsAction, c.Language.IsoCode);
    }

    private async Task<CompetitionQuestionDto?> CurrentQuestionAsync(Competition c, CompetitionPlayer me, CancellationToken ct)
    {
        if (me.State != PlayerState.Playing) return null;
        if (c.IsLive)
            return c.Status == CompetitionStatus.Running && c.CurrentIndex >= 0
                ? await QuestionDtoAsync(c, c.CurrentIndex, c.QuestionOpensAt!.Value, c.QuestionClosesAt!.Value, ct)
                : null;
        return me.CurrentIndex >= 0 ? await QuestionDtoAsync(c, me.CurrentIndex, me.QuestionOpensAt!.Value, me.QuestionClosesAt!.Value, ct) : null;
    }

    private async Task<CompetitionQuestionDto> QuestionDtoAsync(Competition c, int index, DateTimeOffset opens, DateTimeOffset closes, CancellationToken ct)
    {
        var q = await QuestionAsync(c, index, ct);
        var options = q.Type == QuestionType.FillBlank
            ? []
            : q.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Select(o => new CompetitionOptionDto(o.Id, o.Text)).ToList();
        return new CompetitionQuestionDto(c.Id, index, c.QuestionCount, q.Type, q.Stem, q.Prompt, q.MediaUrl, options, opens, closes, clock.GetUtcNow());
    }

    private readonly Dictionary<(int, int), Question> _questions = new();

    private async Task<Question> QuestionAsync(Competition c, int index, CancellationToken ct)
    {
        if (_questions.TryGetValue((c.Id, index), out var cached)) return cached;
        var questionId = await db.CompetitionQuestions.AsNoTracking().Where(q => q.CompetitionId == c.Id && q.SortOrder == index)
            .Select(q => q.QuestionId).FirstAsync(ct);
        var q = await db.Questions.AsNoTracking().Include(x => x.Options).FirstAsync(x => x.Id == questionId, ct);
        _questions[(c.Id, index)] = q;
        return q;
    }

    // ================================================================== helpers

    /// <summary>FR-Q02: published objective questions for the language and level (widening to ± 1 level, then any level, when short).</summary>
    private async Task<Result> PickQuestionsAsync(Competition c, CancellationToken ct)
    {
        if (c.Questions.Count > 0 || await db.CompetitionQuestions.AnyAsync(q => q.CompetitionId == c.Id && c.Id != 0, ct)) return Result.Success();
        var types = CompetitionRules.Types;
        var bank = db.Questions.AsNoTracking().Where(q => q.LanguageId == c.LanguageId && q.Status == ContentStatus.Published && !q.IsDeleted
                                                          && types.Contains(q.Type) && q.Options.Any());
        if (c.ModuleId is { } module) bank = bank.Where(q => q.ModuleId == module);

        var exact = await bank.Where(q => q.Level == c.Level).Select(q => q.Id).Take(500).ToListAsync(ct);
        var ids = exact;
        if (ids.Count < c.QuestionCount)
        {
            var low = c.Level - 1;
            var high = c.Level + 1;
            ids = await bank.Where(q => q.Level >= low && q.Level <= high).Select(q => q.Id).Take(500).ToListAsync(ct);
        }
        if (ids.Count < c.QuestionCount) ids = await bank.Select(q => q.Id).Take(500).ToListAsync(ct);
        if (ids.Count < c.QuestionCount)
            return Error.Conflict("not_enough_questions",
                $"There aren't enough published questions for this language yet ({ids.Count} of {c.QuestionCount}). Try fewer questions.");

        // Exact-level questions first, shuffled.
        var seed = HashCode.Combine(c.CreatedAt.UtcTicks, c.CreatedBy, c.LanguageId);
        var ordered = CompetitionRules.Pick(exact, c.QuestionCount, seed).ToList();
        if (ordered.Count < c.QuestionCount)
            ordered.AddRange(CompetitionRules.Pick(ids.Except(ordered).ToList(), c.QuestionCount - ordered.Count, seed + 1));
        for (var i = 0; i < ordered.Count; i++) c.Questions.Add(new CompetitionQuestion { SortOrder = i, QuestionId = ordered[i] });
        return Result.Success();
    }

    private async Task<Error?> RestrictionAsync(Guid userId, CancellationToken ct)
    {
        // FR-M12: a suspension stops competitions as well as chat.
        var r = await moderation.ChatRestrictionAsync(userId, ct);
        return r is { Blocked: true } or { Code: "chat_suspended" } ? Error.Conflict(r.Code, r.Message) : null;
    }

    private async Task<Error?> RankedLimitAsync(Guid userId, CancellationToken ct)
    {
        var max = await settings.GetIntAsync(SettingKeys.CompetitionMaxRankedPerDay, 20, ct);
        var today = await RankedTodayAsync(userId, ct);
        return max > 0 && today >= max
            ? Error.TooManyRequests("ranked_limit", $"You've played {today} ranked matches today — the limit is {max}. Come back tomorrow!")
            : null;
    }

    private async Task<int> RankedTodayAsync(Guid userId, CancellationToken ct)
    {
        var dayStart = await DayStartAsync(userId, ct);
        CompetitionStatus[] notCounted = [CompetitionStatus.Declined, CompetitionStatus.Expired, CompetitionStatus.Cancelled];
        return await db.CompetitionPlayers.CountAsync(p => p.UserId == userId && p.State != PlayerState.Declined && p.State != PlayerState.Invited
                                                           && p.Competition.IsRanked && p.Competition.CreatedAt >= dayStart
                                                           && !notCounted.Contains(p.Competition.Status)
                                                           && !p.Competition.Players.Any(o => o.IsBot), ct);
    }

    private async Task<DateTimeOffset> DayStartAsync(Guid userId, CancellationToken ct)
    {
        var tz = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        return PracticeRules.DayStartUtc(clock.GetUtcNow(), tz);
    }

    private async Task<CefrLevel?> LevelOfAsync(Guid userId, int languageId, CancellationToken ct) =>
        await db.LearnerCourses.AsNoTracking()
            .Where(lc => lc.UserId == userId && lc.LanguageId == languageId && lc.Status != LearnerCourseStatus.Cancelled)
            .OrderByDescending(lc => lc.CurrentLevel).Select(lc => (CefrLevel?)lc.CurrentLevel).FirstOrDefaultAsync(ct);

    private Task<bool> IsGroupAdminAsync(Guid userId, int conversationId, CancellationToken ct) =>
        db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId && m.IsActive && !m.IsPending
                                             && m.Role != MemberRole.Member, ct);

    private async Task<string> NameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName ?? u.FullName).FirstOrDefaultAsync(ct) ?? "A learner";

    private Task<Competition?> LoadAsync(int id, CancellationToken ct) =>
        db.Competitions.Include(c => c.Players).ThenInclude(p => p.User).FirstOrDefaultAsync(c => c.Id == id, ct);

    private static string PlayerName(CompetitionPlayer p) =>
        p.IsBot ? $"🤖 {p.BotName ?? "Bot"}" : p.User is null ? "Learner" : p.User.IsBlocked ? "Blocked user" : p.User.DisplayName ?? p.User.FullName;

    private static IReadOnlyList<Guid> Humans(Competition c) =>
        c.Players.Where(p => p.UserId is not null && p.State != PlayerState.Declined).Select(p => p.UserId!.Value).ToList();

    private static void Bump(Competition c) => c.ConcurrencyStamp = Guid.NewGuid();

    private void DetachAll(Competition c)
    {
        if (db is not DbContext context) return;
        foreach (var p in c.Players) context.Entry(p).State = EntityState.Detached;
        foreach (var q in c.Questions) context.Entry(q).State = EntityState.Detached;
        context.Entry(c).State = EntityState.Detached;
    }

    private static SemaphoreSlim Lock(int id) => Locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private static AnswerAckDto Ack(bool ok, string? code, string? message) => new(ok, code, message, null, null, false);
}
