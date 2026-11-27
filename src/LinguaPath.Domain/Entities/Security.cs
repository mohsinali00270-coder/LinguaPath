using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>
/// Refresh token (FR-L03). Only the SHA-256 hash is stored. Tokens rotate on every refresh;
/// all tokens issued from one sign-in share a FamilyId so a reused (stolen) token revokes the whole family.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public string? DeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByIp { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>Device + push token (SRS 7.1 UserDevices). DeviceId is also used for re-registration blocks (FR-M15).</summary>
public class UserDevice
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public string DeviceId { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public string? PushToken { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Email one-time code for verification and password reset (FR-L01, FR-L04). Code is stored hashed.</summary>
public class EmailOtp
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public OtpPurpose Purpose { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>Identities that may never register again after a permanent block (FR-M15, BR-28).</summary>
public class BlockedIdentity
{
    public int Id { get; set; }
    public string? NormalizedEmail { get; set; }
    /// <summary>"google:{subject}" or "apple:{subject}".</summary>
    public string? ExternalLoginKey { get; set; }
    public string? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
