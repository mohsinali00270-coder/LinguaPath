using System.Security.Claims;
using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Auth;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LinguaPath.Api.Controllers;

/// <summary>
/// Browser-based Google sign-in for the mobile app (MAUI WebAuthenticator pattern).
/// The app opens /api/v1/auth/external/google?returnUrl=linguapath://auth, the API runs the OAuth flow,
/// then redirects to linguapath://auth#access_token=...&amp;refresh_token=... which the app captures.
/// </summary>
[Route("api/v1/auth/external")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ExternalAuthController(
    IAuthService auth,
    IAuthenticationSchemeProvider schemes,
    IOptions<AppOptions> appOptions) : ControllerBase
{
    public const string ExternalCookieScheme = "External";
    public const string GoogleScheme = "Google";

    [HttpGet("google")]
    [AllowAnonymous]
    public async Task<IActionResult> Google(string returnUrl, string? deviceId, DevicePlatform platform = DevicePlatform.Unknown)
    {
        if (!IsAllowedReturnUrl(returnUrl))
            return ProblemResults.ToResult(HttpContext, Error.Validation(nameof(returnUrl), "Invalid return URL."));
        if (await schemes.GetSchemeAsync(GoogleScheme) is null)
            return ProblemResults.ToResult(HttpContext, Error.Forbidden(ErrorCodes.ExternalLoginNotConfigured, "Google sign-in is not enabled."));

        var callback = Url.Action(nameof(Callback), new { returnUrl, deviceId, platform });
        return Challenge(new AuthenticationProperties { RedirectUri = callback }, GoogleScheme);
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(string returnUrl, string? deviceId, DevicePlatform platform, CancellationToken ct)
    {
        if (!IsAllowedReturnUrl(returnUrl)) return BadRequest();

        var external = await HttpContext.AuthenticateAsync(ExternalCookieScheme);
        await HttpContext.SignOutAsync(ExternalCookieScheme);
        if (!external.Succeeded || external.Principal is null)
            return Redirect($"{returnUrl}#error={ErrorCodes.ExternalLoginFailed}");

        var p = external.Principal;
        var subject = p.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(subject))
            return Redirect($"{returnUrl}#error={ErrorCodes.ExternalLoginFailed}");

        // Google only returns addresses it has verified for the account.
        var info = new ExternalUserInfo("google", subject, p.FindFirstValue(ClaimTypes.Email), true, p.FindFirstValue(ClaimTypes.Name));
        var device = string.IsNullOrWhiteSpace(deviceId) ? null : new DeviceInfoDto(deviceId, platform, null, null);

        var result = await auth.SignInExternalUserAsync(info, null, device, ct);
        if (result.IsFailure)
            return Redirect($"{returnUrl}#error={Uri.EscapeDataString(result.Error!.Code)}&error_description={Uri.EscapeDataString(result.Error.Message)}");

        var r = result.Value;
        var fragment = string.Join('&',
            $"access_token={Uri.EscapeDataString(r.AccessToken)}",
            $"refresh_token={Uri.EscapeDataString(r.RefreshToken)}",
            $"access_expires={r.AccessTokenExpiresAt.ToUnixTimeSeconds()}",
            $"refresh_expires={r.RefreshTokenExpiresAt.ToUnixTimeSeconds()}");
        return Redirect($"{returnUrl}#{fragment}");
    }

    /// <summary>Only the app's own URI scheme is accepted, to prevent open redirects.</summary>
    private bool IsAllowedReturnUrl(string? returnUrl) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, appOptions.Value.MobileCallbackScheme, StringComparison.OrdinalIgnoreCase);
}
