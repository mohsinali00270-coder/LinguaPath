using LinguaPath.Mobile.Core.Api;
using LinguaPath.Shared.Contracts.Chat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.Mobile.Core.Services;

/// <summary>Runs a callback on the UI thread (collections bound to the screen must change there). The MAUI app supplies it.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Device push token (FCM/APNs). The MAUI app supplies it; null when push isn't set up on this build.</summary>
public interface IPushTokenProvider
{
    Task<string?> GetTokenAsync();
}

/// <summary>
/// The live chat connection (/hubs/chat). One per signed-in session; reconnects by itself.
/// Events are raised on the UI thread. Sending falls back to REST when the connection is down.
/// </summary>
public interface IChatConnection
{
    bool IsConnected { get; }
    event EventHandler<MessageDto>? MessageReceived;
    event EventHandler<MessageDeletedDto>? MessageDeleted;
    event EventHandler<ConversationChangedDto>? ConversationChanged;
    event EventHandler<SeenDto>? Seen;
    event EventHandler<TypingDto>? Typing;
    event EventHandler<PresenceDto>? PresenceChanged;
    event EventHandler<ModerationWarningDto>? ModerationWarning;
    event EventHandler<bool>? ConnectionChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task<HubSendResult> SendAsync(int conversationId, SendMessageRequest request, CancellationToken ct = default);
    Task TypingAsync(int conversationId, bool isTyping);
    Task MarkSeenAsync(int conversationId, long upToMessageId);
}

