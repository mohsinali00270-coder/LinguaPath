using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LinguaPath.Infrastructure.Services;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var id = Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }

    public string? Email => Principal?.FindFirstValue(JwtRegisteredClaimNames.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email)
                            ?? Principal?.Identity?.Name;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}

internal sealed class AuditLogger(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider clock) : IAuditLogger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task LogAsync(string action, string entityType, string? entityId, object? oldValues = null, object? newValues = null,
        CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = currentUser.UserId,
            UserEmail = currentUser.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues, Json),
            NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues, Json),
            IpAddress = currentUser.IpAddress,
            CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }
}

internal sealed class SettingsService(IApplicationDbContext db, IMemoryCache cache, IAuditLogger audit, ICurrentUser currentUser,
    TimeProvider clock) : ISettingsService
{
    private const string CacheKey = "system-settings";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await LoadAsync(ct)).TryGetValue(key, out var v) ? v : null;

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken ct = default) =>
        await db.SystemSettings.AsNoTracking().OrderBy(s => s.Group).ThenBy(s => s.Key).ToListAsync(ct);

    public async Task<Result> SetAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return Error.NotFound($"Setting '{key}'");

        value = (value ?? string.Empty).Trim();
        var valid = setting.DataType switch
        {
            SettingDataType.Int => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0,
            SettingDataType.Decimal => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0,
            SettingDataType.Bool => bool.TryParse(value, out _),
            SettingDataType.Version => Version.TryParse(value, out _),
            _ => true
        };
        if (!valid) return Error.Validation(key, $"'{value}' is not a valid {setting.DataType.ToString().ToLowerInvariant()} value.");
        if (setting.Value == value) return Result.Success();

        var old = setting.Value;
        setting.Value = setting.DataType == SettingDataType.Bool ? value.ToLowerInvariant() : value;
        setting.UpdatedAt = clock.GetUtcNow();
        setting.UpdatedBy = currentUser.UserId;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);

        await audit.LogAsync("Update", "SystemSetting", key, new { Value = old }, new { setting.Value }, ct);
        return Result.Success();
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        }))!;
}

public class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"Local" for development. Azure Blob / S3 implementations plug in behind IFileStorage later (NFR-17).</summary>
    public string Provider { get; set; } = "Local";
    /// <summary>Folder under wwwroot for local files.</summary>
    public string LocalFolder { get; set; } = "uploads";

    /// <summary>
    /// Optional folder that holds LocalFolder instead of this host's wwwroot, relative to the content root or absolute.
    /// Point the admin panel at the API's wwwroot (or both hosts at one shared folder) so files the admin uploads are served
    /// by the API at App:PublicBaseUrl, and files the API saves can be read by the admin.
    /// </summary>
    public string? LocalRoot { get; set; }

    public string ResolveRoot(string contentRoot, string? webRoot) =>
        Path.Combine(string.IsNullOrWhiteSpace(LocalRoot)
            ? webRoot ?? Path.Combine(contentRoot, "wwwroot")
            : Path.GetFullPath(Path.IsPathRooted(LocalRoot) ? LocalRoot : Path.Combine(contentRoot, LocalRoot)), LocalFolder);
}

/// <summary>Saves files under wwwroot/uploads and returns absolute URLs based on App:PublicBaseUrl.</summary>
internal sealed class LocalFileStorage(IWebHostEnvironment env, IOptions<StorageOptions> storage, IOptions<AppOptions> app,
    IHttpClientFactory http) : IFileStorage
{
    private string Root => storage.Value.ResolveRoot(env.ContentRootPath, env.WebRootPath);

    public async Task<string> SaveAsync(Stream content, string folder, string extension, string contentType, CancellationToken ct = default)
    {
        var safeFolder = string.Concat(folder.Where(char.IsLetterOrDigit));
        var dir = Path.Combine(Root, safeFolder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{extension}";
        await using (var file = File.Create(Path.Combine(dir, name)))
            await content.CopyToAsync(file, ct);

        return $"{app.Value.PublicBaseUrl.TrimEnd('/')}/{storage.Value.LocalFolder}/{safeFolder}/{name}";
    }

    public Task DeleteAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.CompletedTask;
        var marker = $"/{storage.Value.LocalFolder}/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return Task.CompletedTask;

        var relative = url[(idx + marker.Length)..].Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        // Never delete outside the uploads folder.
        if (full.StartsWith(Path.GetFullPath(Root), StringComparison.Ordinal) && File.Exists(full))
            File.Delete(full);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReadAsync(string url, int maxBytes, CancellationToken ct = default)
    {
        var marker = $"/{storage.Value.LocalFolder}/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var relative = url[(idx + marker.Length)..].Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (full.StartsWith(Path.GetFullPath(Root), StringComparison.Ordinal) && File.Exists(full))
        {
            var info = new FileInfo(full);
            return info.Length > maxBytes ? null : await File.ReadAllBytesAsync(full, ct);
        }

        // The API and the admin panel have their own wwwroot: a learner's recording saved by the API is fetched over HTTP.
        // Only our own upload paths are fetched (the marker above), never arbitrary URLs.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            using var client = http.CreateClient("storage");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > maxBytes ? null : bytes;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
