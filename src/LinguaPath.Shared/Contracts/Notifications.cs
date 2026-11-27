namespace LinguaPath.Shared.Contracts.Notifications;

// ------------------------------------------------------------------ in-app inbox (FR-L112)

/// <summary>
/// One inbox entry. <see cref="Data"/> carries what the app needs to open the right screen:
/// "type" plus ids such as "learnerCourseId", "certificateId", "postId" or "examId".
/// </summary>
public record NotificationDto(
    long Id,
    NotificationType Type,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Data,
    DateTimeOffset CreatedAt,
    bool IsRead);

public record NotificationPageDto(IReadOnlyList<NotificationDto> Items, int UnreadCount, bool HasMore);

public record UnreadCountDto(int Count);

// ------------------------------------------------------------------ client crash reports (NFR-14 monitoring)

/// <summary>An unhandled error from the app. Logged on the server; nothing personal beyond the user id.</summary>
public record ClientErrorReport(
    string Platform,
    string AppVersion,
    string? OsVersion,
    string? DeviceModel,
    string Message,
    string? ExceptionType,
    string? StackTrace,
    string? Page,
    DateTimeOffset OccurredAt);
