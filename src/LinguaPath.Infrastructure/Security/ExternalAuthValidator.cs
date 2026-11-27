using Google.Apis.Auth;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LinguaPath.Infrastructure.Security;

/// <summary>Validates Google and Apple ID tokens sent from the app (FR-L02).</summary>
internal sealed class ExternalAuthValidator : IExternalAuthValidator
{
    public const string Google = "google";
    public const string Apple = "apple";

    // Apple's signing keys are cached and refreshed automatically by the configuration manager.
    private static readonly Microsoft.IdentityModel.Protocols.ConfigurationManager<OpenIdConnectConfiguration> AppleConfig = new(
        "https://appleid.apple.com/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });

    private readonly ExternalAuthOptions _options;
    private readonly ILogger<ExternalAuthValidator> _logger;

    public ExternalAuthValidator(IOptions<ExternalAuthOptions> options, ILogger<ExternalAuthValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled(string provider) => provider switch
    {
        Google => _options.GoogleEnabled,
        Apple => _options.AppleEnabled,
        _ => false
    };

    public async Task<ExternalUserInfo?> ValidateAsync(string provider, string idToken, CancellationToken ct = default)
    {
        try
        {
            return provider switch
            {
                Google => await ValidateGoogleAsync(idToken),
                Apple => await ValidateAppleAsync(idToken, ct),
                _ => null
            };
        }
        catch (Exception ex) when (ex is InvalidJwtException or SecurityTokenException or ArgumentException)
        {
            _logger.LogWarning(ex, "Rejected {Provider} ID token", provider);
            return null;
        }
    }

    private async Task<ExternalUserInfo?> ValidateGoogleAsync(string idToken)
    {
        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = _options.GoogleClientIds
        });
        return new ExternalUserInfo(Google, payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
    }

    private async Task<ExternalUserInfo?> ValidateAppleAsync(string idToken, CancellationToken ct)
    {
        var config = await AppleConfig.GetConfigurationAsync(ct);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = "https://appleid.apple.com",
            ValidAudiences = _options.AppleAudiences,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        });
        if (!result.IsValid)
        {
            _logger.LogWarning(result.Exception, "Apple ID token validation failed");
            return null;
        }

        var claims = result.Claims;
        var email = claims.TryGetValue("email", out var e) ? e?.ToString() : null;
        var verified = claims.TryGetValue("email_verified", out var v) &&
                       (v is bool b ? b : string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
        var subject = claims["sub"].ToString()!;
        // Apple sends the name only on the very first sign-in, and only to the app, so the app passes it as FullName.
        return new ExternalUserInfo(Apple, subject, email, verified, null);
    }
}
