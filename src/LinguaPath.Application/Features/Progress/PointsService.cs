using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Progress;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Progress;

/// <summary>The shared points wallet (FR-S01 .. FR-S10, BR-20, BR-22) and leaderboards (FR-S07).</summary>
public interface IPointsService
{
    /// <summary>
    /// Gives points once per action and reference. Returns what was actually added: 0 when the rule is off, the action was
    /// already rewarded, its own daily limit is used up, or the daily cap is reached.
    /// </summary>
    Task<int> AwardAsync(Guid userId, int? languageId, PointAction action, string referenceType, string referenceId, int? points = null,
        CancellationToken ct = default);

    /// <summary>Gives a badge once; true when it is new.</summary>
    Task<bool> GrantBadgeAsync(Guid userId, BadgeKey badge, CancellationToken ct = default);

    /// <summary>Rebuilds the cached total and tier from the ledger, and grants tier badges.</summary>
    Task<IReadOnlyList<BadgeKey>> RecalculateAsync(Guid userId, CancellationToken ct = default);

    Task<PointsSummaryDto> SummaryAsync(Guid userId, CancellationToken ct = default);
    Task<int> TotalAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<PointTransactionDto>> HistoryAsync(Guid userId, int page, CancellationToken ct = default);
    /// <param name="friendsOnly">FR-S07 / FR-S21: only me and my accepted friends.</param>
    Task<LeaderboardDto> LeaderboardAsync(Guid userId, LeaderboardPeriod period, int? languageId, CancellationToken ct = default, bool friendsOnly = false);
    Task<Result> SetLeaderboardVisibilityAsync(Guid userId, bool show, CancellationToken ct = default);
}

/// <summary>Admin side of the wallet (FR-S03, FR-S10).</summary>
public interface IPointsAdminService
{
    Task<IReadOnlyList<PointRule>> RulesAsync(CancellationToken ct = default);
    Task<Result> UpdateRuleAsync(PointAction action, int points, int dailyCap, bool isActive, CancellationToken ct = default);
    Task<Result<long>> AdjustAsync(Guid userId, int points, string reason, int? languageId, CancellationToken ct = default);
    Task<Result> RevokeAsync(long transactionId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<PointTransactionDto>> TransactionsAsync(Guid userId, int take, CancellationToken ct = default);
}

internal sealed class PointsService(
    IApplicationDbContext db,
    ISettingsService settings,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider clock) : IPointsService, IPointsAdminService
{
    public const int LeaderboardSize = 50;
    public const string HiddenName = "Anonymous learner";
    private Dictionary<PointAction, PointRule>? _rules;

    // ================================================================== awarding

    public async Task<int> AwardAsync(Guid userId, int? languageId, PointAction action, string referenceType, string referenceId,
        int? points = null, CancellationToken ct = default)
    {
        var rule = (await RuleMapAsync(ct)).GetValueOrDefault(action);
        if (rule is null || !rule.IsActive) return 0;
        var amount = points ?? rule.Points;
        if (amount <= 0) return 0;

        var key = PointMath.Key(action, referenceType, referenceId);
        if (await db.PointTransactions.AnyAsync(t => t.UserId == userId && t.ReferenceKey == key, ct)) return 0;

        var now = clock.GetUtcNow();
        var dayStart = await DayStartAsync(userId, now, ct);
        if (rule.DailyCap > 0 &&
            await db.PointTransactions.CountAsync(t => t.UserId == userId && t.Action == action && !t.IsRevoked && t.CreatedAt >= dayStart, ct)
            >= rule.DailyCap)
            return 0;

        var capped = PointMath.IsCapped(action);
        if (capped)
        {
            var cap = await settings.GetIntAsync(SettingKeys.PointsDailyCap, 500, ct);
            var used = await db.PointTransactions.Where(t => t.UserId == userId && t.IsCapped && !t.IsRevoked && t.Points > 0 && t.CreatedAt >= dayStart)
                .SumAsync(t => (int?)t.Points, ct) ?? 0;
            amount = PointMath.UnderCap(amount, used, cap);
            if (amount <= 0) return 0;
        }

        var row = new PointTransaction
        {
            UserId = userId, LanguageId = languageId, Action = action, Points = amount, ReferenceType = referenceType, ReferenceId = referenceId,
            ReferenceKey = key, IsCapped = capped, CreatedAt = now
        };
        db.PointTransactions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same award raced in from another request; the unique key kept it single.
            db.PointTransactions.Remove(row);
            return 0;
        }
        await RecalculateAsync(userId, ct);
        return amount;
    }

