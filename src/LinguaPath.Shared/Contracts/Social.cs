namespace LinguaPath.Shared.Contracts.Social;

// ------------------------------------------------------------------ profiles, friends, blocks, reports, privacy (FR-S20 .. FR-S24)

public record ProfileLanguageDto(string Language, CefrLevel Level);

/// <summary>FR-S20: what other learners can see. Never email or phone.</summary>
public record PublicProfileDto(
    Guid UserId,
    string DisplayName,
    string? PhotoUrl,
    string? Bio,
    IReadOnlyList<ProfileLanguageDto> Languages,
    int Points,
    PointTier Tier,
    IReadOnlyList<Progress.BadgeDto> Badges,
    RelationState Relation,
    bool CanMessage,
    bool IsBlockedAccount,
    bool? IsOnline,
    DateTimeOffset? LastSeenAt);

public record UserCardDto(Guid UserId, string DisplayName, string? PhotoUrl, string? Languages, RelationState Relation);

public record FriendRequestDto(int Id, UserCardDto User, bool Incoming, DateTimeOffset CreatedAt);

public record FriendsDto(IReadOnlyList<UserCardDto> Friends, IReadOnlyList<FriendRequestDto> Requests);

public record SendFriendRequest(Guid UserId);

public record RespondFriendRequest(bool Accept);

public record ReportRequest(Guid? UserId, long? MessageId, ReportReason Reason, string? Details);

public record PrivacyDto(AudienceSetting WhoCanMessage, AudienceSetting WhoCanChallenge, bool ShowOnLeaderboard, bool ShowOnline, bool ReadReceipts);

public record BlockedUserDto(Guid UserId, string DisplayName, DateTimeOffset BlockedAt);
