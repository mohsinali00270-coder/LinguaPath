using LinguaPath.Shared;

namespace LinguaPath.Mobile.Core.Services;

// Interfaces implemented by the MAUI head project. Keeping them here lets ViewModels be unit-tested.

public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

public interface INavigationService
{
    /// <summary>Shell route, e.g. "//main/home" or "verify-email".</summary>
    Task GoToAsync(string route, IDictionary<string, object>? parameters = null);
    Task GoBackAsync();
}

public interface IDialogService
{
    Task AlertAsync(string title, string message, string ok = "OK");
    Task<bool> ConfirmAsync(string title, string message, string accept = "Yes", string cancel = "Cancel");
    Task<string?> PromptAsync(string title, string message, string accept = "OK", string? placeholder = null, bool isPassword = false);
    Task ToastAsync(string message);
    /// <summary>Action sheet: returns the chosen option, or null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, string? message, string cancel, params string[] options);
}

public interface IDeviceContext
{
    string DeviceId { get; }
    DevicePlatform Platform { get; }
    string AppVersion { get; }
    /// <summary>IANA time zone of the device, e.g. "Asia/Karachi".</summary>
    string TimeZoneId { get; }
    bool IsAppleSignInAvailable { get; }
}

public sealed record ExternalSignInResult(
    bool Cancelled = false,
    string? Error = null,
    // Native flow (Apple on iOS): an ID token for POST /auth/apple.
    string? IdToken = null,
    string? FullName = null,
    // Browser flow (Google): tokens issued by our API directly.
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    DateTimeOffset? RefreshTokenExpiresAt = null);

public interface IExternalSignIn
{
    Task<ExternalSignInResult> GoogleAsync();
    Task<ExternalSignInResult> AppleAsync();
}

public sealed record PickedFile(string FileName, string ContentType, Func<Task<Stream>> OpenReadAsync);

public interface IMediaService
{
    Task<PickedFile?> PickPhotoAsync();
    Task ShareTextFileAsync(string fileName, string content, string title);
    Task OpenUrlAsync(string url);
    /// <summary>Saves a downloaded file (e.g. a certificate PDF) to the app's cache and opens it in the system viewer.</summary>
    Task OpenFileAsync(string fileName, byte[] content, string contentType);
    /// <summary>System share sheet for a link (FR-L82: share the verification page).</summary>
    Task ShareLinkAsync(string title, string url);
}

/// <summary>Read-aloud with the device's voices (FR-L34). Locale like "es-ES"; falls back to the closest voice.</summary>
public interface ISpeechService
{
    Task SpeakAsync(string text, string? locale, CancellationToken ct = default);
    /// <summary>Read aloud at a speed (FR-L90): 0.5 = slow, 1 = normal, 1.5 = fast.</summary>
    Task SpeakAsync(string text, string? locale, float rate, CancellationToken ct = default);
    void Stop();
}

/// <summary>Small app-private file store for offline lectures (FR-L38).</summary>
public interface IOfflineStore
{
    Task SaveAsync(string key, string content);
    Task<string?> LoadAsync(string key);
    bool Exists(string key);
    void Remove(string key);
}

/// <summary>A finished recording, read by the API client when it is uploaded.</summary>
public sealed record RecordedAudio(string FileName, string ContentType, Func<Task<Stream>> OpenReadAsync, TimeSpan Duration);

/// <summary>Microphone recording for speaking questions and playback of listening audio (FR-L51).</summary>
public interface IAudioService
{
    bool IsRecording { get; }
    /// <summary>Asks for microphone permission if needed. False when it was refused.</summary>
    Task<bool> StartRecordingAsync();
    Task<RecordedAudio?> StopRecordingAsync();
    Task PlayUrlAsync(string url, CancellationToken ct = default);
    Task PlayRecordingAsync(RecordedAudio audio, CancellationToken ct = default);
    void StopPlayback();
}

/// <summary>Hands the placement test result back to onboarding (FR-L11).</summary>
public sealed class PlacementResults
{
    private (int CourseId, CefrLevel Level)? _last;

    public void Set(int courseId, CefrLevel level) => _last = (courseId, level);

    /// <summary>Returns and clears the result for this course.</summary>
    public CefrLevel? Take(int courseId)
    {
        if (_last is not { } r || r.CourseId != courseId) return null;
        _last = null;
        return r.Level;
    }
}

public interface IAppTheme
{
    bool IsDark { get; }
}

/// <summary>Base address of the API. The MAUI project chooses it per platform/build.</summary>
public sealed class ApiSettings
{
    public required string BaseUrl { get; init; }
}
