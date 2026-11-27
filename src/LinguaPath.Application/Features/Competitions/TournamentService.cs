using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Competitions;

public sealed record TournamentForm(
    string Title, string? Description, int LanguageId, CefrLevel Level, int? ModuleId, DateTimeOffset EntryOpensAt, DateTimeOffset StartsAt,
    int Rounds, int QuestionCount, int SecondsPerQuestion, int? MaxEntrants, int PrizeFirst, int PrizeSecond, int PrizeThird);

public sealed record TournamentRoundItem(int Round, int CompetitionId, CompetitionStatus Status, int Players, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public sealed record TournamentEntryItem(int Id, Guid UserId, string DisplayName, string Email, DateTimeOffset JoinedAt, int RoundReached,
    int LastRoundRank, int LastRoundScore, int? FinalPlace, int PrizePoints, bool IsDisqualified, string? DisqualifiedReason, string? DisqualifiedBy);

public sealed record TournamentAdminItem(int Id, string Title, string LanguageName, CefrLevel Level, TournamentStatus Status, DateTimeOffset EntryOpensAt,
    DateTimeOffset StartsAt, int Rounds, int CurrentRound, int Entrants, int PrizeFirst, int PrizeSecond, int PrizeThird);

public sealed record TournamentAdminDetails(Tournament Tournament, string LanguageName, IReadOnlyList<TournamentEntryItem> Entries,
    IReadOnlyList<TournamentRoundItem> Rounds);

public interface ITournamentService
{
    Task<IReadOnlyList<TournamentDto>> ListAsync(Guid userId, int? languageId, CancellationToken ct = default);
    Task<Result<TournamentDto>> GetAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<TournamentDto>> JoinAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result> WithdrawAsync(Guid userId, int id, CancellationToken ct = default);
    /// <summary>Starts tournaments whose time has come and moves finished rounds on.</summary>
    Task<int> TickAsync(CancellationToken ct = default);
    Task AdvanceAsync(int id, CancellationToken ct = default);
}

/// <summary>FR-Q11: create and schedule tournaments, choose the pool, set prizes, see results, disqualify.</summary>
public interface ITournamentAdminService
{
    Task<IReadOnlyList<TournamentAdminItem>> ListAsync(TournamentStatus? status, CancellationToken ct = default);
    Task<TournamentAdminDetails?> DetailsAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(int? id, TournamentForm form, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> CancelAsync(int id, string reason, CancellationToken ct = default);
    Task<Result> StartNowAsync(int id, CancellationToken ct = default);
    Task<Result> DisqualifyAsync(int id, Guid userId, string reason, CancellationToken ct = default);
}

internal sealed class TournamentService(
    IApplicationDbContext db,
    ICompetitionEngine engine,
    ICompetitionService competitions,
    IModerationService moderation,
    IPointsService points,
    IPointsAdminService pointsAdmin,
    ICurrentUser currentUser,
    IAuditLogger audit,
    IOptions<CompetitionOptions> options,
    TimeProvider clock,
    ILogger<TournamentService> logger) : ITournamentService, ITournamentAdminService
{
    // ================================================================== learners

    public async Task<IReadOnlyList<TournamentDto>> ListAsync(Guid userId, int? languageId, CancellationToken ct = default)
    {
        var since = clock.GetUtcNow().AddDays(-14);
        var q = db.Tournaments.AsNoTracking().Where(t => t.Status == TournamentStatus.Open || t.Status == TournamentStatus.Running
                                                        || (t.Status == TournamentStatus.Finished && t.FinishedAt >= since));
        if (languageId is { } l) q = q.Where(t => t.LanguageId == l);
        var ids = await q.OrderBy(t => t.Status).ThenBy(t => t.StartsAt).Select(t => t.Id).Take(50).ToListAsync(ct);
        var list = new List<TournamentDto>();
        foreach (var id in ids)
        {
            await AdvanceAsync(id, ct);
            list.Add(await BuildAsync(userId, id, standings: false, ct));
        }
        return list;
    }

    public async Task<Result<TournamentDto>> GetAsync(Guid userId, int id, CancellationToken ct = default)
    {
        if (!await db.Tournaments.AnyAsync(t => t.Id == id && t.Status != TournamentStatus.Draft, ct)) return Error.NotFound("Tournament");
        await AdvanceAsync(id, ct);
        return await BuildAsync(userId, id, standings: true, ct);
    }

    public async Task<Result<TournamentDto>> JoinAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var t = await db.Tournaments.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id && x.Status != TournamentStatus.Draft, ct);
        if (t is null) return Error.NotFound("Tournament");
        var now = clock.GetUtcNow();
        if (t.Entries.Any(e => e.UserId == userId)) return await BuildAsync(userId, id, true, ct);
        if (t.Status != TournamentStatus.Open || now < t.EntryOpensAt || now >= t.StartsAt)
            return Error.Conflict("entry_closed", now < t.EntryOpensAt ? $"Entry opens {t.EntryOpensAt:u}." : "Entry has closed.");
        if (t.MaxEntrants is { } max && t.Entries.Count >= max) return Error.Conflict("tournament_full", "The tournament is full.");
        var restriction = await moderation.ChatRestrictionAsync(userId, ct);
        if (restriction is { Blocked: true } or { Code: "chat_suspended" }) return Error.Conflict(restriction.Code, restriction.Message);
        t.Entries.Add(new TournamentEntry { UserId = userId, JoinedAt = now });
        await db.SaveChangesAsync(ct);
        return await BuildAsync(userId, id, true, ct);
    }

    public async Task<Result> WithdrawAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var entry = await db.TournamentEntries.Include(e => e.Tournament).FirstOrDefaultAsync(e => e.TournamentId == id && e.UserId == userId, ct);
        if (entry is null) return Error.NotFound("Entry");
        if (entry.Tournament.Status != TournamentStatus.Open) return Error.Conflict("already_started", "The tournament has started; leave the round instead.");
        db.TournamentEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ================================================================== the clock

    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Tournaments.AsNoTracking()
            .Where(t => (t.Status == TournamentStatus.Open && t.StartsAt <= now) || (t.Status == TournamentStatus.Running && t.NextEventAt <= now))
            .Select(t => t.Id).Take(20).ToListAsync(ct);
        foreach (var id in due)
        {
            try
            {
                await AdvanceAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tournament {Id} could not advance", id);
            }
        }
        return due.Count;
    }

    public async Task AdvanceAsync(int id, CancellationToken ct = default)
    {
        var t = await db.Tournaments.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return;
        var now = clock.GetUtcNow();

        if (t.Status == TournamentStatus.Open && t.StartsAt <= now)
        {
            await StartAsync(t, now, ct);
            return;
        }
        if (t.Status != TournamentStatus.Running) return;

        var roundId = await db.Competitions.Where(c => c.TournamentId == id && c.Round == t.CurrentRound).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (roundId is null) return;
        await competitions.AdvanceAsync(roundId.Value, ct);
        var round = await db.Competitions.AsNoTracking().Include(c => c.Players).FirstAsync(c => c.Id == roundId, ct);
        if (round.Status is not (CompetitionStatus.Finished or CompetitionStatus.Cancelled))
        {
            t.NextEventAt = round.NextEventAt ?? now.AddSeconds(5);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Round over: record it, then the next round or the final standings.
        foreach (var p in round.Players.Where(p => p.UserId is not null))
        {
            var entry = t.Entries.FirstOrDefault(e => e.UserId == p.UserId);
            if (entry is null) continue;
            entry.RoundReached = t.CurrentRound;
            entry.LastRoundRank = p.Rank ?? int.MaxValue;
            entry.LastRoundScore = p.Score;
        }
        var stayed = round.Players.Where(p => p.UserId is not null && !p.LeftEarly && p.Rank is not null
                                              && t.Entries.Any(e => e.UserId == p.UserId && !e.IsDisqualified))
            .OrderBy(p => p.Rank).ToList();
        var advancing = CompetitionRules.Advancing(stayed.Count, t.CurrentRound, t.Rounds);
        if (advancing > 0)
        {
            t.CurrentRound++;
            var next = stayed.Take(advancing).Select(p => p.UserId!.Value).ToList();
            await db.SaveChangesAsync(ct);
            var created = await engine.CreateRoundAsync(t, t.CurrentRound, next, now.AddSeconds(options.Value.TournamentBreakSeconds), ct);
            if (created.IsFailure)
            {
                logger.LogWarning("Tournament {Id} round {Round} could not start: {Error}", id, t.CurrentRound, created.Error!.Message);
                t.CurrentRound--;
                await FinishAsync(t, now, ct);
                return;
            }
            t.NextEventAt = now.AddSeconds(options.Value.TournamentBreakSeconds);
            await db.SaveChangesAsync(ct);
            return;
        }
        await FinishAsync(t, now, ct);
    }

    private async Task StartAsync(Tournament t, DateTimeOffset now, CancellationToken ct)
    {
        var entrants = t.Entries.Where(e => !e.IsDisqualified).OrderBy(e => e.JoinedAt).Select(e => e.UserId).ToList();
        if (entrants.Count < 2)
        {
            t.Status = TournamentStatus.Cancelled;
            t.FinishedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }
        t.Status = TournamentStatus.Running;
        t.CurrentRound = 1;
        foreach (var e in t.Entries.Where(e => !e.IsDisqualified)) e.RoundReached = 1;
        await db.SaveChangesAsync(ct);
        var created = await engine.CreateRoundAsync(t, 1, entrants, now, ct);
        if (created.IsFailure)
        {
            logger.LogWarning("Tournament {Id} could not start: {Error}", t.Id, created.Error!.Message);
            t.Status = TournamentStatus.Cancelled;
            t.FinishedAt = now;
        }
        t.NextEventAt = now.AddSeconds(5);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Final places: the last round's ranks, then everyone else by the round they reached and their rank in it. Prizes to the top 3.</summary>
    private async Task FinishAsync(Tournament t, DateTimeOffset now, CancellationToken ct)
    {
        var order = t.Entries.Where(e => e.RoundReached > 0)
            .OrderBy(e => e.IsDisqualified).ThenByDescending(e => e.RoundReached).ThenBy(e => e.LastRoundRank).ThenByDescending(e => e.LastRoundScore)
            .ThenBy(e => e.JoinedAt).ToList();
        for (var i = 0; i < order.Count; i++) order[i].FinalPlace = order[i].IsDisqualified ? null : i + 1;
        t.Status = TournamentStatus.Finished;
        t.FinishedAt = now;
        t.NextEventAt = null;
        await db.SaveChangesAsync(ct);
        await ReconcilePrizesAsync(t, ct);
    }

    /// <summary>Pays the top 3 not disqualified; takes back prizes that no longer belong (after a disqualification).</summary>
    private async Task ReconcilePrizesAsync(Tournament t, CancellationToken ct)
    {
        var desired = CompetitionRules.Prizes(
            t.Entries.Where(e => e.FinalPlace is not null).Select(e => (e.UserId, e.FinalPlace!.Value, e.IsDisqualified)),
            t.PrizeFirst, t.PrizeSecond, t.PrizeThird);
        var prefix = $"{t.Id}:";
        var paid = await db.PointTransactions
            .Where(x => x.Action == PointAction.TournamentPrize && x.ReferenceType == "tournament" && x.ReferenceId!.StartsWith(prefix) && !x.IsRevoked)
            .ToListAsync(ct);
        foreach (var tx in paid.Where(x => !desired.Any(d => d.UserId == x.UserId && x.ReferenceId == $"{t.Id}:{d.Place}")))
            await pointsAdmin.RevokeAsync(tx.Id, "Tournament standings changed", ct);
        foreach (var e in t.Entries) e.PrizePoints = 0;
        foreach (var (userId, place, prize) in desired)
        {
            var entry = t.Entries.First(e => e.UserId == userId);
            if (!paid.Any(x => x.UserId == userId && x.ReferenceId == $"{t.Id}:{place}"))
                await points.AwardAsync(userId, t.LanguageId, PointAction.TournamentPrize, "tournament", $"{t.Id}:{place}", prize, ct);
            entry.PrizePoints = prize;
        }
        await db.SaveChangesAsync(ct);
    }

    // ================================================================== admin

    public async Task<IReadOnlyList<TournamentAdminItem>> ListAsync(TournamentStatus? status, CancellationToken ct = default)
    {
        var q = db.Tournaments.AsNoTracking().AsQueryable();
        if (status is { } s) q = q.Where(t => t.Status == s);
        return await q.OrderByDescending(t => t.StartsAt).Take(200)
            .Select(t => new TournamentAdminItem(t.Id, t.Title, t.Language.Name, t.Level, t.Status, t.EntryOpensAt, t.StartsAt, t.Rounds, t.CurrentRound,
                t.Entries.Count, t.PrizeFirst, t.PrizeSecond, t.PrizeThird))
            .ToListAsync(ct);
    }

    public async Task<TournamentAdminDetails?> DetailsAsync(int id, CancellationToken ct = default)
    {
        if (await db.Tournaments.AnyAsync(t => t.Id == id && (t.Status == TournamentStatus.Open || t.Status == TournamentStatus.Running), ct))
            await AdvanceAsync(id, ct);
        var t = await db.Tournaments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return null;
        var language = await db.Languages.Where(l => l.Id == t.LanguageId).Select(l => l.Name).FirstAsync(ct);
        var entries = await db.TournamentEntries.AsNoTracking().Where(e => e.TournamentId == id)
            .OrderBy(e => e.FinalPlace ?? int.MaxValue).ThenByDescending(e => e.RoundReached).ThenBy(e => e.LastRoundRank).ThenBy(e => e.JoinedAt)
            .Select(e => new TournamentEntryItem(e.Id, e.UserId, e.User.DisplayName ?? e.User.FullName, e.User.Email ?? "", e.JoinedAt, e.RoundReached,
                e.LastRoundRank, e.LastRoundScore, e.FinalPlace, e.PrizePoints, e.IsDisqualified, e.DisqualifiedReason, e.DisqualifiedBy))
            .ToListAsync(ct);
        var rounds = await db.Competitions.AsNoTracking().Where(c => c.TournamentId == id).OrderBy(c => c.Round)
            .Select(c => new TournamentRoundItem(c.Round ?? 0, c.Id, c.Status, c.Players.Count, c.StartsAt, c.EndsAt)).ToListAsync(ct);
        return new TournamentAdminDetails(t, language, entries, rounds);
    }

    public async Task<Result<int>> SaveAsync(int? id, TournamentForm f, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = [message];
        if (string.IsNullOrWhiteSpace(f.Title) || f.Title.Trim().Length > 120) Add(nameof(f.Title), "Enter a title of up to 120 characters.");
        if (CompetitionRules.ValidateSettings(f.QuestionCount, f.SecondsPerQuestion) is { } bad) Add(nameof(f.QuestionCount), bad);
        if (f.Rounds is < 1 or > 5) Add(nameof(f.Rounds), "1 to 5 rounds.");
        if (f.EntryOpensAt >= f.StartsAt) Add(nameof(f.EntryOpensAt), "Entry must open before the start.");
        if (f.PrizeFirst < 0 || f.PrizeSecond < 0 || f.PrizeThird < 0 || f.PrizeFirst > 100_000) Add(nameof(f.PrizeFirst), "Prizes are 0 to 100,000 points.");
        if (f.MaxEntrants is < 2) Add(nameof(f.MaxEntrants), "At least 2 entrants.");
        if (!await db.Languages.AnyAsync(l => l.Id == f.LanguageId, ct)) Add(nameof(f.LanguageId), "Choose a language.");
        if (errors.Count > 0) return Error.Validation(errors);

        Tournament t;
        if (id is { } tid)
        {
            t = await db.Tournaments.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new KeyNotFoundException();
            if (t.Status is not (TournamentStatus.Draft or TournamentStatus.Open))
                return Error.Conflict("tournament_started", "A tournament can't be edited once it has started.");
        }
        else
        {
            t = new Tournament { CreatedAt = clock.GetUtcNow(), CreatedByEmail = currentUser.Email ?? "", Status = TournamentStatus.Draft };
            db.Tournaments.Add(t);
        }
        t.Title = f.Title.Trim();
        t.Description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim();
        t.LanguageId = f.LanguageId;
        t.Level = f.Level;
        t.ModuleId = f.ModuleId;
        t.EntryOpensAt = f.EntryOpensAt;
        t.StartsAt = f.StartsAt;
        t.Rounds = f.Rounds;
        t.QuestionCount = f.QuestionCount;
        t.SecondsPerQuestion = f.SecondsPerQuestion;
        t.MaxEntrants = f.MaxEntrants;
        t.PrizeFirst = f.PrizeFirst;
        t.PrizeSecond = f.PrizeSecond;
        t.PrizeThird = f.PrizeThird;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(id is null ? "Create" : "Update", "Tournament", t.Id.ToString(), null, f, ct);
        return t.Id;
    }

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var t = await db.Tournaments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Error.NotFound("Tournament");
        if (t.Status != TournamentStatus.Draft) return Error.Conflict("not_draft", "Only drafts can be published.");
        if (t.StartsAt <= clock.GetUtcNow()) return Error.Validation("StartsAt", "Set a start time in the future first.");
        // There must be enough questions for every round.
        var count = await db.Questions.CountAsync(q => q.LanguageId == t.LanguageId && q.Status == ContentStatus.Published && !q.IsDeleted
                                                       && CompetitionRules.Types.Contains(q.Type) && (t.ModuleId == null || q.ModuleId == t.ModuleId), ct);
        if (count < t.QuestionCount) return Error.Validation("QuestionCount", $"Only {count} published questions match this pool; you need {t.QuestionCount}.");
        t.Status = TournamentStatus.Open;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "Tournament", id.ToString(), null, null, ct);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(int id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required.");
        var t = await db.Tournaments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Error.NotFound("Tournament");
        if (t.Status is TournamentStatus.Finished or TournamentStatus.Cancelled) return Error.Conflict("tournament_over", "It's already over.");
        t.Status = TournamentStatus.Cancelled;
        t.FinishedAt = clock.GetUtcNow();
        t.NextEventAt = null;
        var rounds = await db.Competitions.Where(c => c.TournamentId == id && c.Status != CompetitionStatus.Finished).ToListAsync(ct);
        foreach (var c in rounds)
        {
            c.Status = CompetitionStatus.Cancelled;
            c.NextEventAt = null;
            c.ConcurrencyStamp = Guid.NewGuid();
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Cancel", "Tournament", id.ToString(), null, new { reason }, ct);
        return Result.Success();
    }

    public async Task<Result> StartNowAsync(int id, CancellationToken ct = default)
    {
        var t = await db.Tournaments.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Error.NotFound("Tournament");
        if (t.Status != TournamentStatus.Open) return Error.Conflict("not_open", "Only published tournaments that haven't started can start now.");
        if (t.Entries.Count(e => !e.IsDisqualified) < 2) return Error.Conflict("not_enough_players", "At least 2 entrants are needed.");
        var now = clock.GetUtcNow();
        t.StartsAt = now;
        if (t.EntryOpensAt > now) t.EntryOpensAt = now;
        await StartAsync(t, now, ct);
        await audit.LogAsync("StartNow", "Tournament", id.ToString(), null, null, ct);
        return t.Status == TournamentStatus.Running ? Result.Success() : Error.Conflict("not_started", "The tournament couldn't start (not enough questions?).");
    }

    public async Task<Result> DisqualifyAsync(int id, Guid userId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required.");
        var t = await db.Tournaments.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id, ct);
        var entry = t?.Entries.FirstOrDefault(e => e.UserId == userId);
        if (t is null || entry is null) return Error.NotFound("Entry");
        if (entry.IsDisqualified) return Result.Success();
        entry.IsDisqualified = true;
        entry.DisqualifiedReason = reason.Trim();
        entry.DisqualifiedBy = currentUser.Email ?? "admin";
        entry.FinalPlace = null;
        await db.SaveChangesAsync(ct);

        // Out of the running round, and every point this tournament gave them is taken back (FR-Q11).
        var running = await db.Competitions.Where(c => c.TournamentId == id && c.Status != CompetitionStatus.Finished && c.Status != CompetitionStatus.Cancelled)
            .Select(c => c.Id).ToListAsync(ct);
        foreach (var c in running) await competitions.LeaveAsync(userId, c, ct);
        var roundIds = await db.Competitions.Where(c => c.TournamentId == id).Select(c => c.Id.ToString()).ToListAsync(ct);
        var prefix = $"{id}:";
        var txs = await db.PointTransactions.Where(x => x.UserId == userId && !x.IsRevoked
                                                        && ((x.ReferenceType == "tournament" && x.ReferenceId!.StartsWith(prefix))
                                                            || (x.ReferenceType == "competition" && roundIds.Contains(x.ReferenceId!))))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var tx in txs) await pointsAdmin.RevokeAsync(tx, "Disqualified: " + reason.Trim(), ct);

        if (t.Status == TournamentStatus.Finished)
        {
            // Places move up behind the disqualified player.
            var order = t.Entries.Where(e => !e.IsDisqualified && e.FinalPlace is not null).OrderBy(e => e.FinalPlace).ToList();
            for (var i = 0; i < order.Count; i++) order[i].FinalPlace = i + 1;
            await db.SaveChangesAsync(ct);
            await ReconcilePrizesAsync(t, ct);
        }
        await audit.LogAsync("Disqualify", "Tournament", id.ToString(), null, new { userId, reason }, ct);
        return Result.Success();
    }

    // ================================================================== DTO

    private async Task<TournamentDto> BuildAsync(Guid userId, int id, bool standings, CancellationToken ct)
    {
        var t = await db.Tournaments.AsNoTracking().Include(x => x.Language).FirstAsync(x => x.Id == id, ct);
        var now = clock.GetUtcNow();
        var entrants = await db.TournamentEntries.CountAsync(e => e.TournamentId == id && !e.IsDisqualified, ct);
        var mine = await db.TournamentEntries.AsNoTracking().FirstOrDefaultAsync(e => e.TournamentId == id && e.UserId == userId, ct);
        int? myCompetition = null;
        if (mine is not null && t.Status == TournamentStatus.Running)
            myCompetition = await db.CompetitionPlayers.Where(p => p.UserId == userId && p.Competition.TournamentId == id && p.Competition.Round == t.CurrentRound)
                .Select(p => (int?)p.CompetitionId).FirstOrDefaultAsync(ct);

        IReadOnlyList<TournamentStandingDto> table = [];
        if (standings && t.Status is TournamentStatus.Running or TournamentStatus.Finished)
        {
            var rows = await db.TournamentEntries.AsNoTracking().Where(e => e.TournamentId == id && !e.IsDisqualified)
                .OrderBy(e => e.FinalPlace ?? int.MaxValue).ThenByDescending(e => e.RoundReached).ThenBy(e => e.LastRoundRank).ThenBy(e => e.JoinedAt)
                .Take(100)
                .Select(e => new { e.UserId, Name = e.User.DisplayName ?? e.User.FullName, e.RoundReached, e.LastRoundScore, e.PrizePoints, e.FinalPlace })
                .ToListAsync(ct);
            table = rows.Select((r, i) => new TournamentStandingDto(r.FinalPlace ?? i + 1, r.UserId, r.Name, r.RoundReached, r.LastRoundScore, r.PrizePoints,
                r.UserId == userId)).ToList();
        }

        var canJoin = mine is null && t.Status == TournamentStatus.Open && now >= t.EntryOpensAt && now < t.StartsAt
                      && (t.MaxEntrants is null || entrants < t.MaxEntrants);
        return new TournamentDto(t.Id, t.Title, t.Description, t.Language.Name, t.LanguageId, t.Level, t.Status, t.EntryOpensAt, t.StartsAt, t.Rounds,
            t.CurrentRound, t.QuestionCount, t.SecondsPerQuestion, t.PrizeFirst, t.PrizeSecond, t.PrizeThird, entrants, t.MaxEntrants, mine is not null,
            canJoin, mine?.IsDisqualified ?? false, myCompetition, mine?.FinalPlace, table);
    }
}
