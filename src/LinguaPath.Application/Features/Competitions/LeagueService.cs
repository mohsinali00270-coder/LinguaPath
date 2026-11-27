using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Competitions;

/// <summary>
/// FR-S08 weekly leagues: groups of up to 30 learners in the same division, ranked by the points they earn this week (Monday 00:00 UTC);
/// top 5 go up a division, bottom 5 go down. Learners join when they first open the league screen in a week.
/// </summary>
public interface ILeagueService
{
    Task<LeagueDto> CurrentAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Closes every league from earlier weeks that is still open. Returns how many.</summary>
    Task<int> CloseDueAsync(CancellationToken ct = default);
}

internal sealed class LeagueService(IApplicationDbContext db, TimeProvider clock) : ILeagueService
{
    public async Task<LeagueDto> CurrentAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var week = CompetitionRules.WeekOf(now);

        var membership = await db.LeagueMembers.Include(m => m.League)
            .Where(m => m.UserId == userId && m.League.WeekStart == week).FirstOrDefaultAsync(ct);
        LeagueOutcome? lastOutcome = null;
        LeagueTier? lastTier = null;
        if (membership is null)
        {
            var previous = await db.LeagueMembers.Include(m => m.League).Where(m => m.UserId == userId && m.League.WeekStart < week)
                .OrderByDescending(m => m.League.WeekStart).FirstOrDefaultAsync(ct);
            if (previous is not null && !previous.League.IsClosed) await CloseAsync(previous.League.Id, ct);
            var tier = previous is null ? LeagueTier.Bronze : CompetitionRules.NextTier(previous.League.Tier, previous.Outcome ?? LeagueOutcome.Stayed);
            membership = await JoinAsync(userId, week, tier, now, ct);
            lastOutcome = previous?.Outcome;
            lastTier = previous?.League.Tier;
        }
        else
        {
            var previous = await db.LeagueMembers.AsNoTracking().Include(m => m.League).Where(m => m.UserId == userId && m.League.WeekStart < week)
                .OrderByDescending(m => m.League.WeekStart).FirstOrDefaultAsync(ct);
            lastOutcome = previous?.Outcome;
            lastTier = previous?.League.Tier;
        }

        var league = membership.League;
        var standings = await StandingsAsync(league, ct);
        var hidden = (await db.UserPrivacySettings.AsNoTracking().Where(p => !p.ShowOnLeaderboard).Select(p => p.UserId).ToListAsync(ct)).ToHashSet();
        var names = await db.LeagueMembers.AsNoTracking().Where(m => m.LeagueId == league.Id)
            .Select(m => new { m.UserId, Name = m.User.DisplayName ?? m.User.FullName, m.User.PhotoUrl }).ToDictionaryAsync(m => m.UserId, ct);
        var members = standings.Select(s =>
        {
            var anonymous = hidden.Contains(s.UserId) && s.UserId != userId;
            var n = names.GetValueOrDefault(s.UserId);
            return new LeagueMemberDto(s.Rank, s.UserId, anonymous ? PointsService.HiddenName : n?.Name ?? "Learner", anonymous ? null : n?.PhotoUrl, s.Points,
                s.UserId == userId, CompetitionRules.Zone(s.Rank, standings.Count, league.Tier, s.Points));
        }).ToList();
        var me = members.First(m => m.IsMe);
        return new LeagueDto(league.Id, league.Tier, week, CompetitionRules.WeekStartUtc(week).AddDays(7),
            league.Tier < LeagueTier.Diamond ? CompetitionRules.LeagueZone : 0,
            league.Tier > LeagueTier.Bronze && standings.Count > 2 * CompetitionRules.LeagueZone ? CompetitionRules.LeagueZone : 0,
            me.Rank, me.WeeklyPoints, lastOutcome, lastTier, members);
    }

    public async Task<int> CloseDueAsync(CancellationToken ct = default)
    {
        var week = CompetitionRules.WeekOf(clock.GetUtcNow());
        var due = await db.Leagues.Where(l => !l.IsClosed && l.WeekStart < week).Select(l => l.Id).Take(200).ToListAsync(ct);
        foreach (var id in due) await CloseAsync(id, ct);
        return due.Count;
    }

    private async Task CloseAsync(int leagueId, CancellationToken ct)
    {
        var league = await db.Leagues.Include(l => l.Members).FirstAsync(l => l.Id == leagueId, ct);
        if (league.IsClosed) return;
        var standings = await StandingsAsync(league, ct);
        foreach (var s in standings)
        {
            var m = league.Members.First(x => x.UserId == s.UserId);
            m.WeeklyPoints = s.Points;
            m.Rank = s.Rank;
            m.Outcome = CompetitionRules.Zone(s.Rank, standings.Count, league.Tier, s.Points);
        }
        league.IsClosed = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<LeagueMember> JoinAsync(Guid userId, DateOnly week, LeagueTier tier, DateTimeOffset now, CancellationToken ct)
    {
        var league = await db.Leagues.Where(l => l.WeekStart == week && l.Tier == tier && l.Members.Count < CompetitionRules.LeagueSize)
            .OrderBy(l => l.Number).FirstOrDefaultAsync(ct);
        if (league is null)
        {
            var number = await db.Leagues.Where(l => l.WeekStart == week && l.Tier == tier).MaxAsync(l => (int?)l.Number, ct) ?? 0;
            league = new League { WeekStart = week, Tier = tier, Number = number + 1, CreatedAt = now };
            db.Leagues.Add(league);
            await db.SaveChangesAsync(ct);
        }
        var member = new LeagueMember { LeagueId = league.Id, League = league, UserId = userId, JoinedAt = now };
        db.LeagueMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return member;
    }

    /// <summary>Points earned in the league's week (admin adjustments don't count), blocked learners left out.</summary>
    private async Task<IReadOnlyList<(Guid UserId, int Points, int Rank)>> StandingsAsync(League league, CancellationToken ct)
    {
        var from = CompetitionRules.WeekStartUtc(league.WeekStart);
        var to = from.AddDays(7);
        var members = await db.LeagueMembers.AsNoTracking().Where(m => m.LeagueId == league.Id && !m.User.IsBlocked && !m.User.IsDeleted)
            .Select(m => new { m.UserId, m.JoinedAt }).ToListAsync(ct);
        var ids = members.Select(m => m.UserId).ToList();
        var totals = await db.PointTransactions.AsNoTracking()
            .Where(t => ids.Contains(t.UserId) && !t.IsRevoked && t.Action != PointAction.AdminAdjustment && t.CreatedAt >= from && t.CreatedAt < to)
            .GroupBy(t => t.UserId).Select(g => new { g.Key, Points = g.Sum(t => t.Points) }).ToDictionaryAsync(x => x.Key, x => x.Points, ct);
        return CompetitionRules.LeagueRanks(members.Select(m => (m.UserId, totals.GetValueOrDefault(m.UserId), m.JoinedAt)));
    }
}
