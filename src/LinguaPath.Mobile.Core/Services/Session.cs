using System.Text.Json;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Profile;
using Refit;

namespace LinguaPath.Mobile.Core.Services;

public sealed record SessionUser(Guid Id, string Email, string FullName, string? DisplayName, string? PhotoUrl, string UiLanguage)
{
    public string Greeting => DisplayName ?? FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? FullName;

    public static SessionUser From(UserSummaryDto u) => new(u.Id, u.Email, u.FullName, u.DisplayName, u.PhotoUrl, u.UiLanguage);
    public static SessionUser From(UserProfileDto p) => new(p.Id, p.Email, p.FullName, p.DisplayName, p.PhotoUrl, p.UiLanguage);
}

/// <summary>Tokens + current user, persisted in secure storage (FR-L03: SecureStorage).</summary>
public interface IAuthSession
{
    SessionUser? User { get; }
    bool HasTokens { get; }
    event EventHandler? SignedOut;

    Task LoadAsync();
    Task SignInAsync(AuthResponse response);
    Task SignInWithTokensAsync(string accessToken, string refreshToken, DateTimeOffset accessExpires, DateTimeOffset refreshExpires);
    Task UpdateUserAsync(SessionUser user);

    /// <summary>Returns a valid access token, refreshing it first if it is about to expire. Null = signed out.</summary>
    Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Clears local tokens. When <paramref name="callServer"/> is true the refresh token is revoked first.</summary>
    Task SignOutAsync(bool callServer, bool raiseEvent = true);
}

public sealed class AuthSession(ISecureStore store, IAuthApi authApi, Lazy<IAccountApi> accountApi, IDeviceContext device) : IAuthSession
{
    private const string AccessKey = "lp.access";
    private const string AccessExpKey = "lp.access.exp";
    private const string RefreshKey = "lp.refresh";
    private const string RefreshExpKey = "lp.refresh.exp";
    private const string UserKey = "lp.user";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _access;
    private DateTimeOffset _accessExpires;
    private string? _refresh;
    private DateTimeOffset _refreshExpires;
    private bool _loaded;

    public SessionUser? User { get; private set; }
    public bool HasTokens => !string.IsNullOrEmpty(_refresh) && _refreshExpires > DateTimeOffset.UtcNow;
    public event EventHandler? SignedOut;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _access = await store.GetAsync(AccessKey);
        _refresh = await store.GetAsync(RefreshKey);
        _accessExpires = ParseDate(await store.GetAsync(AccessExpKey));
        _refreshExpires = ParseDate(await store.GetAsync(RefreshExpKey));
        var userJson = await store.GetAsync(UserKey);
        if (!string.IsNullOrEmpty(userJson))
        {
            try { User = JsonSerializer.Deserialize<SessionUser>(userJson, JsonDefaults.Options); }
            catch (JsonException) { User = null; }
        }
        _loaded = true;
    }

    public async Task SignInAsync(AuthResponse r)
    {
        await SaveTokensAsync(r.AccessToken, r.AccessTokenExpiresAt, r.RefreshToken, r.RefreshTokenExpiresAt);
        await UpdateUserAsync(SessionUser.From(r.User));
    }

    public Task SignInWithTokensAsync(string accessToken, string refreshToken, DateTimeOffset accessExpires, DateTimeOffset refreshExpires) =>
        SaveTokensAsync(accessToken, accessExpires, refreshToken, refreshExpires);

    public async Task UpdateUserAsync(SessionUser user)
    {
        User = user;
        await store.SetAsync(UserKey, JsonSerializer.Serialize(user, JsonDefaults.Options));
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await LoadAsync();
        if (!forceRefresh && !string.IsNullOrEmpty(_access) && _accessExpires > DateTimeOffset.UtcNow.AddSeconds(60))
            return _access;
        if (!HasTokens) return null;

        var tokenBefore = _access;
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited.
            if (_access != tokenBefore && _accessExpires > DateTimeOffset.UtcNow.AddSeconds(60))
                return _access;

            var response = await authApi.RefreshAsync(new RefreshTokenRequest(_refresh!, device.DeviceId), ct);
            var r = response.Data!;
            await SaveTokensAsync(r.AccessToken, r.AccessTokenExpiresAt, r.RefreshToken, r.RefreshTokenExpiresAt);
            await UpdateUserAsync(SessionUser.From(r.User));
            return _access;
        }
        catch (ApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // Refresh token revoked, expired, account blocked or suspended.
            await SignOutAsync(callServer: false);
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task SignOutAsync(bool callServer, bool raiseEvent = true)
    {
        if (callServer && !string.IsNullOrEmpty(_refresh))
        {
            try { await accountApi.Value.LogoutAsync(new LogoutRequest(_refresh)); }
            catch (Exception) { /* signing out locally matters more than telling the server */ }
        }

        _access = _refresh = null;
        _accessExpires = _refreshExpires = default;
        User = null;
        store.Remove(AccessKey);
        store.Remove(AccessExpKey);
        store.Remove(RefreshKey);
        store.Remove(RefreshExpKey);
        store.Remove(UserKey);
        if (raiseEvent) SignedOut?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveTokensAsync(string access, DateTimeOffset accessExp, string refresh, DateTimeOffset refreshExp)
    {
        _access = access;
        _accessExpires = accessExp;
        _refresh = refresh;
        _refreshExpires = refreshExp;
        _loaded = true;
        await store.SetAsync(AccessKey, access);
        await store.SetAsync(AccessExpKey, accessExp.ToUnixTimeSeconds().ToString());
        await store.SetAsync(RefreshKey, refresh);
        await store.SetAsync(RefreshExpKey, refreshExp.ToUnixTimeSeconds().ToString());
    }

    private static DateTimeOffset ParseDate(string? unixSeconds) =>
        long.TryParse(unixSeconds, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : default;
}

/// <summary>Adds the bearer token, and on a 401 refreshes once and retries the request.</summary>
public sealed class AuthHeaderHandler(IAuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(ct: ct) ?? throw new SessionExpiredException();
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Buffer the body so it can be sent a second time if we need to retry (e.g. photo uploads).
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync();

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) return response;

        // Token was revoked server-side (e.g. stamp changed). Try one forced refresh.
        var fresh = await session.GetAccessTokenAsync(forceRefresh: true, ct: ct);
        if (fresh is null || fresh == token) return response;

        response.Dispose();
        var retry = await CloneAsync(request);
        retry.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, ct);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var h in original.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in original.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }
}
