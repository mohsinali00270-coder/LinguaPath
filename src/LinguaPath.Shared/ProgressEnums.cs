namespace LinguaPath.Shared;

/// <summary>Why points were given or taken (FR-S03). Stored as text in PointTransactions.Action.</summary>
public enum PointAction
{
    LectureCompleted = 1,
    StudyAheadBonus = 2,
    DailyQuiz = 3,
    DailyQuizPerfect = 4,
    MonthlyExamPassed = 5,
    ExamGradeBonus = 6,
    PracticeSession = 7,
    StreakDay = 8,
    StreakWeek = 9,
    StreakMonth = 10,
    CertificateEarned = 11,
    Referral = 12,
    CompetitionWin = 13,
    CompetitionDraw = 14,
    CompetitionLoss = 15,
    TournamentPrize = 16,
    /// <summary>Top 3 of a group battle (FR-Q10).</summary>
    GroupBattleBonus = 17,
    /// <summary>Added or deducted by an admin with a reason (FR-S10).</summary>
    AdminAdjustment = 20
}

/// <summary>Levels from total points (FR-S06).</summary>
public enum PointTier
{
    Bronze = 0,
    Silver = 1,
    Gold = 2,
    Platinum = 3,
    Diamond = 4
}

/// <summary>Badges (FR-L111, FR-S06).</summary>
public enum BadgeKey
{
    FirstLecture = 1,
    Streak7 = 2,
    Streak30 = 3,
    FirstExamPassed = 4,
    FirstCertificate = 5,
    PracticeRegular = 6,
    TierSilver = 10,
    TierGold = 11,
    TierPlatinum = 12,
    TierDiamond = 13
}

/// <summary>Certificate grade band (BR-07).</summary>
public enum GradeBand
{
    Pass = 1,
    Merit = 2,
    Distinction = 3
}

public enum LeaderboardPeriod
{
    Weekly = 1,
    Monthly = 2,
    AllTime = 3
}

/// <summary>Who may message or challenge a learner (FR-S24).</summary>
public enum AudienceSetting
{
    Everyone = 0,
    FriendsOnly = 1,
    Nobody = 2
}