public sealed class ChatConnection(ApiSettings api, IAuthSession session, IUiDispatcher ui, IServiceProvider services) : IChatConnection, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private bool _watchingSession;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public event EventHandler<MessageDto>? MessageReceived;
    public event EventHandler<MessageDeletedDto>? MessageDeleted;
    public event EventHandler<ConversationChangedDto>? ConversationChanged;
    public event EventHandler<SeenDto>? Seen;
    public event EventHandler<TypingDto>? Typing;
    public event EventHandler<PresenceDto>? PresenceChanged;
    public event EventHandler<ModerationWarningDto>? ModerationWarning;
    public event EventHandler<bool>? ConnectionChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!session.HasTokens) return;
        if (!_watchingSession)
        {
            _watchingSession = true;
            session.SignedOut += async (_, _) => await StopAsync();
        }
        await _gate.WaitAsync(ct);
        try
        {
            _hub ??= Build();
            if (_hub.State == HubConnectionState.Disconnected)
            {
                await _hub.StartAsync(ct);
                Raise(ConnectionChanged, true);
            }
        }
        catch (Exception)
        {
            // Offline or server down: screens still work over REST; we try again next time a chat screen opens.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_hub is not null)
            {
                await _hub.DisposeAsync();
                _hub = null;
                Raise(ConnectionChanged, false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HubSendResult> SendAsync(int conversationId, SendMessageRequest request, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            try
            {
                return await _hub!.InvokeAsync<HubSendResult>("SendMessage", conversationId, request, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Dropped mid-call: the REST call below is safe to repeat because of the ClientId.
            }
        }
        try
        {
            var response = await services.GetRequiredService<ISocialApi>().SendMessageAsync(conversationId, request, ct);
            return new HubSendResult(true, response.Data, null, null);
        }
        catch (Exception ex)
        {
            var error = ApiError.From(ex);
            return new HubSendResult(false, null, error.Code, error.Message);
        }
    }

    public async Task TypingAsync(int conversationId, bool isTyping)
    {
        if (!IsConnected) return;
        try
        {
            await _hub!.SendAsync("Typing", conversationId, isTyping);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    public async Task MarkSeenAsync(int conversationId, long upToMessageId)
    {
        try
        {
            if (IsConnected) await _hub!.SendAsync("MarkSeen", conversationId, upToMessageId);
            else await services.GetRequiredService<ISocialApi>().MarkSeenAsync(conversationId, upToMessageId);
        }
        catch (Exception)
        {
            // best effort; the next open marks it again
        }
    }

    private HubConnection Build()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(api.BaseUrl), "/hubs/chat"), o => o.AccessTokenProvider = () => session.GetAccessTokenAsync())
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .AddJsonProtocol(o => o.PayloadSerializerOptions = JsonDefaults.Options)
            .Build();

        hub.On<MessageDto>("MessageReceived", m => Raise(MessageReceived, m));
        hub.On<MessageDeletedDto>("MessageDeleted", m => Raise(MessageDeleted, m));
        hub.On<ConversationChangedDto>("ConversationChanged", m => Raise(ConversationChanged, m));
        hub.On<SeenDto>("Seen", m => Raise(Seen, m));
        hub.On<TypingDto>("Typing", m => Raise(Typing, m));
        hub.On<PresenceDto>("PresenceChanged", m => Raise(PresenceChanged, m));
        hub.On<ModerationWarningDto>("ModerationWarning", m => Raise(ModerationWarning, m));
        hub.Reconnected += _ =>
        {
            Raise(ConnectionChanged, true);
            return Task.CompletedTask;
        };
        hub.Reconnecting += _ =>
        {
            Raise(ConnectionChanged, false);
            return Task.CompletedTask;
        };
        hub.Closed += _ =>
        {
            Raise(ConnectionChanged, false);
            return Task.CompletedTask;
        };
        return hub;
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null) return;
        ui.Post(() => handler(this, value));
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}

/// <summary>
/// Shows strike warnings, suspensions and blocks (FR-M12) wherever the learner is, and makes them accept the rules after a first strike.
/// </summary>
public sealed class ModerationNotices(IChatConnection chat, ISocialApi api, IDialogService dialogs, IAuthSession session)
{
    private bool _attached;
    private bool _showing;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        chat.ModerationWarning += async (_, w) => await ShowAsync(w.Code, w.Message, w.Blocked);
    }

    /// <summary>Call after a send fails with a moderation code: shows the right dialog.</summary>
    public async Task HandleAsync(string? code, string message)
    {
        switch (code)
        {
            case "warning_pending":
                await ShowAsync(code, message, false);
                break;
            case "account_blocked":
                await ShowAsync(code, message, true);
                break;
            case "abusive_blocked" or "spam_blocked" or "chat_suspended":
                // The server may have added a strike: check if there's a warning to accept.
                await dialogs.AlertAsync("Message not sent", message);
                await CheckPendingAsync();
                break;
            default:
                await dialogs.AlertAsync("Message not sent", message);
                break;
        }
    }

    public async Task CheckPendingAsync()
    {
        try
        {
            var status = (await api.GetModerationAsync()).Data;
            if (status?.PendingWarning is { } w) await ShowAsync(w.Code, w.Message, status.Blocked);
        }
        catch (Exception)
        {
            // not critical
        }
    }

    private async Task ShowAsync(string code, string message, bool blocked)
    {
        if (_showing) return;
        _showing = true;
        try
        {
            if (blocked)
            {
                await dialogs.AlertAsync("Account blocked", message);
                await session.SignOutAsync(callServer: false);
                return;
            }
            if (code is "strike_warning" or "warning_pending")
            {
                await dialogs.AlertAsync("Community rules",
                    message + "\n\nBe kind, keep chats about learning, and never share links, phone numbers or social accounts.", "I understand");
                try { await api.AcknowledgeWarningAsync(); } catch (Exception) { /* retried next time */ }
                return;
            }
            await dialogs.AlertAsync("Chat suspended", message);
        }
        finally
        {
            _showing = false;
        }
    }
}

/// <summary>Starts the social side once per app run after sign-in: live chat connection, moderation dialogs and the push token.</summary>
public sealed class SocialBootstrap(IChatConnection chat, ModerationNotices notices, IPushTokenProvider push, IDeviceContext device, IAccountApi account)
{
    private bool _tokenSent;

    public async Task EnsureStartedAsync()
    {
        notices.Attach();
        await chat.StartAsync();
        if (_tokenSent) return;
        _tokenSent = true;
        try
        {
            var token = await push.GetTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
                await account.RegisterDeviceAsync(new LinguaPath.Shared.Contracts.Catalog.RegisterDeviceRequest(device.DeviceId, device.Platform, device.AppVersion, token));
        }
        catch (Exception)
        {
            _tokenSent = false; // try again next time
        }
    }
}
