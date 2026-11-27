using System.Text.Json;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Content;
using LinguaPath.Shared.Contracts.Notifications;
using LinguaPath.Shared.Contracts.Words;
using Refit;

namespace LinguaPath.Mobile.Core.Services;

// ====================================================================== platform hooks (implemented in the MAUI project)

/// <summary>Is the phone online? Raised when that changes (MAUI Connectivity).</summary>
public interface IConnectivityMonitor
{
    bool IsOnline { get; }
    event EventHandler<bool>? Changed;
}

/// <summary>App-private folder for small files that must be written synchronously (crash reports).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
}

/// <summary>Applies the UI language: culture, right-to-left layout for Urdu, and a fresh shell so every page re-reads its text.</summary>
public interface ILanguageService
{
    string Current { get; }
    /// <summary>Returns true when the language changed (and the shell was rebuilt).</summary>
    Task<bool> ApplyAsync(string language);
}

// ====================================================================== offline sync (NFR-09)

/// <summary>Something done offline that the server still needs to hear about.</summary>
public sealed record OfflineOp(string Kind, int EntityId, int? LearnerCourseId, int Seconds, RecallGrade? Grade, DateTimeOffset At)
{
    public const string LectureComplete = "lecture-complete";
    public const string WordReview = "word-review";

    public string Key => $"{Kind}:{EntityId}";
}

public interface IOfflineSyncQueue
{
    int Count { get; }
    Task EnqueueAsync(OfflineOp op);
    /// <summary>Sends what's queued. Stops at the first network failure; drops items the server refuses (4xx).</summary>
    Task<int> FlushAsync(CancellationToken ct = default);
}

/// <summary>
/// Lecture completions and flashcard reviews made without a connection, replayed in order when the phone is back online.
/// The same lecture or word is kept once (the latest), so a review isn't applied twice.
/// </summary>
public sealed class OfflineSyncQueue(IOfflineStore store, IContentApi content, IPracticeApi practice) : IOfflineSyncQueue
{
    private const string StoreKey = "offline-sync-queue";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<OfflineOp>? _items;

    public int Count => _items?.Count ?? 0;

    public async Task EnqueueAsync(OfflineOp op)
    {
        await _gate.WaitAsync();
        try
        {
            var items = await LoadAsync();
            var existing = items.FindIndex(i => i.Key == op.Key);
            if (existing >= 0)
            {
                // Reading time adds up; the newest grade wins.
                var old = items[existing];
                items[existing] = op with { Seconds = Math.Min(3600, old.Seconds + op.Seconds) };
            }
            else items.Add(op);
            await SaveAsync(items);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return 0; // already flushing
        try
        {
            var items = await LoadAsync();
            var sent = 0;
            while (items.Count > 0)
            {
                var op = items[0];
                try
                {
                    await SendAsync(op, ct);
                    sent++;
                }
                catch (ApiException ex) when ((int)ex.StatusCode is >= 400 and < 500 && ex.StatusCode != System.Net.HttpStatusCode.Unauthorized
                                                && ex.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Already completed, word deleted, course changed: nothing left to sync for this one.
                }
                catch (Exception)
                {
                    break; // still offline or the server is down: keep it for next time
                }
                items.RemoveAt(0);
                await SaveAsync(items);
            }
            return sent;
        }
        finally { _gate.Release(); }
    }

    private Task SendAsync(OfflineOp op, CancellationToken ct) => op.Kind switch
    {
        OfflineOp.LectureComplete when op.LearnerCourseId is { } lc =>
            content.CompleteAsync(op.EntityId, new CompleteLectureRequest(lc, op.Seconds), ct),
        OfflineOp.WordReview when op.Grade is { } g => practice.ReviewWordAsync(op.EntityId, new ReviewWordRequest(g), ct),
        _ => Task.CompletedTask
    };

    private async Task<List<OfflineOp>> LoadAsync()
    {
        if (_items is not null) return _items;
        try
        {
            var json = await store.LoadAsync(StoreKey);
            _items = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<OfflineOp>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            _items = [];
        }
        return _items;
    }

    private Task SaveAsync(List<OfflineOp> items)
    {
        _items = items;
        if (items.Count == 0)
        {
            store.Remove(StoreKey);
            return Task.CompletedTask;
        }
        return store.SaveAsync(StoreKey, JsonSerializer.Serialize(items, JsonDefaults.Options));
    }
}

// ====================================================================== crash reporting (NFR-14)

public interface IClientErrorReporter
{
    /// <summary>Writes the error to disk straight away (the app may be about to close). Sent on the next start.</summary>
    void Record(Exception ex, string? page = null);
    Task<int> SendPendingAsync(CancellationToken ct = default);
}

public sealed class ClientErrorReporter(IAppPaths paths, IDeviceContext device, IClientErrorsApi api, TimeProvider clock) : IClientErrorReporter
{
    private const int MaxPending = 10;
    private static readonly object FileLock = new();
    private string Folder => Path.Combine(paths.DataDirectory, "crash-reports");

