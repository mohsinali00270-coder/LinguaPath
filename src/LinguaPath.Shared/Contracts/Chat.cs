namespace LinguaPath.Shared.Contracts.Chat;

// ------------------------------------------------------------------ chat (FR-C01 .. FR-C32)

public record ReactionDto(string Emoji, int Count, IReadOnlyList<Guid> UserIds);

public record ReplyPreviewDto(long MessageId, string SenderName, string? Text, bool IsDeleted);

/// <summary>
/// A message as learners see it. Text is null for deleted or removed messages (FR-C12: the API never returns their text).
/// State is only filled for the viewer's own messages (FR-C03).
/// </summary>
public record MessageDto(
    long Id,
    int ConversationId,
    Guid? SenderId,
    string SenderName,
    string? SenderPhotoUrl,
    string? Text,
    MessageType Type,
    ReplyPreviewDto? ReplyTo,
    DateTimeOffset SentAt,
    bool IsDeleted,
    bool WasMasked,
    DeliveryState? State,
    IReadOnlyList<ReactionDto> Reactions,
    IReadOnlyList<Guid> Mentions,
    string? ClientId,
    int? CompetitionId = null);

public record MemberDto(Guid UserId, string DisplayName, string? PhotoUrl, MemberRole Role, bool IsPending, DateTimeOffset JoinedAt, bool? IsOnline);

public record ConversationSummaryDto(
    int Id,
    ConversationType Type,
    string Title,
    string? PhotoUrl,
    string? IconKey,
    string? LastMessage,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    bool IsMuted,
    bool IsArchived,
    Guid? OtherUserId,
    bool? OtherOnline);

public record ConversationDto(
    int Id,
    ConversationType Type,
    string Title,
    string? Description,
    string? IconKey,
    string? PhotoUrl,
    int? LanguageId,
    string? LanguageName,
    bool IsPublic,
    bool OnlyAdminsCanSend,
    int SlowModeSec,
    bool JoinApproval,
    MemberRole MyRole,
    bool IsMuted,
    bool IsArchived,
    bool CanSend,
    string? CannotSendReason,
    IReadOnlyList<MemberDto> Members,
    Guid? OtherUserId);

public record CreateDirectRequest(Guid UserId);

public record CreateGroupRequest(string Name, string? Description, string? IconKey, int? LanguageId, bool IsPublic, List<Guid>? MemberIds);

/// <summary>Group settings (owner/admins) and my own mute/archive. Null = unchanged.</summary>
public record UpdateConversationRequest(
    string? Name, string? Description, string? IconKey, int? LanguageId, bool? IsPublic, bool? OnlyAdminsCanSend, int? SlowModeSec,
    bool? JoinApproval, bool? Muted, bool? Archived);

public record SendMessageRequest(string Text, long? ReplyToMessageId, string? ClientId);

/// <summary>Why a message wasn't sent (FR-C31, FR-M11) or what happened to it (FR-M10).</summary>
public record ModerationNoticeDto(string Code, string Message, int Strikes, DateTimeOffset? SuspendedUntil, bool Blocked, bool MustAcknowledge);

public record AddMembersRequest(List<Guid> UserIds);

public record ChangeRoleRequest(MemberRole Role);

public record ReactionRequest(string Emoji);

public record PublicGroupDto(int Id, string Name, string? Description, string? IconKey, string? LanguageName, int Members, bool JoinApproval, bool IsMember);

public record SeenByDto(Guid UserId, string DisplayName, DateTimeOffset SeenAt);

public record TranslatedMessageDto(long MessageId, string Text, string From, string To, bool IsSample);

public record MessageSearchHitDto(long MessageId, int ConversationId, string ConversationTitle, string SenderName, string Snippet, DateTimeOffset SentAt);

// ------------------------------------------------------------------ hub events (/hubs/chat)

public record TypingDto(int ConversationId, Guid UserId, string DisplayName, bool IsTyping);

public record SeenDto(int ConversationId, Guid UserId, long LastSeenMessageId, DateTimeOffset SeenAt);

public record PresenceDto(Guid UserId, bool IsOnline, DateTimeOffset? LastSeenAt);

public record MessageDeletedDto(int ConversationId, long MessageId);

public record ConversationChangedDto(int ConversationId, string Change);

/// <summary>Strike warnings, suspensions and blocks pushed to the learner (FR-M12).</summary>
public record ModerationWarningDto(string Code, string Message, int Strikes, DateTimeOffset? SuspendedUntil, bool Blocked);

/// <summary>Answer to the hub's SendMessage call.</summary>
public record HubSendResult(bool Ok, MessageDto? Message, string? Code, string? Error);

/// <summary>GET /me/moderation: strikes, suspension and any warning I must accept (FR-M12).</summary>
public record MyModerationDto(int ActiveStrikes, DateTimeOffset? SuspendedUntil, bool Blocked, ModerationNoticeDto? PendingWarning);
