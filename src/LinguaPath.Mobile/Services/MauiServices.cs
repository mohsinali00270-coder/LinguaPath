using LinguaPath.Mobile.Core.Services;

namespace LinguaPath.Mobile.Services;

/// <summary>Tokens live in the platform keychain / keystore (FR-L03).</summary>
public sealed class MauiSecureStore : ISecureStore
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception)
        {
            // Keystore can be reset after an OS restore; treat as signed out.
            SecureStorage.Default.Remove(key);
            return null;
        }
    }

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public void Remove(string key) => SecureStorage.Default.Remove(key);
}

public sealed class ShellNavigationService : INavigationService
{
    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() => parameters is null
            ? Shell.Current.GoToAsync(route)
            : Shell.Current.GoToAsync(route, parameters));

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));
}

public sealed class MauiDialogService : IDialogService
{
    private static Page Page => Shell.Current?.CurrentPage ?? Application.Current!.Windows[0].Page!;

    public Task AlertAsync(string title, string message, string ok = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() => Page.DisplayAlertAsync(title, message, ok));

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Yes", string cancel = "Cancel") =>
        MainThread.InvokeOnMainThreadAsync(() => Page.DisplayAlertAsync(title, message, accept, cancel));

    public Task<string?> PromptAsync(string title, string message, string accept = "OK", string? placeholder = null, bool isPassword = false) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // DisplayPromptAsync has no password mode; the value is only used once to confirm deletion.
            string? result = await Page.DisplayPromptAsync(title, message, accept, "Cancel", placeholder,
                keyboard: isPassword ? Keyboard.Default : Keyboard.Text);
            return result;
        });

    public Task<string?> ChooseAsync(string title, string? message, string cancel, params string[] options) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // The action sheet has no separate message line; show it in the title when given.
            var heading = string.IsNullOrWhiteSpace(message) ? title : $"{title}\n{message}";
            var choice = await Page.DisplayActionSheetAsync(heading, cancel, null, options);
            return choice is null || choice == cancel ? null : choice;
        });

    public Task ToastAsync(string message) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // Lightweight toast without extra packages: a short-lived banner on the current page.
            if (Page is not ContentPage { Content: Layout layout }) return;
            var toast = new Border
            {
                Padding = new Thickness(18, 12),
                Margin = new Thickness(24, 0, 24, 32),
                BackgroundColor = Color.FromArgb("#0B1530"), // brand navy
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                Shadow = new Shadow { Brush = Colors.Black, Offset = new Point(0, 6), Radius = 18, Opacity = 0.25f },
                VerticalOptions = LayoutOptions.End,
                HorizontalOptions = LayoutOptions.Center,
                Content = new Label { Text = message, TextColor = Colors.White, FontSize = 14 },
                Opacity = 0
            };
            if (layout is Grid grid)
            {
                Grid.SetRowSpan(toast, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(toast, Math.Max(1, grid.ColumnDefinitions.Count));
            }
            layout.Children.Add(toast);
            await toast.FadeToAsync(1, 150);
            await Task.Delay(2000);
            await toast.FadeToAsync(0, 250);
            layout.Children.Remove(toast);
        });
}

public sealed class MauiDeviceContext : IDeviceContext
{
    private const string DeviceIdKey = "lp.device.id";
    private string? _deviceId;

    /// <summary>Random id created on first launch and kept in preferences (no hardware identifiers).</summary>
    public string DeviceId => _deviceId ??= GetOrCreateDeviceId();

    public Shared.DevicePlatform Platform =>
        DeviceInfo.Current.Platform == Microsoft.Maui.Devices.DevicePlatform.Android ? Shared.DevicePlatform.Android
        : DeviceInfo.Current.Platform == Microsoft.Maui.Devices.DevicePlatform.iOS ? Shared.DevicePlatform.iOS
        : DeviceInfo.Current.Platform == Microsoft.Maui.Devices.DevicePlatform.WinUI ? Shared.DevicePlatform.Windows
        : DeviceInfo.Current.Platform == Microsoft.Maui.Devices.DevicePlatform.MacCatalyst ? Shared.DevicePlatform.MacCatalyst
        : Shared.DevicePlatform.Unknown;

