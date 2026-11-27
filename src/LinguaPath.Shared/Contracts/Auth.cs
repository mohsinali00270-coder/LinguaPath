namespace LinguaPath.Shared.Contracts.Auth;

// FR-L01 Register with email + password, verify by 6-digit OTP.
public record RegisterRequest(
    string FullName,
    string Email,
    string Password,
    int? NativeLanguageId,
    string? TimeZone,
    string? UiLanguage,
    DeviceInfoDto? Device = null,
    DateOnly? DateOfBirth = null);

public record RegisterResponse(string Email, DateTimeOffset OtpExpiresAt, int ResendAfterSeconds);

public record VerifyEmailRequest(string Email, string Code, DeviceInfoDto? Device);

public record ResendOtpRequest(string Email, OtpPurpose Purpose);

public record ResendOtpResponse(DateTimeOffset OtpExpiresAt, int ResendAfterSeconds);

// FR-L02 Email/password, Google and Apple sign-in.
public record LoginRequest(string Email, string Password, DeviceInfoDto? Device);

/// <summary>Google or Apple ID token obtained natively on the device.</summary>
public record ExternalLoginRequest(string IdToken, string? FullName, DeviceInfoDto? Device);

// FR-L03 JWT access token + rotating refresh token.
public record RefreshTokenRequest(string RefreshToken, string? DeviceId);

public record LogoutRequest(string RefreshToken);

// FR-L04 Forgot / reset password via email OTP.
public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Code, string NewPassword);

public record DeviceInfoDto(string DeviceId, DevicePlatform Platform, string? AppVersion, string? PushToken);

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserSummaryDto User);

public record UserSummaryDto(
    Guid Id,
    string Email,
    string FullName,
    string? DisplayName,
    string? PhotoUrl,
    string UiLanguage,
    IReadOnlyList<string> Roles,
    bool HasActiveCourse);
