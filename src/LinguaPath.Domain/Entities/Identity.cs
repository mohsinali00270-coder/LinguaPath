using LinguaPath.Shared;
using Microsoft.AspNetCore.Identity;

namespace LinguaPath.Domain.Entities;

/// <summary>AspNetUsers extended per SRS 7.1 and 11.7.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? PhotoUrl { get; set; }

    public int? NativeLanguageId { get; set; }
    public Language? NativeLanguage { get; set; }

    /// <summary>App UI language: "en" or "ur" at launch (NFR-10).</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>IANA time zone id, e.g. "Asia/Karachi". Daily quizzes and streaks use it.</summary>
    public string TimeZone { get; set; } = "UTC";

    public TimeOnly? PreferredStudyTime { get; set; }

    /// <summary>Optional (BR-32). Under 13 can't register; under 18 can chat with friends only.</summary>
    public DateOnly? DateOfBirth { get; set; }

    public UserType UserType { get; set; } = UserType.Learner;

    /// <summary>Admin-controlled switch (suspend). Separate from permanent blocking.</summary>
    public bool IsActive { get; set; } = true;

    // Moderation (FR-M14)
    public bool IsBlocked { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public string? BlockReason { get; set; }
    public DateTimeOffset? ChatSuspendedUntil { get; set; }

    // Notification preferences (FR-L05, FR-L110)
    public bool NotifyStudyReminders { get; set; } = true;
    public bool NotifyQuizReady { get; set; } = true;
    public bool NotifyExamReminders { get; set; } = true;
    public bool NotifyStreakAlerts { get; set; } = true;
    public bool NotifyCertificates { get; set; } = true;
    public bool NotifyNewPosts { get; set; } = true;

    // Account lifecycle (FR-L06)
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<UserDevice> Devices { get; set; } = new List<UserDevice>();
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string name, string? description = null) : base(name) => Description = description;

    public string? Description { get; set; }
}
