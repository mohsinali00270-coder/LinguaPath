using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Catalog;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Catalog;

public interface ILanguageCatalogService
{
    /// <summary>
    /// Published and "Coming soon" languages for the onboarding list (FR-L10).
    /// With <paramref name="includeAll"/> every non-disabled language is returned, for picking a native language.
    /// </summary>
    Task<IReadOnlyList<LanguageDto>> GetPublicAsync(bool includeAll = false, CancellationToken ct = default);
}

internal sealed class LanguageCatalogService(IApplicationDbContext db) : ILanguageCatalogService
{
    public async Task<IReadOnlyList<LanguageDto>> GetPublicAsync(bool includeAll = false, CancellationToken ct = default) =>
        await db.Languages.AsNoTracking()
            .Where(l => !l.IsDeleted && (includeAll
                ? l.Status != LanguageStatus.Disabled
                : l.Status == LanguageStatus.Published || l.Status == LanguageStatus.ComingSoon))
            .OrderBy(l => l.Status == LanguageStatus.Published ? 0 : 1)
            .ThenBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new LanguageDto(l.Id, l.Name, l.NativeName, l.IsoCode, l.FlagUrl, l.Direction, l.Status, l.MinLevel, l.MaxLevel))
            .ToListAsync(ct);
}

public interface IDeviceService
{
    Task<Result> RegisterAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default);
}

internal sealed class DeviceService(IApplicationDbContext db, TimeProvider clock) : IDeviceService
{
    public async Task<Result> RegisterAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await db.UserDevices.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == request.DeviceId, ct);
        if (device is null)
        {
            db.UserDevices.Add(new UserDevice
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                Platform = request.Platform,
                AppVersion = request.AppVersion,
                PushToken = request.PushToken,
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            device.Platform = request.Platform;
            device.AppVersion = request.AppVersion;
            device.PushToken = request.PushToken;
            device.LastSeenAt = now;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public interface IAppVersionService
{
    Task<VersionCheckResponse> CheckAsync(DevicePlatform platform, string? currentVersion, CancellationToken ct = default);
}

/// <summary>Minimum supported app version and force-update message (FR-A112).</summary>
internal sealed class AppVersionService(ISettingsService settings) : IAppVersionService
{
    public async Task<VersionCheckResponse> CheckAsync(DevicePlatform platform, string? currentVersion, CancellationToken ct = default)
    {
        var ios = platform == DevicePlatform.iOS || platform == DevicePlatform.MacCatalyst;
        var min = await settings.GetAsync(ios ? SettingKeys.MinAppVersionIos : SettingKeys.MinAppVersionAndroid, ct) ?? "1.0.0";
        var latest = await settings.GetAsync(ios ? SettingKeys.LatestAppVersionIos : SettingKeys.LatestAppVersionAndroid, ct) ?? min;
        var message = await settings.GetAsync(SettingKeys.ForceUpdateMessage, ct);
        var storeUrl = await settings.GetAsync(ios ? SettingKeys.StoreUrlIos : SettingKeys.StoreUrlAndroid, ct);

        var current = Parse(currentVersion);
        var updateRequired = current is not null && current < (Parse(min) ?? new Version(1, 0));
        var updateAvailable = current is not null && current < (Parse(latest) ?? new Version(1, 0));

        return new VersionCheckResponse(updateRequired, updateAvailable, min, latest,
            updateRequired ? message : null, string.IsNullOrWhiteSpace(storeUrl) ? null : storeUrl);
    }

    internal static Version? Parse(string? value) =>
        Version.TryParse(value?.Split('-', '+')[0], out var v) ? v : null;
}
