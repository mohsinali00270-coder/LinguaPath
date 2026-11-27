using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

// ===================================================================== friends, blocks, reports (FR-S21 .. FR-S23)

public class Friendship
{
    public int Id { get; set; }
    public Guid RequesterId { get; set; }
    public ApplicationUser Requester { get; set; } = null!;
    public Guid AddresseeId { get; set; }
    public ApplicationUser Addressee { get; set; } = null!;
    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>FR-S22: the blocked learner can't message, challenge or see the blocker's profile.</summary>
public class UserBlock
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class UserReport
{
    public int Id { get; set; }
    public Guid ReporterId { get; set; }
    public Guid ReportedUserId { get; set; }
    public long? MessageId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Details { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public string? ReviewedByEmail { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? ModerationEventId { get; set; }
}

// ===================================================================== chat (FR-C01 .. FR-C28, SRS 11.7)

public class Conversation
{
    public int Id { get; set; }
    public ConversationType Type { get; set; }
    /// <summary>Direct chats: "{smallerUserId}:{largerUserId}", unique, so two learners share one chat.</summary>
    public string? DirectKey { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Built-in icon (FR-C20: no uploads).</summary>
    public string? IconKey { get; set; }
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public bool IsPublic { get; set; }
    public bool OnlyAdminsCanSend { get; set; }
    public int SlowModeSec { get; set; }
    public bool JoinApproval { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? LastMessageId { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<ConversationMember> Members { get; set; } = new List<ConversationMember>();
}

public class ConversationMember
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public MemberRole Role { get; set; } = MemberRole.Member;
    /// <summary>Asked to join a public group with join approval (FR-C23).</summary>
    public bool IsPending { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public long? LastReadMessageId { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    /// <summary>Messages before this one are hidden for this member ("clear chat", FR-C08).</summary>
    public long? ClearedBeforeMessageId { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public bool IsMuted { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>False after leaving or being removed; the row stays for history.</summary>
    public bool IsActive { get; set; } = true;
}

public class Message
{
    public long Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    /// <summary>Null for system messages.</summary>
    public Guid? SenderId { get; set; }
    public ApplicationUser? Sender { get; set; }
    /// <summary>What recipients see (mild words masked).</summary>
    public string Body { get; set; } = string.Empty;
    /// <summary>What the sender typed, kept for moderators when it was masked (FR-C12).</summary>
    public string? OriginalBody { get; set; }
    public long? ReplyToMessageId { get; set; }
    public MessageType MessageType { get; set; } = MessageType.Text;
    /// <summary>Mentioned user ids, comma-separated (FR-C26).</summary>
    public string? Mentions { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Competition cards (FR-Q07, FR-C28).</summary>
    public int? CompetitionId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    /// <summary>False after "delete for everyone" or removal by moderation; never physically deleted (FR-C12).</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeletedForEveryoneAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public MessageModeration ModerationStatus { get; set; }
    public DateTimeOffset? AiScannedAt { get; set; }
}

/// <summary>Per recipient: delivered/seen and "delete for me" (FR-C03, FR-C10).</summary>
public class MessageRecipientState
{
    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public Guid UserId { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? SeenAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public class MessageReaction
{
    public long MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

// ===================================================================== moderation (FR-M01 .. FR-M26)

public class AbusiveWord
{
    public int Id { get; set; }
    /// <summary>Null = every language.</summary>
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public string Term { get; set; } = string.Empty;
    public string NormalizedTerm { get; set; } = string.Empty;
    public ModerationSeverity Severity { get; set; } = ModerationSeverity.Abusive;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>FR-M25: regex patterns that block links/contacts, plus exceptions (allowed) that win over them.</summary>
public class BlockedLinkPattern
{
    public int Id { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>True = an allowed exception (none by default).</summary>
    public bool IsException { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class ModerationEvent
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public ModerationTarget EntityType { get; set; }
    public string? EntityId { get; set; }
    public int? ConversationId { get; set; }
    /// <summary>The text that was checked (kept for review even if never delivered).</summary>
    public string? Text { get; set; }
    public ModerationDetector DetectedBy { get; set; }
    public string? Category { get; set; }
    public ModerationSeverity Severity { get; set; }
    public double? Score { get; set; }
    public string? MatchedTerms { get; set; }
    public ModerationOutcome ActionTaken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}

public class ModerationStrike
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public long? ModerationEventId { get; set; }
    public ModerationSeverity Severity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>The learner has seen the warning screen (FR-M12, first strike).</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? RemovedBy { get; set; }
    public string? RemovedReason { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}
