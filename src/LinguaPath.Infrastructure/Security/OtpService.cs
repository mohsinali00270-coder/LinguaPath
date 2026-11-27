using System.Security.Cryptography;
using System.Text;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.Security;

/// <summary>6-digit email codes, stored as salted SHA-256 hashes, max 5 tries, 10 minutes (FR-L01, FR-L04).</summary>
internal sealed class OtpService(IApplicationDbContext db, IOptions<OtpOptions> options, TimeProvider clock) : IOtpService
{
    private readonly OtpOptions _options = options.Value;

    public async Task<(string Code, DateTimeOffset ExpiresAt)> CreateAsync(Guid userId, OtpPurpose purpose, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        // Only the newest code is valid.
        var open = await db.EmailOtps.Where(o => o.UserId == userId && o.Purpose == purpose && o.ConsumedAt == null).ToListAsync(ct);
        foreach (var o in open) o.ConsumedAt = now;

        var code = GenerateCode(_options.Length);
        var expires = now.AddMinutes(_options.ExpiryMinutes);
        db.EmailOtps.Add(new EmailOtp
        {
            UserId = userId,
            Purpose = purpose,
            CodeHash = Hash(userId, code),
            CreatedAt = now,
            ExpiresAt = expires
        });
        await db.SaveChangesAsync(ct);
        return (code, expires);
    }

    public async Task<Result> VerifyAsync(Guid userId, OtpPurpose purpose, string code, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var otp = await db.EmailOtps
            .Where(o => o.UserId == userId && o.Purpose == purpose && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (otp is null)
            return Error.Validation("Code", "The code is incorrect. Request a new one.");
        if (otp.ExpiresAt <= now)
            return new Error(ErrorCodes.OtpExpired, "The code has expired. Request a new one.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["Code"] = ["The code has expired. Request a new one."] });
        if (otp.Attempts >= _options.MaxAttempts)
            return Error.TooManyRequests(ErrorCodes.OtpTooManyAttempts, "Too many wrong attempts. Request a new code.");

        if (!FixedTimeEquals(otp.CodeHash, Hash(userId, code.Trim())))
        {
            otp.Attempts++;
            if (otp.Attempts >= _options.MaxAttempts) otp.ConsumedAt = now;
            await db.SaveChangesAsync(ct);
            var left = _options.MaxAttempts - otp.Attempts;
            return new Error(ErrorCodes.InvalidOtp,
                left > 0 ? $"The code is incorrect. {left} attempt(s) left." : "Too many wrong attempts. Request a new code.",
                ErrorType.Validation,
                new Dictionary<string, string[]> { ["Code"] = ["The code is incorrect."] });
        }

        otp.ConsumedAt = now;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<int> SecondsUntilResendAllowedAsync(Guid userId, OtpPurpose purpose, CancellationToken ct = default)
    {
        var last = await db.EmailOtps
            .Where(o => o.UserId == userId && o.Purpose == purpose)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => (DateTimeOffset?)o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (last is null) return 0;

        var wait = last.Value.AddSeconds(_options.ResendCooldownSeconds) - clock.GetUtcNow();
        return wait > TimeSpan.Zero ? (int)Math.Ceiling(wait.TotalSeconds) : 0;
    }

    internal static string GenerateCode(int length)
    {
        var max = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(0, max).ToString($"D{length}");
    }

    internal static string Hash(Guid userId, string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{code}")));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
