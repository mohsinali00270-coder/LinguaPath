using System.ComponentModel.DataAnnotations;

namespace LinguaPath.Application.Features.Admin;

public record DashboardStats(
    int TotalLearners,
    int ActiveToday,
    int ActiveLast30Days,
    int NewThisWeek,
    int UnverifiedLearners,
    int SuspendedLearners,
    int BlockedLearners,
    int AdminUsers,
    int PublishedLanguages,
    int ComingSoonLanguages,
    IReadOnlyList<DailyCount> SignupsLast14Days,
    int ActiveEnrolments = 0,
    int PublishedCourses = 0,
    int PendingLanguageRequests = 0,
    int PendingReviews = 0,
    int OpenReports = 0,
    int PendingGrading = 0);

public record DailyCount(DateOnly Day, int Count);

public record AdminUserListItem(
    Guid Id, string Email, string FullName, IReadOnlyList<string> Roles, bool IsActive, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

public class CreateAdminUserInput
{
    [Required, StringLength(100)]
    [Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    [DataType(DataType.Password)]
    [Display(Name = "Temporary password")]
    public string Password { get; set; } = string.Empty;

    [MinLength(1, ErrorMessage = "Pick at least one role.")]
    public List<string> Roles { get; set; } = [];
}

public class EditAdminUserInput
{
    public Guid Id { get; set; }

    [Required, StringLength(100)]
    [Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [MinLength(1, ErrorMessage = "Pick at least one role.")]
    public List<string> Roles { get; set; } = [];

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;
}

public record LearnerListItem(
    Guid Id, string Email, string FullName, string? NativeLanguage, bool EmailConfirmed, bool IsActive, bool IsBlocked,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public record LearnerDetails(
    Guid Id, string Email, string FullName, string? DisplayName, string? NativeLanguage, string UiLanguage, string TimeZone,
    bool EmailConfirmed, bool IsActive, bool IsBlocked, string? BlockReason, DateTimeOffset? BlockedAt,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, IReadOnlyList<string> ExternalLogins,
    IReadOnlyList<LearnerDevice> Devices, int ActiveSessions,
    IReadOnlyList<LearnerCourseSummary>? Courses = null);

public record LearnerCourseSummary(string Language, string Course, string Levels, string Status, int ProgressPercent, DateOnly EndDate, int Id = 0);

public record LearnerDevice(string DeviceId, string Platform, string? AppVersion, DateTimeOffset LastSeenAt);

public enum LearnerStatusFilter
{
    All,
    Active,
    Unverified,
    Suspended,
    Blocked
}

public record AuditLogItem(
    long Id, DateTimeOffset CreatedAt, string? UserEmail, string Action, string EntityType, string? EntityId,
    string? OldValues, string? NewValues, string? IpAddress);

public record AuditLogFilter(string? Search, string? EntityType, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 50);