    public string AppVersion => AppInfo.Current.VersionString;

    public string TimeZoneId
    {
        get
        {
            var local = TimeZoneInfo.Local;
            // Windows gives Windows ids; convert to IANA so the server stores one format.
            if (!local.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana)) return iana;
            return local.Id;
        }
    }

    public bool IsAppleSignInAvailable =>
        DeviceInfo.Current.Platform == Microsoft.Maui.Devices.DevicePlatform.iOS && DeviceInfo.Current.Version.Major >= 13;

    private static string GetOrCreateDeviceId()
    {
        var id = Preferences.Default.Get(DeviceIdKey, string.Empty);
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(DeviceIdKey, id);
        }
        return id;
    }
}

/// <summary>
/// Google: browser flow through our API (/api/v1/auth/external/google), returning our own tokens.
/// Apple (iOS): native sheet returning an ID token, verified by POST /api/v1/auth/apple.
/// </summary>
public sealed class MauiExternalSignIn(ApiSettings api, IDeviceContext device) : IExternalSignIn
{
    public async Task<ExternalSignInResult> GoogleAsync()
    {
        var callback = new Uri($"{AppConfig.CallbackScheme}://auth");
        var start = new Uri($"{api.BaseUrl.TrimEnd('/')}/api/v1/auth/external/google" +
                            $"?returnUrl={Uri.EscapeDataString(callback.ToString())}" +
                            $"&deviceId={Uri.EscapeDataString(device.DeviceId)}&platform={device.Platform}");
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(start, callback);
            var p = result.Properties;
            if (p.TryGetValue("error", out var error))
                return new ExternalSignInResult(Error: p.TryGetValue("error_description", out var d) ? d : "Google sign-in failed (" + error + ").");

            return new ExternalSignInResult(
                AccessToken: result.AccessToken,
                RefreshToken: result.RefreshToken,
                AccessTokenExpiresAt: FromUnix(p, "access_expires"),
                RefreshTokenExpiresAt: FromUnix(p, "refresh_expires"));
        }
        catch (TaskCanceledException)
        {
            return new ExternalSignInResult(Cancelled: true);
        }
    }

    public async Task<ExternalSignInResult> AppleAsync()
    {
        if (!device.IsAppleSignInAvailable)
            return new ExternalSignInResult(Error: "Sign in with Apple is available on iPhone and iPad.");
        try
        {
            var result = await AppleSignInAuthenticator.AuthenticateAsync(new AppleSignInAuthenticator.Options
            {
                IncludeEmailScope = true,
                IncludeFullNameScope = true
            });
            result.Properties.TryGetValue("name", out var name);
            return new ExternalSignInResult(IdToken: result.IdToken, FullName: string.IsNullOrWhiteSpace(name) ? null : name);
        }
        catch (TaskCanceledException)
        {
            return new ExternalSignInResult(Cancelled: true);
        }
    }

    private static DateTimeOffset? FromUnix(IDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && long.TryParse(v, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}

public sealed class MauiMediaService : IMediaService
{
    public async Task<PickedFile?> PickPhotoAsync()
    {
        var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "Choose a profile photo" });
        if (photo is null) return null;

        var contentType = string.IsNullOrEmpty(photo.ContentType) ? "image/jpeg" : photo.ContentType;
        return new PickedFile(photo.FileName, contentType, photo.OpenReadAsync);
    }

    public async Task ShareTextFileAsync(string fileName, string content, string title)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(path, content);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = title, File = new ShareFile(path, "application/json") });
    }

    public Task OpenUrlAsync(string url) => Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);

    public async Task OpenFileAsync(string fileName, byte[] content, string contentType)
    {
        var safe = string.Concat(fileName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        var path = Path.Combine(FileSystem.CacheDirectory, string.IsNullOrEmpty(safe) ? "file.pdf" : safe);
        await File.WriteAllBytesAsync(path, content);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(path, contentType)));
    }

    public Task ShareLinkAsync(string title, string url) =>
        Share.Default.RequestAsync(new ShareTextRequest { Title = title, Uri = url });
}

