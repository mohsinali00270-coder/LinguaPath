namespace LinguaPath.Shared;

public enum FriendshipStatus
{
    Pending = 1,
    Accepted = 2,
    Rejected = 3
}

/// <summary>How the viewer relates to another learner (profile and search screens).</summary>
public enum RelationState
{
    None = 0,
    RequestSent = 1,
    RequestReceived = 2,
    Friends = 3,
    /// <summary>The viewer blocked this learner.</summary>
    Blocked = 4,
    Self = 5
}

public enum ConversationType
{
    Direct = 1,
    Group = 2
}

public enum MemberRole
{
    Member = 1,
    Admin = 2,
    Owner = 3
}

public enum MessageType
{
    Text = 1,
    /// <summary>"Ali joined", "Group renamed" (FR-C25).</summary>
    System = 2,
    /// <summary>A competition invitation or result shared into chat (FR-Q07, FR-C28); MessageDto.CompetitionId says which.</summary>
    CompetitionCard = 3
}

/// <summary>Moderation outcome stored on each message.</summary>
public enum MessageModeration
{
    /// <summary>Passed the word filter; waiting for the AI scan.</summary>
    Pending = 0,
    Clean = 1,
    /// <summary>Mild words were masked before delivery (FR-M10).</summary>
    Masked = 2,
    /// <summary>Hidden after delivery by the AI scan or a moderator.</summary>
    Removed = 3
}

/// <summary>Delivery state of my own message (FR-C03).</summary>
public enum DeliveryState
{
    Sent = 1,
    Delivered = 2,
    Seen = 3
}

/// <summary>FR-M01 word severities, also used for AI results.</summary>
public enum ModerationSeverity
{
    None = 0,
    Mild = 1,
    Abusive = 2,
    Severe = 3
}

public enum ModerationDetector
{
    WordFilter = 1,
    Ai = 2,
    Report = 3,
    Spam = 4,
    LinkFilter = 5,
    Moderator = 6
}

public enum ModerationTarget
{
    Message = 1,
    GroupName = 2,
    DisplayName = 3,
    Bio = 4,
    User = 5
}

/// <summary>What happened as a result (shown in the moderation queue).</summary>
public enum ModerationOutcome
{
    /// <summary>Waiting for a moderator (reports, borderline AI scores).</summary>
    Open = 0,
    Masked = 1,
    Rejected = 2,
    StrikeAdded = 3,
    Suspended = 4,
    Blocked = 5,
    Dismissed = 6,
    Removed = 7,
    Logged = 8
}

public enum ReportReason
{
    Abuse = 1,
    Spam = 2,
    Cheating = 3,
    Other = 4
}

/// <summary>What a moderator does with a queue item (FR-M22).</summary>
public enum ModeratorAction
{
    Dismiss = 1,
    AddStrike = 2,
    RemoveStrike = 3,
    Suspend = 4,
    Block = 5,
    RemoveMessage = 6
}
