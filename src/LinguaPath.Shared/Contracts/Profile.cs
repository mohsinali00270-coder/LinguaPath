namespace LinguaPath.Shared.Contracts.Profile;

// FR-L05 Profile: name, photo, native language, UI language, time zone, notification preferences.
public record UserProfileDto(
    Guid Id,
    string Email,
    string FullName,
    string? DisplayName,
    string? Bio,
    string? PhotoUrl,
    int? NativeLanguageId,
    string? NativeLanguageName,
    string UiLanguage,
    string TimeZone,
    TimeOnly? PreferredStudyTime,
    NotificationPreferencesDto Notifications,
    bool HasPassword,
    IReadOnlyList<string> ExternalLogins,
    DateTimeOffset CreatedAt,
    DateOnly? DateOfBirth = null);

public record NotificationPreferencesDto(
    bool StudyReminders,
    bool QuizReady,
    bool ExamReminders,
    bool StreakAlerts,
    bool Certificates,
    bool NewPosts);

public record UpdateProfileRequest(
    string FullName,
    string? DisplayName,
    string? Bio,
    int? NativeLanguageId,
    string UiLanguage,
    string TimeZone,
    TimeOnly? PreferredStudyTime,
    NotificationPreferencesDto Notifications,
    DateOnly? DateOfBirth = null);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Learners who signed up with Google/Apple only have no password; they confirm with the word DELETE.</summary>
public record DeleteAccountRequest(string? Password, string Confirmation);

public record PhotoUploadResponse(string PhotoUrl);
