using System.Collections.Concurrent;
using System.Threading.Channels;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Application.Features.Social;
using LinguaPath.Domain.Constants;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LinguaPath.Api.Chat;

/// <summary>Event names the app listens for on /hubs/chat.</summary>
public static class ChatEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string MessageDeleted = "MessageDeleted";
    public const string ConversationChanged = "ConversationChanged";
    public const string Seen = "Seen";
    public const string Typing = "Typing";
    public const string PresenceChanged = "PresenceChanged";
    public const string ModerationWarning = "ModerationWarning";
}

/// <summary>
/// Real-time chat (FR-C01 .. FR-C05). Clients connect with the access token in the "access_token" query string.
/// Sending also works over REST (POST /conversations/{id}/messages); both go through <see cref="IChatService"/>.
/// </summary>
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class ChatHub(IChatService chat, PresenceTracker presence, AppDbContext db, ILogger<ChatHub> logger) : Hub
{
    private Guid UserId => Guid.TryParse(Context.UserIdentifier, out var id) ? id : throw new HubException("Not signed in.");

    public override async Task OnConnectedAsync()
    {
        var userId = UserId;
        var first = presence.Connected(userId, Context);
        await chat.MarkDeliveredAsync(userId, Context.ConnectionAborted);
        if (first) await AnnounceAsync(userId, true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Guid.TryParse(Context.UserIdentifier, out var userId) && presence.Disconnected(userId, Context.ConnectionId))
        {
            try
            {
                await AnnounceAsync(userId, false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Presence update failed");
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<HubSendResult> SendMessage(int conversationId, SendMessageRequest request)
    {
        var result = await chat.SendAsync(UserId, conversationId, request, Context.ConnectionAborted);
        return result.IsSuccess
            ? new HubSendResult(true, result.Value, null, null)
            : new HubSendResult(false, null, result.Error!.Code, result.Error.Message);
    }

    public async Task Typing(int conversationId, bool isTyping)
    {
        var userId = UserId;
        if (!presence.TypingAllowed(userId, conversationId, isTyping)) return;
        if (!await chat.IsMemberAsync(userId, conversationId, Context.ConnectionAborted)) return;
        var others = (await chat.MemberIdsAsync(conversationId, Context.ConnectionAborted)).Where(id => id != userId).Select(id => id.ToString()).ToList();
        var name = Context.User?.FindFirst(LinguaPath.Infrastructure.Security.AppClaims.FullName)?.Value ?? "";
        await Clients.Users(others).SendAsync(ChatEvents.Typing, new TypingDto(conversationId, userId, name, isTyping), Context.ConnectionAborted);
    }

    public async Task MarkSeen(int conversationId, long upToMessageId) =>
        await chat.MarkSeenAsync(UserId, conversationId, upToMessageId, Context.ConnectionAborted);

    /// <summary>FR-C04: tell direct-chat partners I came online or went offline, unless I hide it.</summary>
    private async Task AnnounceAsync(Guid userId, bool online)
    {
        var show = await db.UserPrivacySettings.Where(p => p.UserId == userId).Select(p => (bool?)p.ShowOnline).FirstOrDefaultAsync() ?? true;
        if (!show) return;
        var partners = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.UserId != userId && m.IsActive && m.Conversation.Type == ConversationType.Direct
                        && m.Conversation.Members.Any(x => x.UserId == userId && x.IsActive))
            .Select(m => m.UserId.ToString()).Distinct().Take(500).ToListAsync();
        if (partners.Count == 0) return;
        await Clients.Users(partners).SendAsync(ChatEvents.PresenceChanged, new PresenceDto(userId, online, online ? null : presence.LastSeen(userId)));
    }
}

/// <summary>SignalR users are identified by the JWT "sub" claim.</summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}

/// <summary>Live connections per learner (one API instance; use a SignalR backplane such as Redis when scaling out).</summary>
public sealed class PresenceTracker : IPresenceTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> connections = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lastSeen = new();
    private readonly ConcurrentDictionary<(Guid, int), DateTimeOffset> typing = new();

    /// <summary>True when this is the learner's first connection.</summary>
    public bool Connected(Guid userId, HubCallerContext context)
    {
        var set = connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>());
        set[context.ConnectionId] = context;
        return set.Count == 1;
    }

    /// <summary>True when the learner has no connections left.</summary>
    public bool Disconnected(Guid userId, string connectionId)
    {
        if (!connections.TryGetValue(userId, out var set)) return false;
        set.TryRemove(connectionId, out _);
        lastSeen[userId] = DateTimeOffset.UtcNow;
        if (!set.IsEmpty) return false;
        connections.TryRemove(userId, out _);
        return true;
    }

    public bool IsOnline(Guid userId) => connections.TryGetValue(userId, out var set) && !set.IsEmpty;

    public DateTimeOffset? LastSeen(Guid userId) => lastSeen.TryGetValue(userId, out var t) ? t : null;

    /// <summary>At most one "typing" event per user and chat every two seconds; "stopped" always goes through.</summary>
    public bool TypingAllowed(Guid userId, int conversationId, bool isTyping)
    {
        if (!isTyping) return true;
        var now = DateTimeOffset.UtcNow;
        if (typing.TryGetValue((userId, conversationId), out var last) && now - last < TimeSpan.FromSeconds(2)) return false;
        typing[(userId, conversationId)] = now;
        return true;
    }

    public void Abort(Guid userId)
    {
        if (!connections.TryRemove(userId, out var set)) return;
        foreach (var c in set.Values) c.Abort();
    }
}

public sealed class SignalRChatNotifier(IHubContext<ChatHub> hub, PresenceTracker presence) : IChatNotifier
{
    private static IReadOnlyList<string> Ids(IEnumerable<Guid> ids) => ids.Distinct().Select(i => i.ToString()).ToList();

    public Task MessageAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(ChatEvents.MessageReceived, message, ct);

    public Task MessageDeletedAsync(IReadOnlyCollection<Guid> userIds, MessageDeletedDto deleted, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(ChatEvents.MessageDeleted, deleted, ct);

    public Task ConversationChangedAsync(IReadOnlyCollection<Guid> userIds, ConversationChangedDto change, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(ChatEvents.ConversationChanged, change, ct);

    public Task SeenAsync(IReadOnlyCollection<Guid> userIds, SeenDto seen, CancellationToken ct = default) =>
        userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(Ids(userIds)).SendAsync(ChatEvents.Seen, seen, ct);

    public Task ModerationWarningAsync(Guid userId, ModerationWarningDto warning, CancellationToken ct = default) =>
        hub.Clients.User(userId.ToString()).SendAsync(ChatEvents.ModerationWarning, warning, ct);

    public Task DisconnectAsync(Guid userId, CancellationToken ct = default)
    {
        presence.Abort(userId);
        return Task.CompletedTask;
    }
}

/// <summary>Evicts the cached sign-in state so a block applies on the very next request.</summary>
public sealed class ApiUserSessionCache(IMemoryCache cache) : IUserSessionCache
{
    public void Invalidate(Guid userId) => LinguaPath.Api.Infrastructure.SecurityStampCheck.Invalidate(cache, userId);
}

/// <summary>In-memory queue for the AI scan (FR-M03); messages it misses are swept from the database.</summary>
public sealed class ModerationScanQueue : IModerationQueue
{
    private readonly Channel<long> channel = Channel.CreateBounded<long>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
    });

    public ChannelReader<long> Reader => channel.Reader;

    public void Enqueue(long messageId) => channel.Writer.TryWrite(messageId);
}

public sealed class ModerationScanWorker(ModerationScanQueue queue, IServiceScopeFactory scopes, ILogger<ModerationScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSweep = DateTimeOffset.UtcNow.AddMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                long? id = null;
                try
                {
                    id = await queue.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // idle: fall through to the sweep check
                }

                if (id is { } messageId) await ScanAsync(messageId, stoppingToken);

                if (DateTimeOffset.UtcNow >= nextSweep)
                {
                    nextSweep = DateTimeOffset.UtcNow.AddMinutes(5);
                    using var scope = scopes.CreateScope();
                    var pending = await scope.ServiceProvider.GetRequiredService<IModerationService>().PendingScansAsync(200, stoppingToken);
                    foreach (var p in pending) await ScanAsync(p, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Moderation scan loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }
    }

    private async Task ScanAsync(long messageId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IModerationService>().ScanAsync(messageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI scan of message {MessageId} failed", messageId);
        }
    }
}
