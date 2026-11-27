using LinguaPath.Application.Common;
using Microsoft.IdentityModel.Tokens;

namespace LinguaPath.Infrastructure.Security;

/// <summary>Builds the token validation parameters the API uses, matching what JwtTokenService issues.</summary>
public static class JwtKeys
{
    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = JwtTokenService.SigningKey(options),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
        RoleClaimType = AppClaims.Role
    };
}
