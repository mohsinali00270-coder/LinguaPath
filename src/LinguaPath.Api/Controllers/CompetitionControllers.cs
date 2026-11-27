using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Competitions;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Quiz competitions (SRS 11.4, FR-Q01 .. FR-Q10). Live play also runs over /hubs/competition.</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class CompetitionsController(ICompetitionService competitions) : ApiControllerBase
{
    /// <summary>Challenge a friend or chat contact ("who can challenge me" applies). They have 24 hours to accept.</summary>
    [HttpPost("competitions/challenge")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Challenge(ChallengeRequest request, CancellationToken ct) =>
        FromResult(await competitions.ChallengeAsync(CurrentUserId, request, ct));

    [HttpPost("competitions/{id:int}/accept")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(int id, CancellationToken ct) => FromResult(await competitions.AcceptAsync(CurrentUserId, id, ct));

    [HttpPost("competitions/{id:int}/decline")]
    public async Task<IActionResult> Decline(int id, CancellationToken ct) => FromResult(await competitions.DeclineAsync(CurrentUserId, id, ct));

    /// <summary>Random opponent at ± 1 level. Returns a waiting match when nobody is searching; after 30 s, offer <c>/bot</c>.</summary>
    [HttpPost("competitions/quick-match")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> QuickMatch(QuickMatchRequest request, CancellationToken ct) =>
        FromResult(await competitions.QuickMatchAsync(CurrentUserId, request, ct));

    [HttpPost("competitions/{id:int}/bot")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bot(int id, CancellationToken ct) => FromResult(await competitions.PlayBotAsync(CurrentUserId, id, ct));

    /// <summary>Stop searching, or withdraw a challenge before it's accepted.</summary>
    [HttpPost("competitions/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct) => FromResult(await competitions.CancelAsync(CurrentUserId, id, ct));

    /// <summary>FR-C28: a group admin opens a battle lobby; a card is posted in the group.</summary>
    [HttpPost("conversations/{conversationId:int}/competitions")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GroupBattle(int conversationId, GroupBattleRequest request, CancellationToken ct) =>
        FromResult(await competitions.GroupBattleAsync(CurrentUserId, conversationId, request, ct));

    [HttpPost("competitions/{id:int}/join")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Join(int id, CancellationToken ct) => FromResult(await competitions.JoinAsync(CurrentUserId, id, ct));

    [HttpPost("competitions/{id:int}/start")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(int id, CancellationToken ct) => FromResult(await competitions.StartNowAsync(CurrentUserId, id, ct));

    /// <summary>Your-own-time challenge: start my run of the questions.</summary>
    [HttpPost("competitions/{id:int}/play")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Play(int id, CancellationToken ct) => FromResult(await competitions.PlayAsync(CurrentUserId, id, ct));

    [HttpGet("competitions/{id:int}")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await competitions.GetAsync(CurrentUserId, id, ct));

    /// <summary>Same as the hub's SubmitAnswer. The server times it; the correct answer comes only after the question closes.</summary>
    [HttpPost("competitions/{id:int}/answers")]
    [ProducesResponseType<MyApiResponse<AnswerAckDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Answer(int id, SubmitAnswerRequest request, CancellationToken ct) =>
        FromResult(await competitions.SubmitAnswerAsync(CurrentUserId, id, request, ct));

    /// <summary>FR-Q06: leaving early counts as a loss.</summary>
    [HttpPost("competitions/{id:int}/leave")]
    public async Task<IActionResult> Leave(int id, CancellationToken ct) => FromResult(await competitions.LeaveAsync(CurrentUserId, id, ct));

    /// <summary>FR-Q07: scores, per-question review, points.</summary>
    [HttpGet("competitions/{id:int}/result")]
    [ProducesResponseType<MyApiResponse<CompetitionResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Result(int id, CancellationToken ct) => FromResult(await competitions.ResultAsync(CurrentUserId, id, ct));

    [HttpPost("competitions/{id:int}/rematch")]
    [ProducesResponseType<MyApiResponse<CompetitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rematch(int id, CancellationToken ct) => FromResult(await competitions.RematchAsync(CurrentUserId, id, ct));

    /// <summary>FR-Q07: posts the result into a chat as a card (not a link).</summary>
    [HttpPost("competitions/{id:int}/share")]
    public async Task<IActionResult> Share(int id, ShareRequest request, CancellationToken ct) =>
        FromResult(await competitions.ShareAsync(CurrentUserId, id, request.ConversationId, ct), "Shared.");

    /// <summary>Challenges waiting for me, searches, lobbies and matches in progress.</summary>
    [HttpGet("competitions/active")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<CompetitionSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Active(CancellationToken ct) => OkData(await competitions.ActiveAsync(CurrentUserId, ct));

    [HttpGet("competitions/history")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<CompetitionSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History([FromQuery] int page = 1, CancellationToken ct = default) =>
        OkData(await competitions.HistoryAsync(CurrentUserId, page, ct));

    /// <summary>FR-Q08: played, won, win rate, best streak, rating per language.</summary>
    [HttpGet("competitions/stats")]
    [ProducesResponseType<MyApiResponse<CompetitionStatsDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats(CancellationToken ct) => OkData(await competitions.StatsAsync(CurrentUserId, ct));
}

/// <summary>Admin-scheduled tournaments (FR-Q11) and weekly leagues (FR-S08).</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class TournamentsController(ITournamentService tournaments, ILeagueService leagues) : ApiControllerBase
{
    [HttpGet("tournaments")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<TournamentDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? languageId, CancellationToken ct) => OkData(await tournaments.ListAsync(CurrentUserId, languageId, ct));

    [HttpGet("tournaments/{id:int}")]
    [ProducesResponseType<MyApiResponse<TournamentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await tournaments.GetAsync(CurrentUserId, id, ct));

    [HttpPost("tournaments/{id:int}/join")]
    [ProducesResponseType<MyApiResponse<TournamentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Join(int id, CancellationToken ct) => FromResult(await tournaments.JoinAsync(CurrentUserId, id, ct));

    [HttpDelete("tournaments/{id:int}/join")]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct) => FromResult(await tournaments.WithdrawAsync(CurrentUserId, id, ct));

    /// <summary>FR-S08: my league this week (joined on first visit), with promotion and relegation zones.</summary>
    [HttpGet("leagues/current")]
    [ProducesResponseType<MyApiResponse<LeagueDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> League(CancellationToken ct) => OkData(await leagues.CurrentAsync(CurrentUserId, ct));
}
