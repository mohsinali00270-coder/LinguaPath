using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Auth;

public interface IAuthService
{
    Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct = default);
    Task<Result<ResendOtpResponse>> ResendOtpAsync(ResendOtpRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> ExternalLoginAsync(string provider, ExternalLoginRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> SignInExternalUserAsync(ExternalUserInfo info, string? fullName, DeviceInfoDto? device, CancellationToken ct = default);
    Task<Result<AuthResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task<Result> LogoutAsync(Guid userId, LogoutRequest request, CancellationToken ct = default);
    Task<Result> LogoutAllAsync(Guid userId, CancellationToken ct = default);
    Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default);
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
}

/// <summary>Account and token flows for learners (FR-L01 .. FR-L04).</summary>
internal sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    ITokenService tokens,
    IOtpService otp,
    IEmailSender email,
    IExternalAuthValidator externalAuth,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<JwtOptions> jwtOptions,
    IOptions<OtpOptions> otpOptions,
    IOptions<AppOptions> appOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly OtpOptions _otp = otpOptions.Value;
    private readonly AppOptions _app = appOptions.Value;

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---------------------------------------------------------------- register / verify

    public async Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var normalizedEmail = userManager.NormalizeEmail(request.Email.Trim());

        if (await IsBlockedIdentityAsync(normalizedEmail, null, request.Device?.DeviceId, ct))
            return Error.Forbidden(ErrorCodes.RegistrationBlocked, "This account cannot be registered.");

        if (request.NativeLanguageId is { } langId &&
            !await db.Languages.AnyAsync(l => l.Id == langId && !l.IsDeleted, ct))
            return Error.Validation(nameof(request.NativeLanguageId), "Selected native language does not exist.");

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is not null)
        {
            if (user.EmailConfirmed || user.IsDeleted || user.IsBlocked)
                return Error.Conflict(ErrorCodes.EmailAlreadyRegistered, "An account with this email already exists. Please sign in.");

            // Unverified earlier sign-up: let the person start again with the new details.
            user.FullName = request.FullName.Trim();
            user.NativeLanguageId = request.NativeLanguageId;
            user.TimeZone = request.TimeZone ?? user.TimeZone;
            user.UiLanguage = request.UiLanguage ?? user.UiLanguage;
            user.DateOfBirth = request.DateOfBirth;
            if (await userManager.HasPasswordAsync(user))
                await userManager.RemovePasswordAsync(user);
            var pwd = await userManager.AddPasswordAsync(user, request.Password);
            if (!pwd.Succeeded) return pwd.ToError();
            await userManager.UpdateAsync(user);
        }
        else
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = request.Email.Trim(),
                Email = request.Email.Trim(),
                FullName = request.FullName.Trim(),
                NativeLanguageId = request.NativeLanguageId,
                TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone,
                UiLanguage = string.IsNullOrWhiteSpace(request.UiLanguage) ? "en" : request.UiLanguage,
                UserType = UserType.Learner,
                DateOfBirth = request.DateOfBirth,
                CreatedAt = Now
            };
            var created = await userManager.CreateAsync(user, request.Password);
            if (!created.Succeeded) return created.ToError();
            await userManager.AddToRoleAsync(user, Roles.Learner);
        }

        var (code, expiresAt) = await otp.CreateAsync(user.Id, OtpPurpose.VerifyEmail, ct);
        var (subject, html) = EmailTemplates.VerifyEmail(_app.Name, user.FullName, code, _otp.ExpiryMinutes);
        await email.SendAsync(user.Email!, subject, html, ct);

        return new RegisterResponse(user.Email!, expiresAt, _otp.ResendCooldownSeconds);
    }

    public async Task<Result<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.IsDeleted)
            return Error.Validation(nameof(request.Code), "The code is incorrect.");
        if (user.EmailConfirmed)
            return Error.Conflict(ErrorCodes.Conflict, "This email is already verified. Please sign in.");

        var check = await otp.VerifyAsync(user.Id, OtpPurpose.VerifyEmail, request.Code, ct);
        if (check.IsFailure) return check.Error!;

        user.EmailConfirmed = true;
        await userManager.UpdateAsync(user);

        var gate = CheckCanSignIn(user);
        if (gate is not null) return gate;

        return await IssueTokensAsync(user, request.Device, ct);
    }

    public async Task<Result<ResendOtpResponse>> ResendOtpAsync(ResendOtpRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());

        // Do not reveal whether the email exists: unknown emails get a normal-looking answer.
        var fakeAnswer = new ResendOtpResponse(Now.AddMinutes(_otp.ExpiryMinutes), _otp.ResendCooldownSeconds);
        if (user is null || user.IsDeleted || user.IsBlocked) return fakeAnswer;
        if (request.Purpose == OtpPurpose.VerifyEmail && user.EmailConfirmed) return fakeAnswer;

        var wait = await otp.SecondsUntilResendAllowedAsync(user.Id, request.Purpose, ct);
        if (wait > 0)
            return Error.TooManyRequests(ErrorCodes.OtpResendTooSoon, $"Please wait {wait} seconds before requesting a new code.");

        var (code, expiresAt) = await otp.CreateAsync(user.Id, request.Purpose, ct);
        var (subject, html) = request.Purpose == OtpPurpose.VerifyEmail
            ? EmailTemplates.VerifyEmail(_app.Name, user.FullName, code, _otp.ExpiryMinutes)
            : EmailTemplates.ResetPassword(_app.Name, user.FullName, code, _otp.ExpiryMinutes);
        await email.SendAsync(user.Email!, subject, html, ct);

        return new ResendOtpResponse(expiresAt, _otp.ResendCooldownSeconds);
    }

    // ---------------------------------------------------------------- login

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var invalid = Error.Unauthorized(ErrorCodes.InvalidCredentials, "Email or password is incorrect.");

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.IsDeleted) return invalid;

        if (await userManager.IsLockedOutAsync(user))
            return Error.Forbidden(ErrorCodes.AccountLocked, "Too many failed attempts. Try again in a few minutes or reset your password.");

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            return invalid;
        }
        await userManager.ResetAccessFailedCountAsync(user);

        // Password is right: now it is safe to give the specific reason.
        var gate = CheckCanSignIn(user);
        if (gate is not null) return gate;

        if (!user.EmailConfirmed)
            return Error.Forbidden(ErrorCodes.EmailNotVerified, "Please verify your email first. We can send you a new code.");

        if (!await userManager.IsInRoleAsync(user, Roles.Learner))
            return Error.Forbidden(ErrorCodes.Forbidden, "Admin accounts sign in through the admin panel.");

        return await IssueTokensAsync(user, request.Device, ct);
    }

    public async Task<Result<AuthResponse>> ExternalLoginAsync(string provider, ExternalLoginRequest request, CancellationToken ct = default)
    {
        provider = provider.ToLowerInvariant();
        if (!externalAuth.IsEnabled(provider))
            return Error.Forbidden(ErrorCodes.ExternalLoginNotConfigured, $"{provider} sign-in is not enabled.");

        var info = await externalAuth.ValidateAsync(provider, request.IdToken, ct);
        if (info is null)
            return Error.Unauthorized(ErrorCodes.ExternalLoginFailed, "Sign-in could not be verified. Please try again.");

        return await SignInExternalUserAsync(info, request.FullName, request.Device, ct);
    }

    public async Task<Result<AuthResponse>> SignInExternalUserAsync(ExternalUserInfo info, string? fullName,
        DeviceInfoDto? device, CancellationToken ct = default)
    {
        var loginKey = $"{info.Provider}:{info.Subject}";
        var normalizedEmail = info.Email is null ? null : userManager.NormalizeEmail(info.Email);

        var user = await userManager.FindByLoginAsync(info.Provider, info.Subject);
        if (user is null && info.Email is not null && info.EmailVerified)
        {
            // Link to an existing email/password account.
            user = await userManager.FindByEmailAsync(info.Email);
            if (user is not null)
            {
                var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(info.Provider, info.Subject, info.Provider));
                if (!linked.Succeeded) return linked.ToError();
                if (!user.EmailConfirmed)
                {
                    user.EmailConfirmed = true;
                    await userManager.UpdateAsync(user);
                }
            }
        }

        if (user is null)
        {
            if (info.Email is null)
                return Error.Validation("Email", "Your account did not share an email address. Please allow email access and try again.");
            if (await IsBlockedIdentityAsync(normalizedEmail, loginKey, device?.DeviceId, ct))
                return Error.Forbidden(ErrorCodes.RegistrationBlocked, "This account cannot be registered.");

            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = info.Email,
                Email = info.Email,
                EmailConfirmed = info.EmailVerified,
                FullName = FirstNonEmpty(info.Name, fullName, info.Email.Split('@')[0]),
                UserType = UserType.Learner,
                CreatedAt = Now
            };
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded) return created.ToError();
            await userManager.AddToRoleAsync(user, Roles.Learner);
            await userManager.AddLoginAsync(user, new UserLoginInfo(info.Provider, info.Subject, info.Provider));
        }

        var gate = CheckCanSignIn(user);
        if (gate is not null) return gate;
        if (!await userManager.IsInRoleAsync(user, Roles.Learner))
            return Error.Forbidden(ErrorCodes.Forbidden, "Admin accounts sign in through the admin panel.");

        return await IssueTokensAsync(user, device, ct);
    }

    // ---------------------------------------------------------------- tokens

    public async Task<Result<AuthResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var invalid = Error.Unauthorized(ErrorCodes.InvalidRefreshToken, "Your session has expired. Please sign in again.");
        var hash = tokens.HashToken(request.RefreshToken);

        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return invalid;

        if (stored.RevokedAt is not null)
        {
            if (stored.ReplacedByTokenHash is not null)
            {
                // A rotated token was used again: likely stolen. Kill the whole sign-in family.
                logger.LogWarning("Refresh token reuse detected for user {UserId}, family {FamilyId}", stored.UserId, stored.FamilyId);
                await RevokeFamilyAsync(stored.FamilyId, "reuse detected", ct);
            }
            return invalid;
        }
        if (stored.ExpiresAt <= Now) return invalid;

        var user = stored.User;
        var gate = CheckCanSignIn(user);
        if (gate is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, gate.Code, ct);
            return gate;
        }

        var newRaw = tokens.GenerateRefreshToken();
        var replacement = NewRefreshToken(user.Id, newRaw, stored.FamilyId, request.DeviceId ?? stored.DeviceId);

        stored.RevokedAt = Now;
        stored.RevokedReason = "rotated";
        stored.ReplacedByTokenHash = replacement.TokenHash;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(ct);

        var roles = await userManager.GetRolesAsync(user);
        var access = tokens.CreateAccessToken(user, roles);
        return new AuthResponse(access.Token, access.ExpiresAt, newRaw, replacement.ExpiresAt,
            ToSummary(user, roles, await HasActiveCourseAsync(user.Id, ct)));
    }

    public async Task<Result> LogoutAsync(Guid userId, LogoutRequest request, CancellationToken ct = default)
    {
        var hash = tokens.HashToken(request.RefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = Now;
            stored.RevokedReason = "logout";
            await db.SaveChangesAsync(ct);
        }
        return Result.Success();
    }

    public async Task<Result> LogoutAllAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("User");

        await RevokeAllForUserAsync(userId, "logout all devices", ct);
        // New security stamp invalidates access tokens that are still within their 15 minutes.
        await userManager.UpdateSecurityStampAsync(user);
        return Result.Success();
    }

    // ---------------------------------------------------------------- password reset

    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        // Same answer whether or not the email exists.
        if (user is null || user.IsDeleted || user.IsBlocked) return Result.Success();

        var wait = await otp.SecondsUntilResendAllowedAsync(user.Id, OtpPurpose.ResetPassword, ct);
        if (wait > 0) return Result.Success();

        var (code, _) = await otp.CreateAsync(user.Id, OtpPurpose.ResetPassword, ct);
        var (subject, html) = EmailTemplates.ResetPassword(_app.Name, user.FullName, code, _otp.ExpiryMinutes);
        await email.SendAsync(user.Email!, subject, html, ct);
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.IsDeleted)
            return Error.Validation(nameof(request.Code), "The code is incorrect.");

        var check = await otp.VerifyAsync(user.Id, OtpPurpose.ResetPassword, request.Code, ct);
        if (check.IsFailure) return check.Error!;

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!reset.Succeeded) return reset.ToError(nameof(request.NewPassword));

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true; // they proved they own the mailbox
            await userManager.UpdateAsync(user);
        }
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        await RevokeAllForUserAsync(user.Id, "password reset", ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- helpers

    private Error? CheckCanSignIn(ApplicationUser user)
    {
        if (user.IsDeleted)
            return Error.Unauthorized(ErrorCodes.InvalidCredentials, "Email or password is incorrect.");
        if (user.IsBlocked)
            return Error.Forbidden(ErrorCodes.AccountBlocked, "Account permanently blocked.");
        if (!user.IsActive)
            return Error.Forbidden(ErrorCodes.AccountInactive, "Your account is suspended. Please contact support.");
        return null;
    }

    private async Task<AuthResponse> IssueTokensAsync(ApplicationUser user, DeviceInfoDto? device, CancellationToken ct)
    {
        var roles = await userManager.GetRolesAsync(user);
        var access = tokens.CreateAccessToken(user, roles);
        var raw = tokens.GenerateRefreshToken();
        var refresh = NewRefreshToken(user.Id, raw, Guid.NewGuid(), device?.DeviceId);
        db.RefreshTokens.Add(refresh);

        if (device is not null && !string.IsNullOrWhiteSpace(device.DeviceId))
            await UpsertDeviceAsync(user.Id, device, ct);

        user.LastLoginAt = Now;
        await db.SaveChangesAsync(ct);

        return new AuthResponse(access.Token, access.ExpiresAt, raw, refresh.ExpiresAt,
            ToSummary(user, roles, await HasActiveCourseAsync(user.Id, ct)));
    }

    private RefreshToken NewRefreshToken(Guid userId, string raw, Guid familyId, string? deviceId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokens.HashToken(raw),
        FamilyId = familyId,
        DeviceId = deviceId,
        CreatedAt = Now,
        CreatedByIp = currentUser.IpAddress,
        ExpiresAt = Now.AddDays(_jwt.RefreshTokenDays)
    };

    private async Task UpsertDeviceAsync(Guid userId, DeviceInfoDto device, CancellationToken ct)
    {
        var existing = await db.UserDevices.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == device.DeviceId, ct);
        if (existing is null)
        {
            db.UserDevices.Add(new UserDevice
            {
                UserId = userId,
                DeviceId = device.DeviceId,
                Platform = device.Platform,
                AppVersion = device.AppVersion,
                PushToken = device.PushToken,
                CreatedAt = Now,
                LastSeenAt = Now
            });
        }
        else
        {
            existing.Platform = device.Platform;
            existing.AppVersion = device.AppVersion ?? existing.AppVersion;
            existing.PushToken = device.PushToken ?? existing.PushToken;
            existing.LastSeenAt = Now;
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active)
        {
            t.RevokedAt = Now;
            t.RevokedReason = reason;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active)
        {
            t.RevokedAt = Now;
            t.RevokedReason = reason;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> IsBlockedIdentityAsync(string? normalizedEmail, string? externalKey, string? deviceId, CancellationToken ct) =>
        await db.BlockedIdentities.AnyAsync(b =>
            (normalizedEmail != null && b.NormalizedEmail == normalizedEmail) ||
            (externalKey != null && b.ExternalLoginKey == externalKey) ||
            (deviceId != null && b.DeviceId == deviceId), ct);

    private Task<bool> HasActiveCourseAsync(Guid userId, CancellationToken ct) =>
        db.LearnerCourses.AnyAsync(c => c.UserId == userId &&
            (c.Status == LearnerCourseStatus.Active || c.Status == LearnerCourseStatus.Paused), ct);

    private static UserSummaryDto ToSummary(ApplicationUser user, IEnumerable<string> roles, bool hasActiveCourse) => new(
        user.Id, user.Email!, user.FullName, user.DisplayName, user.PhotoUrl, user.UiLanguage,
        roles.ToList(), hasActiveCourse);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "Learner";
}
