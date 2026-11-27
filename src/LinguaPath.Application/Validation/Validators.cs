using FluentValidation;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;
using LinguaPath.Shared.Contracts.Profile;

namespace LinguaPath.Application.Validation;

internal static class Rules
{
    public static readonly string[] UiLanguages = ["en", "ur"];

    /// <summary>Same policy as ASP.NET Identity options: 8+ chars, upper, lower, digit.</summary>
    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty().WithMessage("Password is required.")
        .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
        .MaximumLength(100)
        .Matches("[A-Z]").WithMessage("Password needs at least one capital letter.")
        .Matches("[a-z]").WithMessage("Password needs at least one small letter.")
        .Matches("[0-9]").WithMessage("Password needs at least one number.");

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty().WithMessage("Email is required.")
        .MaximumLength(256)
        .EmailAddress().WithMessage("Enter a valid email address.");

    public static IRuleBuilderOptions<T, string?> ValidTimeZone<T>(this IRuleBuilder<T, string?> rule) => rule
        .Must(tz => string.IsNullOrWhiteSpace(tz) || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
        .WithMessage("Unknown time zone.");

    /// <summary>BR-32: optional, but when given it must be real and the learner at least 13.</summary>
    public static IRuleBuilderOptions<T, DateOnly?> ValidDateOfBirth<T>(this IRuleBuilder<T, DateOnly?> rule) => rule
        .Must(d => d is null || LinguaPath.Shared.AgeRules.IsPlausible(d.Value, DateOnly.FromDateTime(DateTime.UtcNow)))
        .WithMessage("Enter a real date of birth.")
        .Must(d => !LinguaPath.Shared.AgeRules.IsTooYoung(d, DateOnly.FromDateTime(DateTime.UtcNow)))
        .WithMessage($"You need to be at least {LinguaPath.Shared.AgeRules.MinimumAge} to use LinguaPath.");

    public static IRuleBuilderOptions<T, string> Otp<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty().WithMessage("Enter the code from your email.")
        .Matches("^[0-9]{6}$").WithMessage("The code has 6 digits.");
}

public sealed class DeviceInfoValidator : AbstractValidator<DeviceInfoDto>
{
    public DeviceInfoValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.AppVersion).MaximumLength(32);
        RuleFor(x => x.PushToken).MaximumLength(512);
    }
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Name is required.").MaximumLength(100);
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.TimeZone).ValidTimeZone();
        RuleFor(x => x.UiLanguage).Must(l => l is null || Rules.UiLanguages.Contains(l)).WithMessage("UI language must be en or ur.");
        RuleFor(x => x.Device!).SetValidator(new DeviceInfoValidator()).When(x => x.Device is not null);
        RuleFor(x => x.DateOfBirth).ValidDateOfBirth();
    }
}

public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Code).Otp();
        RuleFor(x => x.Device!).SetValidator(new DeviceInfoValidator()).When(x => x.Device is not null);
    }
}

public sealed class ResendOtpRequestValidator : AbstractValidator<ResendOtpRequest>
{
    public ResendOtpRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Purpose).IsInEnum();
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
        RuleFor(x => x.Device!).SetValidator(new DeviceInfoValidator()).When(x => x.Device is not null);
    }
}

public sealed class ExternalLoginRequestValidator : AbstractValidator<ExternalLoginRequest>
{
    public ExternalLoginRequestValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty().MaximumLength(8192);
        RuleFor(x => x.FullName).MaximumLength(100);
        RuleFor(x => x.Device!).SetValidator(new DeviceInfoValidator()).When(x => x.Device is not null);
    }
}

public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() => RuleFor(x => x.Email).ValidEmail();
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Code).Otp();
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Name is required.").MaximumLength(100);
        RuleFor(x => x.DisplayName).MaximumLength(50);
        RuleFor(x => x.Bio).MaximumLength(300);
        RuleFor(x => x.UiLanguage).Must(l => Rules.UiLanguages.Contains(l)).WithMessage("UI language must be en or ur.");
        RuleFor(x => x.TimeZone).NotEmpty().ValidTimeZone();
        RuleFor(x => x.Notifications).NotNull();
        RuleFor(x => x.DateOfBirth).ValidDateOfBirth();
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotNull();
        RuleFor(x => x.NewPassword).StrongPassword()
            .NotEqual(x => x.CurrentPassword).WithMessage("New password must be different from the current one.");
    }
}

public sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator() => RuleFor(x => x.Confirmation).NotEmpty();
}

public sealed class RegisterDeviceRequestValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.AppVersion).MaximumLength(32);
        RuleFor(x => x.PushToken).MaximumLength(512);
    }
}
