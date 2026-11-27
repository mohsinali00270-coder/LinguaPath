using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>
/// One line of the shared points ledger (FR-S01, FR-S02, BR-20). Totals are never edited; they are sums of these rows.
/// ReferenceKey makes awards idempotent: the same action for the same thing is only given once.
/// </summary>
public class PointTransaction
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The language the points were earned in; null for account-wide actions (streaks, admin adjustments).</summary>
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public PointAction Action { get; set; }
    public int Points { get; set; }
    /// <summary>What it was for, e.g. "RoadmapItem", "QuizAttempt", "Quiz", "Certificate", "Streak".</summary>
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    /// <summary>"{Action}:{ReferenceType}:{ReferenceId}", unique per learner.</summary>
    public string ReferenceKey { get; set; } = string.Empty;
    /// <summary>Counts toward the daily cap (BR-22: everything except exams, certificates, tournament prizes and admin changes).</summary>
    public bool IsCapped { get; set; }
    public string? Note { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>Cached totals for profiles and the all-time leaderboard; rebuilt from the ledger whenever it changes.</summary>
public class UserPointsSummary
{
    public Guid UserId { get; set; }
    public int TotalPoints { get; set; }
    public PointTier Tier { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Points per action, editable in the admin panel (FR-S03).</summary>
public class PointRule
{
    public PointAction Action { get; set; }
    public int Points { get; set; }
    /// <summary>How many times a day the action earns points (0 = no limit), e.g. practice: 4.</summary>
    public int DailyCap { get; set; }
    public bool IsActive { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedByEmail { get; set; }
}

/// <summary>Daily streak (BR-09): a day counts when the learner completes a lecture or a daily quiz.</summary>
public class UserStreak
{
    public Guid UserId { get; set; }
    public int CurrentStreak { get; set; }
    public int LongestStreak { get; set; }
    public DateOnly? LastActivityDate { get; set; }
    public DateOnly? StreakStartDate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class UserBadge
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public BadgeKey Badge { get; set; }
    public DateTimeOffset EarnedAt { get; set; }
}

/// <summary>Privacy choices (FR-S24). Phase 7 uses the leaderboard switch; chat and challenges use the rest later.</summary>
public class UserPrivacySettings
{
    public Guid UserId { get; set; }
    public bool ShowOnLeaderboard { get; set; } = true;
    public AudienceSetting WhoCanMessage { get; set; } = AudienceSetting.Everyone;
    public AudienceSetting WhoCanChallenge { get; set; } = AudienceSetting.Everyone;
    public bool ShowOnline { get; set; } = true;
    public bool ReadReceipts { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Certificate design (FR-A82). LanguageId null = the default for every language.</summary>
public class CertificateTemplate : AuditableEntity<int>
{
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Heading { get; set; } = "Certificate of Achievement";
    /// <summary>Body text with placeholders: {Name}, {Language}, {Course}, {Level}, {Grade}, {Band}, {Duration}, {CompletedOn}, {Number}.</summary>
    public string BodyText { get; set; } = string.Empty;
    public string? SignatoryName { get; set; }
    public string? SignatoryTitle { get; set; }
    public string? SignatureUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? BackgroundUrl { get; set; }
    /// <summary>Hex colour for borders and headings, e.g. "#4F46E5".</summary>
    public string AccentColor { get; set; } = "#4F46E5";
    public bool IsActive { get; set; } = true;
}

/// <summary>An issued certificate (SRS 7.5). Numbers are never reused, also after revoking.</summary>
public class Certificate
{
    public int Id { get; set; }
    /// <summary>LP-{LANG}-{YYYY}-{6-digit sequence}, e.g. LP-ES-2026-000123.</summary>
    public string CertificateNo { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public int LearnerCourseId { get; set; }
    public LearnerCourse LearnerCourse { get; set; } = null!;
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public int? TemplateId { get; set; }
    /// <summary>Name as printed, frozen at issue (a later profile change doesn't alter an issued certificate until reissue).</summary>
    public string LearnerName { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public CefrLevel LevelAchieved { get; set; }
    public int FinalGrade { get; set; }
    public GradeBand GradeBand { get; set; }
    public DateOnly StartedOn { get; set; }
    /// <summary>Actual completion date (BR-12).</summary>
    public DateOnly CompletedOn { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    /// <summary>Not stored: PDFs are rendered on request so they always match the current record. Kept for the SRS schema.</summary>
    public string? PdfUrl { get; set; }
    /// <summary>Printed on the certificate and in the QR code; needed to see the details on the public page.</summary>
    public string VerificationCode { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public DateTimeOffset? ReissuedAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? RevokedByEmail { get; set; }
}
