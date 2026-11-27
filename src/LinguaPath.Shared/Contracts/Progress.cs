namespace LinguaPath.Shared.Contracts.Progress;

// ------------------------------------------------------------------ points wallet, streak, badges (FR-S01 .. FR-S10, FR-L111, BR-09)

public record StreakDto(int Current, int Longest, DateOnly? LastActivityDate, bool ActiveToday, bool AtRisk);

public record BadgeDto(BadgeKey Key, string Icon, string Title, string Description, DateTimeOffset? EarnedAt)
{
    public bool Earned => EarnedAt is not null;
}

public record LanguagePointsDto(int? LanguageId, string Name, int Points);

public record PointsSummaryDto(
    int Total,
    PointTier Tier,
    PointTier? NextTier,
    int? PointsToNextTier,
    int TierProgressPercent,
    int WeeklyPoints,
    int MonthlyPoints,
    int TodayCapped,
    int DailyCap,
    IReadOnlyList<LanguagePointsDto> ByLanguage,
    StreakDto Streak,
    IReadOnlyList<BadgeDto> Badges,
    bool ShowOnLeaderboard);

public record PointTransactionDto(long Id, PointAction Action, string Description, string? LanguageName, int Points, DateTimeOffset CreatedAt,
    bool IsRevoked);

public record LeaderboardEntryDto(int Rank, string DisplayName, string? PhotoUrl, int Points, PointTier Tier, bool IsMe);

public record LeaderboardDto(
    LeaderboardPeriod Period,
    int? LanguageId,
    string Scope,
    DateTimeOffset? Since,
    IReadOnlyList<LeaderboardEntryDto> Entries,
    LeaderboardEntryDto? Me);

public record LeaderboardVisibilityRequest(bool Show);

/// <summary>What a learner earned from one action, shown as "+15 points" in the app.</summary>
public record PointsEarnedDto(int Points, IReadOnlyList<BadgeDto> NewBadges, int Streak);
