using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Auth;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace LinguaPath.Api.Controllers;

/// <summary>Sign-up, sign-in and session endpoints (FR-L01 .. FR-L04).</summary>
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(IAuthService auth, IMemoryCache cache) : ApiControllerBase
{
    /// <summary>Create a learner account and email a 6-digit verification code.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<RegisterResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct) =>
        FromResult(await auth.RegisterAsync(request, ct), "We sent a 6-digit code to your email.");

    /// <summary>Confirm the email with the code; signs the learner in.</summary>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<AuthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken ct) =>
        FromResult(await auth.VerifyEmailAsync(request, ct), "Email verified.");

    /// <summary>Send a new code for email verification or password reset (60-second cooldown).</summary>
    [HttpPost("resend-otp")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<ResendOtpResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendOtp(ResendOtpRequest request, CancellationToken ct) =>
        FromResult(await auth.ResendOtpAsync(request, ct), "If the account exists, a new code is on its way.");

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<AuthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct) =>
        FromResult(await auth.LoginAsync(request, ct));

    /// <summary>Sign in with a Google ID token obtained on the device.</summary>
    [HttpPost("google")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<AuthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Google(ExternalLoginRequest request, CancellationToken ct) =>
        FromResult(await auth.ExternalLoginAsync("google", request, ct));

    /// <summary>Sign in with Apple. Pass FullName on the first sign-in, Apple only shares it once.</summary>
    [HttpPost("apple")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<AuthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Apple(ExternalLoginRequest request, CancellationToken ct) =>
        FromResult(await auth.ExternalLoginAsync("apple", request, ct));

    /// <summary>Exchange a refresh token for a new access + refresh token pair (rotation).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [DisableRateLimiting]
    [ProducesResponseType<MyApiResponse<AuthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken ct) =>
        FromResult(await auth.RefreshAsync(request, ct));

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct) =>
        FromResult(await auth.LogoutAsync(CurrentUserId, request, ct), "Signed out.");

    /// <summary>Sign out from all devices (FR-L04).</summary>
    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var result = await auth.LogoutAllAsync(userId, ct);
        SecurityStampCheck.Invalidate(cache, userId);
        return FromResult(result, "Signed out from all devices.");
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct) =>
        FromResult(await auth.ForgotPasswordAsync(request, ct), "If the account exists, we sent a reset code to the email.");

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct) =>
        FromResult(await auth.ResetPasswordAsync(request, ct), "Password changed. Please sign in.");
}