    public void Record(Exception ex, string? page = null)
    {
        try
        {
            var report = new ClientErrorReport(device.Platform.ToString(), device.AppVersion, Environment.OSVersion.VersionString, null,
                Cut(ex.Message, 1000), ex.GetType().FullName, Cut(ex.ToString(), 8000), page, clock.GetUtcNow());
            lock (FileLock)
            {
                Directory.CreateDirectory(Folder);
                var files = Directory.GetFiles(Folder, "*.json").OrderBy(f => f).ToList();
                foreach (var old in files.Take(Math.Max(0, files.Count - MaxPending + 1))) File.Delete(old);
                File.WriteAllText(Path.Combine(Folder, $"{clock.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.json"),
                    JsonSerializer.Serialize(report, JsonDefaults.Options));
            }
        }
        catch
        {
            // Reporting must never be the thing that crashes.
        }
    }

    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        string[] files;
        lock (FileLock) files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.json").OrderBy(f => f).ToArray() : [];
        var sent = 0;
        foreach (var file in files)
        {
            try
            {
                var report = JsonSerializer.Deserialize<ClientErrorReport>(await File.ReadAllTextAsync(file, ct), JsonDefaults.Options);
                if (report is not null) await api.ReportAsync(report, ct);
                sent++;
            }
            catch (JsonException) { /* unreadable: drop it */ }
            catch (Exception) { break; } // offline: try again next start
            lock (FileLock) File.Delete(file);
        }
        return sent;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}

// ====================================================================== notification taps (push and inbox)

/// <summary>Where a notification's data opens: the same rules for a tapped push and a tapped inbox entry.</summary>
public static class NotificationRouter
{
    public static (string Route, Dictionary<string, object>? Parameters)? Resolve(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null || !data.TryGetValue("type", out var type)) return null;
        string? Get(string key) => data.TryGetValue(key, out var v) ? v : null;
        var lc = Get("learnerCourseId");
        return type switch
        {
            "today" => (Routes.Home, null),
            "dailyQuiz" when lc is not null => (Routes.Quiz, new() { ["mode"] = "daily", ["learnerCourseId"] = lc }),
            "exams" when lc is not null => (Routes.Exams, new() { ["learnerCourseId"] = lc }),
            "certificate" => (Routes.Certificates, null),
            "post" => (Routes.Posts, null),
            "chat" when Get("conversationId") is { } c => (Routes.Chat, new() { ["id"] = c }),
            "competition" when Get("competitionId") is { } m => (Routes.Match, new() { ["id"] = m }),
            _ => null
        };
    }
}

/// <summary>
/// Runs once after sign-in when Home first appears: chat connection and push token, then sends queued offline work and
/// crash reports, and applies the account's UI language if this phone was set differently.
/// </summary>
public sealed class SessionStartup
{
    private readonly SocialBootstrap _social;
    private readonly IOfflineSyncQueue _sync;
    private readonly IClientErrorReporter _crashes;
    private readonly IConnectivityMonitor _connectivity;
    private readonly ILanguageService _language;
    private readonly IAuthSession _session;
    private bool _started;

    public SessionStartup(SocialBootstrap social, IOfflineSyncQueue sync, IClientErrorReporter crashes, IConnectivityMonitor connectivity,
        ILanguageService language, IAuthSession session, IDialogService dialogs)
    {
        (_social, _sync, _crashes, _connectivity, _language, _session) = (social, sync, crashes, connectivity, language, session);
        connectivity.Changed += async (_, online) =>
        {
            if (!online || session.User is null) return;
            try
            {
                if (await sync.FlushAsync() > 0) await dialogs.ToastAsync(Localization.Localizer.T("Offline_Synced"));
            }
            catch { /* next time */ }
        };
        // After sign-out the next sign-in runs the start-up again.
        session.SignedOut += (_, _) => _started = false;
    }

    public async Task RunAsync()
    {
        if (_started) return;
        _started = true;
        try { await _social.EnsureStartedAsync(); } catch { /* chat retries on its own */ }
        try { if (_connectivity.IsOnline) await _sync.FlushAsync(); } catch { }
        try { await _crashes.SendPendingAsync(); } catch { }
        if (_session.User is { } user && Localization.Localizer.Normalise(user.UiLanguage) != _language.Current)
            await _language.ApplyAsync(user.UiLanguage);
    }
}
