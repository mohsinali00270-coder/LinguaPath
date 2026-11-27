namespace LinguaPath.Shared.Contracts.Catalog;

// FR-L10 Language list shown during onboarding.
public record LanguageDto(
    int Id,
    string Name,
    string NativeName,
    string IsoCode,
    string? FlagUrl,
    TextDirection Direction,
    LanguageStatus Status,
    CefrLevel MinLevel,
    CefrLevel MaxLevel);

// FR-A112 Minimum supported app version / force update.
public record VersionCheckResponse(
    bool UpdateRequired,
    bool UpdateAvailable,
    string MinimumVersion,
    string LatestVersion,
    string? Message,
    string? StoreUrl);

public record RegisterDeviceRequest(string DeviceId, DevicePlatform Platform, string? AppVersion, string? PushToken);
