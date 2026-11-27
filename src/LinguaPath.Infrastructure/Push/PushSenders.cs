using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Moderation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.Push;

public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>Firebase project id (Firebase console → Project settings).</summary>
    public string? FcmProjectId { get; set; }

    /// <summary>Path to the service-account JSON file, or the JSON itself (keep it in user-secrets or Key Vault, never in the database).</summary>
    public string? FcmServiceAccount { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FcmProjectId) && !string.IsNullOrWhiteSpace(FcmServiceAccount);
}

/// <summary>Picks FCM when configured, otherwise writes pushes to the log (development and tests).</summary>
internal sealed class PushSenderSelector(IOptions<PushOptions> options, FcmPushSender fcm, LoggingPushSender log) : IPushSender
{
    public Task SendAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default) =>
        options.Value.IsConfigured ? fcm.SendAsync(userIds, message, ct) : log.SendAsync(userIds, message, ct);
}

internal sealed class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task SendAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        logger.LogInformation("Push (not configured) to {Count} learner(s): {Title} — {Body}", userIds.Count, message.Title, message.Body);
        return Task.CompletedTask;
    }
}

/// <summary>Firebase Cloud Messaging HTTP v1 (Android and iOS through APNs in Firebase). Tokens that FCM rejects are cleared.</summary>
internal sealed class FcmPushSender(
    IOptions<PushOptions> options,
    IHttpClientFactory http,
    IServiceProvider services,
    ILogger<FcmPushSender> logger) : IPushSender
{
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static (string Token, DateTimeOffset Expires)? _accessToken;

    public async Task SendAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var devices = await db.UserDevices.Where(d => userIds.Contains(d.UserId) && d.PushToken != null && d.PushToken != "")
            .ToListAsync(ct);
        if (devices.Count == 0) return;

        string accessToken;
        try
        {
            accessToken = await AccessTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "FCM sign-in failed; push not sent");
            return;
        }

        var client = http.CreateClient("push");
        var url = $"https://fcm.googleapis.com/v1/projects/{options.Value.FcmProjectId}/messages:send";
        var stale = false;
        foreach (var device in devices)
        {
            var body = new
            {
                message = new
                {
                    token = device.PushToken,
                    notification = new { title = message.Title, body = message.Body },
                    data = message.Data ?? new Dictionary<string, string>(),
                    android = new { priority = "high" }
                }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new("Bearer", accessToken);
            try
            {
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    if (text.Contains("UNREGISTERED", StringComparison.Ordinal) || text.Contains("INVALID_ARGUMENT", StringComparison.Ordinal))
                    {
                        device.PushToken = null;
                        stale = true;
                    }
                }
                else if (!response.IsSuccessStatusCode)
                    logger.LogWarning("FCM answered {Status} for device {DeviceId}", (int)response.StatusCode, device.Id);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "FCM send failed");
            }
        }
        if (stale) await db.SaveChangesAsync(ct);
    }

    /// <summary>OAuth 2.0 service-account flow: a signed JWT exchanged for an access token, cached for 50 minutes.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached.Token;
        await TokenGate.WaitAsync(ct);
        try
        {
            if (_accessToken is { } again && again.Expires > DateTimeOffset.UtcNow) return again.Token;
            var raw = options.Value.FcmServiceAccount!.Trim();
            var json = raw.StartsWith('{') ? raw : await File.ReadAllTextAsync(raw, ct);
            using var doc = JsonDocument.Parse(json);
            var email = doc.RootElement.GetProperty("client_email").GetString()!;
            var key = doc.RootElement.GetProperty("private_key").GetString()!;
            var tokenUri = doc.RootElement.TryGetProperty("token_uri", out var t) ? t.GetString()! : "https://oauth2.googleapis.com/token";

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = email, scope = "https://www.googleapis.com/auth/firebase.messaging", aud = tokenUri, iat = now, exp = now + 3600
            }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key);
            var signature = B64(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            var client = http.CreateClient("push");
            using var response = await client.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{header}.{claims}.{signature}"
            }), ct);
            response.EnsureSuccessStatusCode();
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = answer.RootElement.GetProperty("access_token").GetString()!;
            _accessToken = (token, DateTimeOffset.UtcNow.AddMinutes(50));
            return token;
        }
        finally
        {
            TokenGate.Release();
        }
    }
}
