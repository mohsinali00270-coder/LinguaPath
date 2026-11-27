using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LinguaPath.Infrastructure.Security;

public static class AppClaims
{
    /// <summary>Copy of the user's security stamp; changing the stamp invalidates live access tokens.</summary>
    public const string SecurityStamp = "sstamp";
    public const string FullName = "name";
    /// <summary>Short claim names are kept as-is (MapInboundClaims = false), so roles travel as "role".</summary>
    public const string Role = "role";
}

internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public AccessToken CreateAccessToken(ApplicationUser user, IEnumerable<string> roles)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(AppClaims.FullName, user.FullName),
            new(AppClaims.SecurityStamp, user.SecurityStamp ?? string.Empty)
        };
        claims.AddRange(roles.Select(r => new Claim(AppClaims.Role, r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    public string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static SymmetricSecurityKey SigningKey(JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be set and at least 32 characters (use user-secrets or an environment variable).");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }
}
