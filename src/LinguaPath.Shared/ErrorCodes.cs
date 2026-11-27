namespace LinguaPath.Shared;

/// <summary>Stable machine-readable error codes returned in Problem Details "code".</summary>
public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string NotFound = "not_found";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string ServerError = "server_error";

    public const string InvalidCredentials = "invalid_credentials";
    public const string EmailNotVerified = "email_not_verified";
    public const string EmailAlreadyRegistered = "email_already_registered";
    public const string AccountBlocked = "account_blocked";
    public const string AccountInactive = "account_inactive";
    public const string AccountLocked = "account_locked";
    public const string RegistrationBlocked = "registration_blocked";
    public const string InvalidOtp = "invalid_otp";
    public const string OtpExpired = "otp_expired";
    public const string OtpTooManyAttempts = "otp_too_many_attempts";
    public const string OtpResendTooSoon = "otp_resend_too_soon";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string ExternalLoginFailed = "external_login_failed";
    public const string ExternalLoginNotConfigured = "external_login_not_configured";
    public const string UpdateRequired = "update_required";
}