    public async Task<bool> GrantBadgeAsync(Guid userId, BadgeKey badge, CancellationToken ct = default)
    {
        if (await db.UserBadges.AnyAsync(b => b.UserId == userId && b.Badge == badge, ct)) return false;
        var row = new UserBadge { UserId = userId, Badge = badge, EarnedAt = clock.GetUtcNow() };
        db.UserBadges.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.UserBadges.Remove(row);
            return false;
        }
    }

    public async Task<IReadOnlyList<BadgeKey>> RecalculateAsync(Guid userId, CancellationToken ct = default)
    {
        var total = await db.PointTransactions.Where(t => t.UserId == userId && !t.IsRevoked).SumAsync(t => (int?)t.Points, ct) ?? 0;
        var tier = PointMath.TierFor(Math.Max(0, total));
        var summary = await db.UserPointsSummaries.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (summary is null)
        {
            summary = new UserPointsSummary { UserId = userId };
            db.UserPointsSummaries.Add(summary);
        }
        summary.TotalPoints = total;
        summary.Tier = tier;
        summary.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // Tier badges stay once earned, even if points are later revoked.
        var added = new List<BadgeKey>();
        foreach (var badge in Badges.ForTier(tier))
            if (await GrantBadgeAsync(userId, badge, ct)) added.Add(badge);
        return added;
    }

    // ================================================================== learner views

    public async Task<int> TotalAsync(Guid userId, CancellationToken ct = default) =>
        await db.UserPointsSummaries.Where(s => s.UserId == userId).Select(s => (int?)s.TotalPoints).FirstOrDefaultAsync(ct) ?? 0;

    public async Task<PointsSummaryDto> SummaryAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rows = db.PointTransactions.Where(t => t.UserId == userId && !t.IsRevoked);
        var total = await rows.SumAsync(t => (int?)t.Points, ct) ?? 0;
        var week = PointMath.WeekStart(now);
        var month = PointMath.MonthStart(now);
        var weekly = await rows.Where(t => t.CreatedAt >= week).SumAsync(t => (int?)t.Points, ct) ?? 0;
        var monthly = await rows.Where(t => t.CreatedAt >= month).SumAsync(t => (int?)t.Points, ct) ?? 0;
        var dayStart = await DayStartAsync(userId, now, ct);
        var today = await rows.Where(t => t.IsCapped && t.Points > 0 && t.CreatedAt >= dayStart).SumAsync(t => (int?)t.Points, ct) ?? 0;
        var cap = await settings.GetIntAsync(SettingKeys.PointsDailyCap, 500, ct);

        // FR-S01: per-language breakdown; account-wide points (streaks, adjustments) show as "General".
        var byLanguage = await rows.GroupBy(t => new { t.LanguageId, Name = t.Language != null ? t.Language.Name : null })
            .Select(g => new { g.Key.LanguageId, g.Key.Name, Points = g.Sum(t => t.Points) }).ToListAsync(ct);

        var tier = PointMath.TierFor(Math.Max(0, total));
        var (next, toGo, progress) = PointMath.NextTier(Math.Max(0, total));

        var streak = await db.UserStreaks.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone(await TimeZoneAsync(userId, ct))).DateTime);

        var earned = await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).ToListAsync(ct);
        var badges = Badges.All.Select(b => Badges.ToDto(b.Key, earned.FirstOrDefault(e => e.Badge == b.Key)?.EarnedAt)).ToList();

        var show = await db.UserPrivacySettings.Where(p => p.UserId == userId).Select(p => (bool?)p.ShowOnLeaderboard).FirstOrDefaultAsync(ct) ?? true;

        return new PointsSummaryDto(total, tier, next, toGo, progress, weekly, monthly, today, cap,
            byLanguage.OrderByDescending(x => x.Points).Select(x => new LanguagePointsDto(x.LanguageId, x.Name ?? "General", x.Points)).ToList(),
            StreakRules.View(streak, localToday), badges, show);
    }

    public async Task<IReadOnlyList<PointTransactionDto>> HistoryAsync(Guid userId, int page, CancellationToken ct = default)
    {
        const int size = 50;
        var rows = await db.PointTransactions.AsNoTracking().Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Skip(Math.Max(0, page - 1) * size).Take(size)
            .Select(t => new { t.Id, t.Action, t.Note, Language = t.Language != null ? t.Language.Name : null, t.Points, t.CreatedAt, t.IsRevoked })
            .ToListAsync(ct);
        return rows.Select(r => new PointTransactionDto(r.Id, r.Action, PointMath.Describe(r.Action, r.Note), r.Language, r.Points, r.CreatedAt,
            r.IsRevoked)).ToList();
    }

    public async Task<LeaderboardDto> LeaderboardAsync(Guid userId, LeaderboardPeriod period, int? languageId, CancellationToken ct = default,
        bool friendsOnly = false)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? since = period switch
        {
            LeaderboardPeriod.Weekly => PointMath.WeekStart(now),
            LeaderboardPeriod.Monthly => PointMath.MonthStart(now),
            _ => null
        };

        // Only learners in good standing (FR-M14: blocked learners leave the boards).
        var eligibleUsers = db.Users.Where(u => u.UserType == UserType.Learner && u.IsActive && !u.IsBlocked && !u.IsDeleted).Select(u => u.Id);
        if (friendsOnly)
        {
            var friends = db.Friendships.Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
                .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId);
            eligibleUsers = eligibleUsers.Where(id => id == userId || friends.Contains(id));
        }
        var rows = db.PointTransactions.Where(t => !t.IsRevoked && eligibleUsers.Contains(t.UserId));
        if (since is { } s) rows = rows.Where(t => t.CreatedAt >= s);
        if (languageId is { } l) rows = rows.Where(t => t.LanguageId == l);

        var totals = rows.GroupBy(t => t.UserId).Select(g => new { UserId = g.Key, Points = g.Sum(t => t.Points) }).Where(x => x.Points > 0);
        var top = await totals.OrderByDescending(x => x.Points).ThenBy(x => x.UserId).Take(LeaderboardSize).ToListAsync(ct);

        var ids = top.Select(t => t.UserId).Append(userId).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.FullName, u.PhotoUrl }).ToListAsync(ct);
        var hidden = (await db.UserPrivacySettings.AsNoTracking().Where(p => ids.Contains(p.UserId) && !p.ShowOnLeaderboard)
            .Select(p => p.UserId).ToListAsync(ct)).ToHashSet();
        var tiers = await db.UserPointsSummaries.AsNoTracking().Where(x => ids.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => x.Tier, ct);

        LeaderboardEntryDto Entry(Guid id, int rank, int points)
        {
            var p = people.FirstOrDefault(x => x.Id == id);
            var isMe = id == userId;
            // FR-S07: opting out hides the name and photo from others; you still see yourself. Friends see each other.
            var anonymous = hidden.Contains(id) && !isMe && !friendsOnly;
            return new LeaderboardEntryDto(rank, anonymous ? HiddenName : p?.Name ?? HiddenName, anonymous ? null : p?.PhotoUrl, points,
                tiers.GetValueOrDefault(id), isMe);
        }

        // Equal points share a rank (1, 2, 2, 4).
        var entries = new List<LeaderboardEntryDto>();
        for (var i = 0; i < top.Count; i++)
        {
            var rank = i > 0 && top[i].Points == top[i - 1].Points ? entries[i - 1].Rank : i + 1;
            entries.Add(Entry(top[i].UserId, rank, top[i].Points));
        }

        var mine = entries.FirstOrDefault(e => e.IsMe);
        if (mine is null)
        {
            var my = await totals.Where(x => x.UserId == userId).Select(x => (int?)x.Points).FirstOrDefaultAsync(ct);
            if (my is { } myPoints) mine = Entry(userId, await totals.CountAsync(x => x.Points > myPoints, ct) + 1, myPoints);
        }

        var scope = friendsOnly ? (languageId is null ? "Friends" : "Friends · " + await db.Languages.Where(x => x.Id == languageId).Select(x => x.Name).FirstOrDefaultAsync(ct))
            : languageId is null ? "All languages"
            : await db.Languages.Where(x => x.Id == languageId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "Language";
        return new LeaderboardDto(period, languageId, scope, since, entries, mine);
    }

    public async Task<Result> SetLeaderboardVisibilityAsync(Guid userId, bool show, CancellationToken ct = default)
    {
        var row = await db.UserPrivacySettings.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (row is null)
        {
            row = new UserPrivacySettings { UserId = userId };
            db.UserPrivacySettings.Add(row);
        }
        row.ShowOnLeaderboard = show;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ================================================================== admin (FR-S03, FR-S10)

    public async Task<IReadOnlyList<PointRule>> RulesAsync(CancellationToken ct = default) =>
        await db.PointRules.AsNoTracking().OrderBy(r => r.Action).ToListAsync(ct);

    public async Task<Result> UpdateRuleAsync(PointAction action, int points, int dailyCap, bool isActive, CancellationToken ct = default)
    {
        if (points is < 0 or > 10_000) return Error.Validation("Points", "Use 0 to 10,000 points.");
        if (dailyCap is < 0 or > 1_000) return Error.Validation("DailyCap", "Use 0 (no limit) to 1,000 times a day.");
        var rule = await db.PointRules.FirstOrDefaultAsync(r => r.Action == action, ct);
        if (rule is null) return Error.NotFound("Points rule");
        var old = new { rule.Points, rule.DailyCap, rule.IsActive };
        rule.Points = points;
        rule.DailyCap = dailyCap;
        rule.IsActive = isActive;
        rule.UpdatedAt = clock.GetUtcNow();
        rule.UpdatedByEmail = currentUser.Email;
        await db.SaveChangesAsync(ct);
        _rules = null;
        await audit.LogAsync("Update", "PointRule", action.ToString(), old, new { points, dailyCap, isActive }, ct);
        return Result.Success();
    }

    public async Task<Result<long>> AdjustAsync(Guid userId, int points, string reason, int? languageId, CancellationToken ct = default)
    {
        if (points == 0 || Math.Abs(points) > 100_000) return Error.Validation("Points", "Enter a non-zero amount up to 100,000.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Error.Validation("Reason", "A reason is required (FR-S10).");
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.UserType == UserType.Learner, ct)) return Error.NotFound("Learner");

        var row = new PointTransaction
        {
            UserId = userId, LanguageId = languageId, Action = PointAction.AdminAdjustment, Points = points,
            ReferenceType = "Admin", ReferenceId = Guid.NewGuid().ToString("N"), Note = reason.Trim(), CreatedByEmail = currentUser.Email,
            CreatedAt = clock.GetUtcNow()
        };
        row.ReferenceKey = PointMath.Key(row.Action, row.ReferenceType, row.ReferenceId);
        db.PointTransactions.Add(row);
        await db.SaveChangesAsync(ct);
        await RecalculateAsync(userId, ct);
        await audit.LogAsync("AdjustPoints", "Learner", userId.ToString(), null, new { points, reason, languageId }, ct);
        return row.Id;
    }

    public async Task<Result> RevokeAsync(long transactionId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required (FR-S10).");
        var row = await db.PointTransactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (row is null) return Error.NotFound("Points entry");
        if (row.IsRevoked) return Error.Conflict("already_revoked", "These points were already revoked.");
        row.IsRevoked = true;
        row.RevokedAt = clock.GetUtcNow();
        row.RevokedReason = reason.Trim();
        await db.SaveChangesAsync(ct);
        await RecalculateAsync(row.UserId, ct);
        await audit.LogAsync("RevokePoints", "PointTransaction", transactionId.ToString(), new { row.Points, Action = row.Action.ToString() },
            new { reason }, ct);
        return Result.Success();
    }

    public Task<IReadOnlyList<PointTransactionDto>> TransactionsAsync(Guid userId, int take, CancellationToken ct = default) =>
        HistoryAsync(userId, 1, ct);

    // ================================================================== helpers

    private async Task<Dictionary<PointAction, PointRule>> RuleMapAsync(CancellationToken ct) =>
        _rules ??= await db.PointRules.AsNoTracking().ToDictionaryAsync(r => r.Action, ct);

    private async Task<string> TimeZoneAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";

    private async Task<DateTimeOffset> DayStartAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        PracticeRules.DayStartUtc(now, await TimeZoneAsync(userId, ct));

    internal static TimeZoneInfo Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;
}