/// <summary>Read-aloud with the device's built-in voices (FR-L34).</summary>
public sealed class MauiSpeechService : ISpeechService
{
    private CancellationTokenSource? _cts;
    private IReadOnlyList<Locale>? _locales;

    public Task SpeakAsync(string text, string? locale, CancellationToken ct = default) => SpeakAsync(text, locale, 1f, ct);

    public async Task SpeakAsync(string text, string? locale, float rate, CancellationToken ct = default)
    {
        Stop();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var options = new SpeechOptions { Locale = await FindLocaleAsync(locale), Rate = Math.Clamp(rate, 0.1f, 2f) };
        await TextToSpeech.Default.SpeakAsync(text, options, _cts.Token);
    }

    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>"es-ES" → an exact voice if there is one, else any voice for "es", else the default voice.</summary>
    private async Task<Locale?> FindLocaleAsync(string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        _locales ??= (await TextToSpeech.Default.GetLocalesAsync()).ToList();
        var parts = wanted.Replace('_', '-').Split('-');
        var language = parts[0];
        var country = parts.Length > 1 ? parts[1] : null;
        return _locales.FirstOrDefault(l => l.Language.Equals(language, StringComparison.OrdinalIgnoreCase)
                                            && country is not null && string.Equals(l.Country, country, StringComparison.OrdinalIgnoreCase))
               ?? _locales.FirstOrDefault(l => l.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Offline lectures live in the app's private data folder (FR-L38).</summary>
public sealed class MauiOfflineStore : IOfflineStore
{
    private static string Folder
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "offline");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string PathFor(string key) => Path.Combine(Folder, string.Concat(key.Split(Path.GetInvalidFileNameChars())));

    public Task SaveAsync(string key, string content) => File.WriteAllTextAsync(PathFor(key), content);

    public async Task<string?> LoadAsync(string key) => File.Exists(PathFor(key)) ? await File.ReadAllTextAsync(PathFor(key)) : null;

    public bool Exists(string key) => File.Exists(PathFor(key));

    public void Remove(string key)
    {
        if (File.Exists(PathFor(key))) File.Delete(PathFor(key));
    }
}

public sealed class MauiAppTheme : IAppTheme
{
    public bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;
}

/// <summary>Microphone recording and audio playback with Plugin.Maui.Audio (speaking and listening questions).</summary>
public sealed class MauiAudioService : IAudioService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Plugin.Maui.Audio.IAudioManager _manager = Plugin.Maui.Audio.AudioManager.Current;
    private Plugin.Maui.Audio.IAudioRecorder? _recorder;
    private Plugin.Maui.Audio.IAudioPlayer? _player;
    private DateTime _startedAt;

    public bool IsRecording => _recorder?.IsRecording == true;

    public async Task<bool> StartRecordingAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted) return false;

        StopPlayback();
        _recorder ??= _manager.CreateRecorder();
        await _recorder.StartAsync();
        _startedAt = DateTime.UtcNow;
        return true;
    }

    public async Task<RecordedAudio?> StopRecordingAsync()
    {
        if (_recorder is not { IsRecording: true }) return null;
        var source = await _recorder.StopAsync();
        var duration = DateTime.UtcNow - _startedAt;

        // Copy the recording so it can be uploaded and replayed more than once.
        await using var stream = source.GetAudioStream();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        var bytes = copy.ToArray();
        return new RecordedAudio("answer.wav", "audio/wav", () => Task.FromResult<Stream>(new MemoryStream(bytes)), duration);
    }

    public async Task PlayUrlAsync(string url, CancellationToken ct = default)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct);
        Play(new MemoryStream(bytes));
    }

    public async Task PlayRecordingAsync(RecordedAudio audio, CancellationToken ct = default) =>
        Play(await audio.OpenReadAsync());

    private void Play(Stream stream)
    {
        StopPlayback();
        _player = _manager.CreatePlayer(stream);
        _player.Play();
    }

    public void StopPlayback()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Stop();
        _player.Dispose();
        _player = null;
    }
}

/// <summary>Phase 8: chat events arrive on a background thread; screens update on the main thread.</summary>
public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }
}

// MauiPushTokenProvider moved to Services/Push.cs in Phase 10 (Firebase builds).
