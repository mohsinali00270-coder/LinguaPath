namespace LinguaPath.Application.Common;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "LinguaPath";
    public string Audience { get; set; } = "LinguaPath.Mobile";
    /// <summary>HMAC-SHA256 key, at least 32 characters. Keep in user-secrets / Key Vault, never in source control.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;   // FR-L03
    public int RefreshTokenDays { get; set; } = 30;     // FR-L03
}

public class OtpOptions
{
    public const string Section = "Otp";

    public int Length { get; set; } = 6;
    public int ExpiryMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
}

public class AppOptions
{
    public const string Section = "App";

    public string Name { get; set; } = "LinguaPath";
    /// <summary>Public base URL of the API, used to build absolute links to uploaded files.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7080";
    public string SupportEmail { get; set; } = "support@linguapath.app";
    /// <summary>Custom URI scheme the mobile app registers for the Google web sign-in callback.</summary>
    public string MobileCallbackScheme { get; set; } = "linguapath";
}

public class ExternalAuthOptions
{
    public const string Section = "ExternalAuth";

    /// <summary>Accepted Google OAuth client ids (web, Android, iOS) for ID-token validation.</summary>
    public string[] GoogleClientIds { get; set; } = [];
    /// <summary>Accepted Apple audiences: the iOS bundle id and, if used, the Services ID.</summary>
    public string[] AppleAudiences { get; set; } = [];

    public bool GoogleEnabled => GoogleClientIds.Length > 0;
    public bool AppleEnabled => AppleAudiences.Length > 0;
}

public class SeedOptions
{
    public const string Section = "Seed";

    public string SuperAdminEmail { get; set; } = "admin@linguapath.local";
    /// <summary>Only used the first time the database is created. Change it after first login.</summary>
    public string SuperAdminPassword { get; set; } = "Admin@12345";
    public string SuperAdminName { get; set; } = "Super Admin";

    /// <summary>Development only: publishes Spanish with a sample A1 → A2 roadmap so onboarding can be tried at once.</summary>
    public bool SampleContent { get; set; }
}
