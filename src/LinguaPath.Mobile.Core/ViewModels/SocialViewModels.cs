using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Chat;
using LinguaPath.Shared.Contracts.Social;

namespace LinguaPath.Mobile.Core.ViewModels;

internal static class SocialText
{
    public static string When(DateTimeOffset? at)
    {
        if (at is null) return "";
        var local = at.Value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today ? local.ToString("HH:mm")
            : local.Date == today.AddDays(-1) ? "Yesterday"
            : local.Date > today.AddDays(-7) ? local.ToString("ddd")
            : local.ToString("d MMM");
    }

    public static string Initial(string? name) => string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

    public static string Icon(string? key) => key switch
    {
        "book" => "📚", "globe" => "🌍", "star" => "⭐", "music" => "🎵", "coffee" => "☕", "trophy" => "🏆", "heart" => "❤️",
        "rocket" => "🚀", "leaf" => "🌿", _ => "💬"
    };

    public static string LastSeen(bool? online, DateTimeOffset? lastSeen) =>
        online == true ? "online" : lastSeen is { } t ? $"last seen {When(t)}" : "";

    public static readonly string[] Reactions = ["👍", "❤️", "😂", "😮", "😢", "🙏", "🎉", "👏"];
    public static readonly string[] IconKeys = ["chat", "book", "globe", "star", "music", "coffee", "trophy", "heart", "rocket", "leaf"];
}

// ===================================================================== chat list (FR-C06, FR-C07)

public sealed record ChatRow(ConversationSummaryDto Dto)
{
    public int Id => Dto.Id;
    public string Title => Dto.Title;
    public string Preview => Dto.LastMessage ?? (Dto.Type == ConversationType.Group ? "Group created" : "Say hello 👋");
    public string TimeText => SocialText.When(Dto.LastMessageAt);
    public bool HasUnread => Dto.UnreadCount > 0;
    public string UnreadText => Dto.UnreadCount > 99 ? "99+" : Dto.UnreadCount.ToString();
    public string Avatar => Dto.Type == ConversationType.Group ? SocialText.Icon(Dto.IconKey) : SocialText.Initial(Dto.Title);
    public string? PhotoUrl => Dto.PhotoUrl;
    public bool HasPhoto => !string.IsNullOrEmpty(Dto.PhotoUrl);
    public bool IsOnline => Dto.OtherOnline == true;
    public bool IsMuted => Dto.IsMuted;
}

public sealed partial class ChatsViewModel(
    ISocialApi api,
    IChatConnection chat,
    SocialBootstrap bootstrap,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private bool _subscribed;

    public ObservableCollection<ChatRow> Chats { get; } = [];

    [ObservableProperty] private bool showArchived;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool isRefreshing;
    [ObservableProperty] private string title = "Chats";

    public override async Task OnAppearingAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            chat.MessageReceived += OnChanged;
            chat.ConversationChanged += OnConversationChanged;
            chat.MessageDeleted += OnDeleted;
        }
        await bootstrap.EnsureStartedAsync();
        await LoadAsync();
    }

    public override Task OnDisappearingAsync()
    {
        if (_subscribed)
        {
            _subscribed = false;
            chat.MessageReceived -= OnChanged;
            chat.ConversationChanged -= OnConversationChanged;
            chat.MessageDeleted -= OnDeleted;
        }
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, MessageDto e) => _ = LoadQuietlyAsync();
    private void OnConversationChanged(object? sender, ConversationChangedDto e) => _ = LoadQuietlyAsync();
    private void OnDeleted(object? sender, MessageDeletedDto e) => _ = LoadQuietlyAsync();

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync();
        IsRefreshing = false;
    }

    [RelayCommand]
    private Task ToggleArchived()
    {
        ShowArchived = !ShowArchived;
        Title = ShowArchived ? "Archived chats" : "Chats";
        return LoadAsync();
    }

    private Task LoadAsync() => RunAsync(FillAsync);

    private async Task LoadQuietlyAsync()
    {
        try
        {
            await FillAsync();
        }
        catch (Exception)
        {
            // the next pull-to-refresh will show the error
        }
    }

    private async Task FillAsync()
    {
        var rows = (await api.GetConversationsAsync(ShowArchived)).Data ?? [];
        Chats.Clear();
        foreach (var r in rows) Chats.Add(new ChatRow(r));
        IsEmpty = Chats.Count == 0;
    }

    [RelayCommand]
    private Task Open(ChatRow? row) =>
        row is null ? Task.CompletedTask : navigation.GoToAsync(Routes.Chat, new Dictionary<string, object> { ["id"] = row.Id });

    [RelayCommand]
    private Task NewChat() => navigation.GoToAsync(Routes.People);

    [RelayCommand]
    private Task NewGroup() => navigation.GoToAsync(Routes.GroupCreate);

    [RelayCommand]
    private Task FindGroups() => navigation.GoToAsync(Routes.PublicGroups);

    /// <summary>Long-press menu: mute, archive, clear, leave (FR-C07, FR-C08).</summary>
    [RelayCommand]
    private async Task Actions(ChatRow? row)
    {
        if (row is null) return;
        var mute = row.IsMuted ? "Unmute" : "Mute";
        var archive = row.Dto.IsArchived ? "Unarchive" : "Archive";
        var leave = row.Dto.Type == ConversationType.Group ? "Leave group" : "Delete chat";
        var choice = await dialogs.ChooseAsync(row.Title, null, "Cancel", mute, archive, "Clear history", leave);
        if (choice is null) return;
        await RunAsync(async () =>
        {
            if (choice == mute) await api.UpdateConversationAsync(row.Id, Update(muted: !row.IsMuted));
            else if (choice == archive) await api.UpdateConversationAsync(row.Id, Update(archived: !row.Dto.IsArchived));
            else if (choice == "Clear history")
            {
                if (!await dialogs.ConfirmAsync("Clear history", "Remove all messages from this chat for you? Others still see them.")) return;
                await api.ClearAsync(row.Id);
            }
            else if (choice == leave)
            {
                if (!await dialogs.ConfirmAsync(leave, row.Dto.Type == ConversationType.Group ? "Leave this group?" : "Remove this chat from your list?")) return;
                await api.LeaveAsync(row.Id);
            }
            await FillAsync();
        });
    }

    internal static UpdateConversationRequest Update(bool? muted = null, bool? archived = null) =>
        new(null, null, null, null, null, null, null, null, muted, archived);
}

// ===================================================================== one chat (FR-C01 .. FR-C05, FR-C10 .. FR-C14, FR-C26, FR-C30)

public sealed partial class MessageRow : ObservableObject
{
    public MessageRow(MessageDto dto, Guid me, bool isGroup)
    {
        Me = me;
        IsGroup = isGroup;
        Apply(dto);
    }

    private Guid Me { get; }
    private bool IsGroup { get; }

    public MessageDto Dto { get; private set; } = null!;
    public long Id => Dto.Id;
    public string? ClientId => Dto.ClientId;
    public bool IsSystem => Dto.Type == MessageType.System;
    /// <summary>Phase 9: a competition invitation or result (FR-Q07, FR-C28).</summary>
    public bool IsCard => Dto.Type == MessageType.CompetitionCard && !Dto.IsDeleted;
    public bool IsMine => !IsSystem && !IsCard && Dto.SenderId == Me;
    public bool IsOther => !IsSystem && !IsCard && !IsMine;
    public bool ShowSender => IsGroup && IsOther;
    public string SenderName => Dto.SenderName;
    public string TimeText => Dto.SentAt.ToLocalTime().ToString("HH:mm");

    [ObservableProperty] private string displayText = "";
    [ObservableProperty] private bool isDeleted;
    [ObservableProperty] private string stateIcon = "";
    [ObservableProperty] private string reactionsText = "";
    [ObservableProperty] private bool hasReactions;
    [ObservableProperty] private string? translation;
    [ObservableProperty] private bool hasTranslation;
    [ObservableProperty] private bool isPending;
    [ObservableProperty] private string? replyText;
    [ObservableProperty] private bool hasReply;
    [ObservableProperty] private bool wasMasked;

    public void Apply(MessageDto dto)
    {
        // Recipients get updates without my delivery state: keep the one I have.
        if (dto.State is null && Dto?.State is not null && dto.SenderId == Me) dto = dto with { State = Dto.State };
        Dto = dto;
        IsDeleted = dto.IsDeleted;
        DisplayText = dto.IsDeleted ? "🚫 This message was deleted" : dto.Text ?? "";
        WasMasked = dto.WasMasked && !dto.IsDeleted;
        SetState(dto.State);
        ReactionsText = string.Join("  ", dto.Reactions.Select(r => r.Count > 1 ? $"{r.Emoji} {r.Count}" : r.Emoji));
        HasReactions = dto.Reactions.Count > 0;
        HasReply = dto.ReplyTo is not null;
        ReplyText = dto.ReplyTo is { } r ? $"↪ {r.SenderName}: {(r.IsDeleted ? "deleted message" : r.Text)}" : null;
        IsPending = false;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(TimeText));
    }

    public void SetState(DeliveryState? state) =>
        StateIcon = !IsMine || IsDeleted ? "" : state switch { DeliveryState.Seen => "✓✓ seen", DeliveryState.Delivered => "✓✓", _ => "✓" };

    public void MarkDeleted()
    {
        Dto = Dto with { IsDeleted = true, Text = null, Reactions = [] };
        Apply(Dto);
        HasTranslation = false;
    }

    public bool MentionsMe => Dto.Mentions.Contains(Me);
}

public sealed partial class ChatViewModel(
    ISocialApi api,
    IChatConnection chat,
    IAuthSession session,
    SocialBootstrap bootstrap,
    ModerationNotices notices,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _id;
    private bool _subscribed;
    private bool _hasOlder = true;
    private DateTimeOffset _lastTypingSent;
    private CancellationTokenSource? _typingClear;

    public ObservableCollection<MessageRow> Messages { get; } = [];

    /// <summary>The page scrolls to the newest message.</summary>
    public event EventHandler? MessageAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroup), nameof(Title), nameof(CanSend), nameof(CannotSendReason), nameof(HasCannotSend), nameof(Avatar))]
    private ConversationDto? conversation;

    [ObservableProperty] private string draft = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private MessageRow? replyTo;
    [ObservableProperty] private bool isLoadingOlder;

    public bool IsGroup => Conversation?.Type == ConversationType.Group;
    public string Title => Conversation?.Title ?? "Chat";
    public string Avatar => IsGroup ? SocialText.Icon(Conversation?.IconKey) : SocialText.Initial(Conversation?.Title);
    public bool CanSend => Conversation?.CanSend ?? false;
    public string? CannotSendReason => Conversation?.CannotSendReason;
    public bool HasCannotSend => Conversation is not null && !CanSend;
    public bool HasReply => ReplyTo is not null;
    public string ReplyPreview => ReplyTo is null ? "" : $"Replying to {(ReplyTo.IsMine ? "yourself" : ReplyTo.SenderName)}: {ReplyTo.DisplayText}";

    partial void OnReplyToChanged(MessageRow? value)
    {
        OnPropertyChanged(nameof(HasReply));
        OnPropertyChanged(nameof(ReplyPreview));
    }

    private Guid Me => session.User?.Id ?? Guid.Empty;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override async Task OnAppearingAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            chat.MessageReceived += OnMessage;
            chat.MessageDeleted += OnDeleted;
            chat.Seen += OnSeen;
            chat.Typing += OnTyping;
            chat.ConversationChanged += OnConversationChanged;
            chat.PresenceChanged += OnPresence;
        }
        await bootstrap.EnsureStartedAsync();
        await RunAsync(async () =>
        {
            Conversation = (await api.GetConversationAsync(_id)).Data;
            UpdateSubtitle();
            var page = (await api.GetMessagesAsync(_id)).Data ?? [];
            Messages.Clear();
            foreach (var m in page) Messages.Add(new MessageRow(m, Me, IsGroup));
            _hasOlder = page.Count >= 40;
            MessageAdded?.Invoke(this, EventArgs.Empty);
            await MarkSeenAsync();
        });
    }

    public override Task OnDisappearingAsync()
    {
        if (_subscribed)
        {
            _subscribed = false;
            chat.MessageReceived -= OnMessage;
            chat.MessageDeleted -= OnDeleted;
            chat.Seen -= OnSeen;
            chat.Typing -= OnTyping;
            chat.ConversationChanged -= OnConversationChanged;
            chat.PresenceChanged -= OnPresence;
        }
        if (Draft.Length > 0) _ = chat.TypingAsync(_id, false);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadOlder()
    {
        if (!_hasOlder || IsLoadingOlder || Messages.Count == 0) return;
        IsLoadingOlder = true;
        try
        {
            var older = (await api.GetMessagesAsync(_id, Messages[0].Id)).Data ?? [];
            _hasOlder = older.Count >= 40;
            for (var i = older.Count - 1; i >= 0; i--) Messages.Insert(0, new MessageRow(older[i], Me, IsGroup));
        }
        catch (Exception ex)
        {
            ErrorMessage = ApiError.From(ex).Message;
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    partial void OnDraftChanged(string value)
    {
        // FR-C05: "typing…" at most every 3 seconds.
        if (value.Length == 0 || DateTimeOffset.UtcNow - _lastTypingSent < TimeSpan.FromSeconds(3)) return;
        _lastTypingSent = DateTimeOffset.UtcNow;
        _ = chat.TypingAsync(_id, true);
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || !CanSend) return;
        var clientId = Guid.NewGuid().ToString("N");
        var reply = ReplyTo;
        var pending = new MessageRow(new MessageDto(0, _id, Me, session.User?.Greeting ?? "", null, text, MessageType.Text,
            reply is null ? null : new ReplyPreviewDto(reply.Id, reply.SenderName, reply.DisplayText, reply.IsDeleted),
            DateTimeOffset.UtcNow, false, false, DeliveryState.Sent, [], [], clientId), Me, IsGroup) { IsPending = true };
        Messages.Add(pending);
        MessageAdded?.Invoke(this, EventArgs.Empty);
        Draft = "";
        ReplyTo = null;
        _ = chat.TypingAsync(_id, false);

        var result = await chat.SendAsync(_id, new SendMessageRequest(text, reply?.Id, clientId));
        if (result.Ok && result.Message is { } sent)
        {
            // The hub may already have delivered it to us; keep one row.
            var existing = Messages.FirstOrDefault(m => m != pending && m.Id == sent.Id);
            if (existing is not null) Messages.Remove(pending);
            else pending.Apply(sent);
            return;
        }

        Messages.Remove(pending);
        Draft = text;
        await notices.HandleAsync(result.Code, result.Error ?? "The message wasn't sent.");
        if (result.Code is "chat_suspended" or "warning_pending" or "privacy_restricted" or "cannot_message" or "announcement_only")
            Conversation = (await api.GetConversationAsync(_id)).Data;
    }

    [RelayCommand]
    private void CancelReply() => ReplyTo = null;

    /// <summary>Tapping a competition card opens the lobby or the result.</summary>
    [RelayCommand]
    private Task OpenCompetition(MessageRow? row) => row?.Dto.CompetitionId is { } id
        ? navigation.GoToAsync(Routes.Match, new Dictionary<string, object> { ["id"] = id })
        : Task.CompletedTask;

    /// <summary>Long-press menu on a message.</summary>
    [RelayCommand]
    private async Task MessageActions(MessageRow? row)
    {
        if (row is null || row.IsSystem || row.IsPending) return;
        if (row.IsCard)
        {
            await OpenCompetition(row);
            return;
        }
        var options = new List<string>();
        if (!row.IsDeleted)
        {
            if (CanSend) options.Add("Reply");
            options.Add("React");
            if (!row.IsMine) options.Add("Translate");
        }
        if (row.IsMine && IsGroup && !row.IsDeleted) options.Add("Seen by");
        options.Add("Delete for me");
        var isAdmin = Conversation?.MyRole is MemberRole.Admin or MemberRole.Owner;
        if (!row.IsDeleted && (row.IsMine || (IsGroup && isAdmin))) options.Add("Delete for everyone");
        if (!row.IsMine && !row.IsDeleted) options.Add("Report");

        var choice = await dialogs.ChooseAsync("Message", null, "Cancel", [.. options]);
        switch (choice)
        {
            case "Reply":
                ReplyTo = row;
                break;
            case "React":
                var emoji = await dialogs.ChooseAsync("React", null, "Cancel", [.. SocialText.Reactions, "Remove my reaction"]);
                if (emoji is null) return;
                await RunAsync(async () =>
                {
                    if (emoji == "Remove my reaction") await api.UnreactAsync(row.Id);
                    else await api.ReactAsync(row.Id, new ReactionRequest(emoji));
                });
                break;
            case "Translate":
                await RunAsync(async () =>
                {
                    var t = (await api.TranslateMessageAsync(row.Id)).Data!;
                    row.Translation = (t.IsSample ? "(sample) " : "") + t.Text;
                    row.HasTranslation = true;
                });
                break;
            case "Seen by":
                await RunAsync(async () =>
                {
                    var seen = (await api.SeenByAsync(row.Id)).Data ?? [];
                    await dialogs.AlertAsync("Seen by", seen.Count == 0 ? "Nobody yet." : string.Join("\n", seen.Select(s => $"{s.DisplayName} · {SocialText.When(s.SeenAt)}")));
                });
                break;
            case "Delete for me":
                await RunAsync(async () =>
                {
                    await api.DeleteMessageAsync(row.Id);
                    Messages.Remove(row);
                });
                break;
            case "Delete for everyone":
                if (!await dialogs.ConfirmAsync("Delete for everyone", "Delete this message for everyone in the chat?", "Delete")) return;
                await RunAsync(async () =>
                {
                    await api.DeleteMessageAsync(row.Id, forEveryone: true);
                    row.MarkDeleted();
                });
                break;
            case "Report":
                await ReportAsync(row.Id, null);
                break;
        }
    }

    private async Task ReportAsync(long? messageId, Guid? userId)
    {
        var reason = await dialogs.ChooseAsync("Report", "Why are you reporting this?", "Cancel", "Abuse or bullying", "Spam", "Cheating", "Something else");
        if (reason is null) return;
        var r = reason switch { "Abuse or bullying" => ReportReason.Abuse, "Spam" => ReportReason.Spam, "Cheating" => ReportReason.Cheating, _ => ReportReason.Other };
        var details = r == ReportReason.Other ? await dialogs.PromptAsync("Report", "Tell us a little more (optional)") : null;
        await RunAsync(async () =>
        {
            await api.ReportAsync(new ReportRequest(userId, messageId, r, details));
            await dialogs.ToastAsync("Thanks — a moderator will look at it.");
        });
    }

    [RelayCommand]
    private Task OpenInfo()
    {
        if (Conversation is null) return Task.CompletedTask;
        return IsGroup
            ? navigation.GoToAsync(Routes.GroupInfo, new Dictionary<string, object> { ["id"] = _id })
            : Conversation.OtherUserId is { } other
                ? navigation.GoToAsync(Routes.UserProfile, new Dictionary<string, object> { ["id"] = other })
                : Task.CompletedTask;
    }

    // ------------------------------------------------------------------ live events

    private void OnMessage(object? sender, MessageDto m)
    {
        if (m.ConversationId != _id) return;
        var row = Messages.FirstOrDefault(x => x.Id == m.Id && m.Id != 0)
                  ?? (m.ClientId is { } cid ? Messages.FirstOrDefault(x => x.ClientId == cid && x.IsPending) : null);
        if (row is not null)
        {
            row.Apply(m);
            return;
        }
        // FR-S22: hide learners I blocked (the server filters history; live messages are filtered here too).
        Messages.Add(new MessageRow(m, Me, IsGroup));
        MessageAdded?.Invoke(this, EventArgs.Empty);
        if (m.SenderId != Me) _ = MarkSeenAsync();
    }

    private void OnDeleted(object? sender, MessageDeletedDto d)
    {
        if (d.ConversationId != _id) return;
        Messages.FirstOrDefault(m => m.Id == d.MessageId)?.MarkDeleted();
    }

    private void OnSeen(object? sender, SeenDto s)
    {
        if (s.ConversationId != _id || s.UserId == Me || IsGroup) return;
        foreach (var m in Messages.Where(m => m.IsMine && m.Id <= s.LastSeenMessageId)) m.SetState(DeliveryState.Seen);
    }

    private void OnTyping(object? sender, TypingDto t)
    {
        if (t.ConversationId != _id || t.UserId == Me) return;
        _typingClear?.Cancel();
        if (!t.IsTyping)
        {
            UpdateSubtitle();
            return;
        }
        var name = Conversation?.Members.FirstOrDefault(x => x.UserId == t.UserId)?.DisplayName ?? t.DisplayName;
        Subtitle = IsGroup ? $"{name} is typing…" : "typing…";
        var cts = _typingClear = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ContinueWith(_ => UpdateSubtitle(), cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnConversationChanged(object? sender, ConversationChangedDto c)
    {
        if (c.ConversationId != _id) return;
        _ = ReloadConversationAsync();
    }

    private void OnPresence(object? sender, PresenceDto p)
    {
        if (Conversation is null || IsGroup || Conversation.OtherUserId != p.UserId) return;
        Subtitle = SocialText.LastSeen(p.IsOnline, p.LastSeenAt);
    }

    private async Task ReloadConversationAsync()
    {
        try
        {
            Conversation = (await api.GetConversationAsync(_id)).Data;
            UpdateSubtitle();
        }
        catch (Exception)
        {
            // removed from the group, etc.
        }
    }

    private void UpdateSubtitle()
    {
        if (Conversation is null)
        {
            Subtitle = "";
            return;
        }
        Subtitle = IsGroup
            ? $"{Conversation.Members.Count(m => !m.IsPending)} members" + (Conversation.SlowModeSec > 0 ? $" · slow mode {Conversation.SlowModeSec}s" : "")
            : SocialText.LastSeen(Conversation.Members.FirstOrDefault(m => m.UserId != Me)?.IsOnline, null);
    }

    private Task MarkSeenAsync()
    {
        var last = Messages.LastOrDefault(m => m.Id > 0);
        return last is null ? Task.CompletedTask : chat.MarkSeenAsync(_id, last.Id);
    }
}

// ===================================================================== people, friends (FR-S20, FR-S21)

public sealed record PersonRow(UserCardDto Dto)
{
    public Guid UserId => Dto.UserId;
    public string Name => Dto.DisplayName;
    public string Initial => SocialText.Initial(Dto.DisplayName);
    public string? PhotoUrl => Dto.PhotoUrl;
    public bool HasPhoto => !string.IsNullOrEmpty(Dto.PhotoUrl);
    public string Detail => Dto.Relation switch
    {
        RelationState.Friends => "Friend" + (Dto.Languages is null ? "" : $" · {Dto.Languages}"),
        RelationState.RequestSent => "Request sent",
        RelationState.RequestReceived => "Wants to be friends",
        RelationState.Blocked => "Blocked",
        _ => Dto.Languages ?? "Learner"
    };
}

public sealed record RequestRow(FriendRequestDto Dto)
{
    public int Id => Dto.Id;
    public string Name => Dto.User.DisplayName;
    public string Initial => SocialText.Initial(Dto.User.DisplayName);
    public bool Incoming => Dto.Incoming;
    public string Detail => Dto.Incoming ? "wants to be your friend" : "request sent";
}

public sealed partial class PeopleViewModel(ISocialApi api, INavigationService navigation, IDialogService dialogs) : BaseViewModel
{
    public ObservableCollection<PersonRow> Results { get; } = [];
    public ObservableCollection<PersonRow> Friends { get; } = [];
    public ObservableCollection<RequestRow> Requests { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private bool hasRequests;
    [ObservableProperty] private bool noFriends;

    public override Task OnAppearingAsync() => RunAsync(LoadFriendsAsync);

    private async Task LoadFriendsAsync()
    {
        var f = (await api.GetFriendsAsync()).Data!;
        Friends.Clear();
        foreach (var x in f.Friends) Friends.Add(new PersonRow(x));
        Requests.Clear();
        foreach (var r in f.Requests) Requests.Add(new RequestRow(r));
        HasRequests = Requests.Count > 0;
        NoFriends = Friends.Count == 0;
    }

    [RelayCommand]
    private Task Search() => RunAsync(async () =>
    {
        Results.Clear();
        IsSearching = Query.Trim().Length >= 2;
        if (!IsSearching) return;
        foreach (var u in (await api.SearchUsersAsync(Query.Trim())).Data ?? []) Results.Add(new PersonRow(u));
        if (Results.Count == 0) ErrorMessage = "Nobody found with that name.";
    });

    [RelayCommand]
    private Task Open(PersonRow? row) =>
        row is null ? Task.CompletedTask : navigation.GoToAsync(Routes.UserProfile, new Dictionary<string, object> { ["id"] = row.UserId });

    [RelayCommand]
    private Task Message(PersonRow? row) => row is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var c = (await api.OpenDirectAsync(new CreateDirectRequest(row.UserId))).Data!;
        await navigation.GoToAsync(Routes.Chat, new Dictionary<string, object> { ["id"] = c.Id });
    });

    [RelayCommand]
    private Task Accept(RequestRow? row) => Respond(row, true);

    [RelayCommand]
    private Task Decline(RequestRow? row) => Respond(row, false);

    private Task Respond(RequestRow? row, bool accept) => row is null ? Task.CompletedTask : RunAsync(async () =>
    {
        if (row.Incoming) await api.RespondFriendRequestAsync(row.Id, new RespondFriendRequest(accept));
        else await api.RemoveFriendAsync(row.Dto.User.UserId);
        if (accept) await dialogs.ToastAsync($"You and {row.Name} are now friends.");
        await LoadFriendsAsync();
    });
}

// ===================================================================== public profile (FR-S20, FR-S22, FR-S23)

public sealed partial class UserProfileViewModel(ISocialApi api, INavigationService navigation, IDialogService dialogs) : BaseViewModel
{
    private Guid _id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Initial), nameof(HasPhoto), nameof(LanguagesText), nameof(PointsText), nameof(StatusText),
        nameof(CanAdd), nameof(CanAccept), nameof(IsFriend), nameof(IsPending), nameof(IsBlockedByMe), nameof(CanMessage), nameof(IsOther),
        nameof(CanBlock), nameof(HasBio))]
    private PublicProfileDto? profile;

    public ObservableCollection<LinguaPath.Shared.Contracts.Progress.BadgeDto> Badges { get; } = [];

    public string Name => Profile?.DisplayName ?? "";
    public string Initial => SocialText.Initial(Profile?.DisplayName);
    public bool HasPhoto => !string.IsNullOrEmpty(Profile?.PhotoUrl);
    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);
    public string LanguagesText => Profile is null || Profile.Languages.Count == 0 ? "No courses yet"
        : string.Join(" · ", Profile.Languages.Select(l => $"{l.Language} {l.Level}"));
    public string PointsText => Profile is null ? "" : $"{PointsViewModel.TierIcon(Profile.Tier)} {Profile.Tier} · {Profile.Points:N0} points";
    public string StatusText => Profile is null ? "" : SocialText.LastSeen(Profile.IsOnline, Profile.LastSeenAt);
    private RelationState Relation => Profile?.Relation ?? RelationState.None;
    public bool IsOther => Relation != RelationState.Self && Profile?.IsBlockedAccount != true;
    public bool CanAdd => IsOther && Relation == RelationState.None;
    public bool CanAccept => IsOther && Relation == RelationState.RequestReceived;
    public bool IsPending => IsOther && Relation == RelationState.RequestSent;
    public bool IsFriend => IsOther && Relation == RelationState.Friends;
    public bool IsBlockedByMe => Relation == RelationState.Blocked;
    public bool CanBlock => IsOther && !IsBlockedByMe;
    public bool CanMessage => IsOther && Profile?.CanMessage == true;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (Guid.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override Task OnAppearingAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Profile = (await api.GetProfileAsync(_id)).Data;
        Badges.Clear();
        foreach (var b in Profile?.Badges ?? []) Badges.Add(b);
    }

    [RelayCommand]
    private Task AddFriend() => RunAsync(async () =>
    {
        await api.SendFriendRequestAsync(new SendFriendRequest(_id));
        await LoadAsync();
    });

    [RelayCommand]
    private Task RemoveFriend() => RunAsync(async () =>
    {
        if (IsFriend && !await dialogs.ConfirmAsync("Remove friend", $"Remove {Name} from your friends?")) return;
        await api.RemoveFriendAsync(_id);
        await LoadAsync();
    });

    [RelayCommand]
    private Task Block() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("Block", $"Block {Name}? They won't be able to message or challenge you, and your friendship ends.", "Block")) return;
        await api.BlockAsync(_id);
        await LoadAsync();
    });

    [RelayCommand]
    private Task Unblock() => RunAsync(async () =>
    {
        await api.UnblockAsync(_id);
        await LoadAsync();
    });

    [RelayCommand]
    private async Task Report()
    {
        var reason = await dialogs.ChooseAsync("Report", $"Why are you reporting {Name}?", "Cancel", "Abuse or bullying", "Spam", "Cheating", "Something else");
        if (reason is null) return;
        var r = reason switch { "Abuse or bullying" => ReportReason.Abuse, "Spam" => ReportReason.Spam, "Cheating" => ReportReason.Cheating, _ => ReportReason.Other };
        var details = await dialogs.PromptAsync("Report", "Anything a moderator should know? (optional)");
        await RunAsync(async () =>
        {
            await api.ReportAsync(new ReportRequest(_id, null, r, details));
            await dialogs.ToastAsync("Thanks — a moderator will look at it.");
        });
    }

    [RelayCommand]
    private Task Message() => RunAsync(async () =>
    {
        var c = (await api.OpenDirectAsync(new CreateDirectRequest(_id))).Data!;
        await navigation.GoToAsync(Routes.Chat, new Dictionary<string, object> { ["id"] = c.Id });
    });

    /// <summary>Phase 9: "who can challenge me" is checked by the server.</summary>
    [RelayCommand]
    private Task Challenge() =>
        navigation.GoToAsync(Routes.ChallengeSetup, new Dictionary<string, object> { ["opponentId"] = _id, ["name"] = Name });
}

// ===================================================================== groups (FR-C20 .. FR-C29)

public sealed partial class SelectablePerson(PersonRow person) : ObservableObject
{
    public PersonRow Person { get; } = person;
    public string Name => Person.Name;
    public string Initial => Person.Initial;
    [ObservableProperty] private bool isSelected;
}

public sealed partial class IconChoice(string key) : ObservableObject
{
    public string Key { get; } = key;
    public string Emoji => SocialText.Icon(Key);
    [ObservableProperty] private bool isSelected;
}

public sealed partial class GroupCreateViewModel(ISocialApi api, INavigationService navigation) : BaseViewModel
{
    public ObservableCollection<SelectablePerson> Friends { get; } = [];
    public ObservableCollection<IconChoice> Icons { get; } = new(SocialText.IconKeys.Select(k => new IconChoice(k) { IsSelected = k == "chat" }));

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private bool isPublic;
    [ObservableProperty] private bool noFriends;

    public override Task OnAppearingAsync() => Friends.Count > 0 ? Task.CompletedTask : RunAsync(async () =>
    {
        var f = (await api.GetFriendsAsync()).Data!;
        foreach (var x in f.Friends) Friends.Add(new SelectablePerson(new PersonRow(x)));
        NoFriends = Friends.Count == 0;
    });

    [RelayCommand]
    private void PickIcon(IconChoice? icon)
    {
        if (icon is null) return;
        foreach (var i in Icons) i.IsSelected = i == icon;
    }

    [RelayCommand]
    private void Toggle(SelectablePerson? p)
    {
        if (p is not null) p.IsSelected = !p.IsSelected;
    }

    [RelayCommand]
    private Task Create()
    {
        if (!Check.All(Errors, ("Name", Name.Trim().Length is < 2 or > 80 ? "Group names are 2 to 80 characters." : null))) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var icon = Icons.FirstOrDefault(i => i.IsSelected)?.Key;
            var members = Friends.Where(f => f.IsSelected).Select(f => f.Person.UserId).ToList();
            var c = (await api.CreateGroupAsync(new CreateGroupRequest(Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                icon, null, IsPublic, members))).Data!;
            await navigation.GoToAsync($"../{Routes.Chat}", new Dictionary<string, object> { ["id"] = c.Id });
        });
    }
}

public sealed record MemberRow(MemberDto Dto, bool IsMe)
{
    public Guid UserId => Dto.UserId;
    public string Name => IsMe ? $"{Dto.DisplayName} (you)" : Dto.DisplayName;
    public string Initial => SocialText.Initial(Dto.DisplayName);
    public string RoleText => Dto.IsPending ? "wants to join" : Dto.Role switch { MemberRole.Owner => "Owner", MemberRole.Admin => "Admin", _ => "" };
    public bool IsOnline => Dto.IsOnline == true;
}

public sealed partial class GroupInfoViewModel(ISocialApi api, ICompetitionApi competitions, IAuthSession session, INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _id;

    public ObservableCollection<MemberRow> Members { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Description), nameof(Icon), nameof(IsAdmin), nameof(IsOwner), nameof(MuteText), nameof(AnnouncementText),
        nameof(ApprovalText), nameof(PublicText), nameof(SlowModeText), nameof(MembersTitle))]
    private ConversationDto? conversation;

    public string Name => Conversation?.Title ?? "";
    public string? Description => Conversation?.Description;
    public string Icon => SocialText.Icon(Conversation?.IconKey);
    public bool IsAdmin => Conversation?.MyRole is MemberRole.Admin or MemberRole.Owner;
    public bool IsOwner => Conversation?.MyRole == MemberRole.Owner;
    public string MuteText => Conversation?.IsMuted == true ? "🔔 Unmute" : "🔕 Mute";
    public string AnnouncementText => Conversation?.OnlyAdminsCanSend == true ? "Only admins can send: ON" : "Only admins can send: OFF";
    public string ApprovalText => Conversation?.JoinApproval == true ? "Approve new members: ON" : "Approve new members: OFF";
    public string PublicText => Conversation?.IsPublic == true ? "Public (anyone can find it)" : "Private (invite only)";
    public string SlowModeText => Conversation?.SlowModeSec is > 0 and var s ? $"Slow mode: {s} s" : "Slow mode: off";
    public string MembersTitle => $"{Members.Count(m => !m.Dto.IsPending)} members";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override Task OnAppearingAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Conversation = (await api.GetConversationAsync(_id)).Data;
        Members.Clear();
        var me = session.User?.Id;
        foreach (var m in Conversation?.Members.OrderBy(m => m.IsPending ? 0 : 1).ThenByDescending(m => m.Role) ?? Enumerable.Empty<MemberDto>())
            Members.Add(new MemberRow(m, m.UserId == me));
        OnPropertyChanged(nameof(MembersTitle));
    }

    private Task Patch(UpdateConversationRequest request) => RunAsync(async () =>
    {
        Conversation = (await api.UpdateConversationAsync(_id, request)).Data;
        await LoadAsync();
    });

    private static UpdateConversationRequest Empty => new(null, null, null, null, null, null, null, null, null, null);

    [RelayCommand]
    private Task ToggleMute() => Patch(Empty with { Muted = !(Conversation?.IsMuted ?? false) });

    [RelayCommand]
    private async Task Rename()
    {
        var name = await dialogs.PromptAsync("Group name", "New name", placeholder: Name);
        if (!string.IsNullOrWhiteSpace(name)) await Patch(Empty with { Name = name.Trim() });
    }

    [RelayCommand]
    private async Task EditDescription()
    {
        var text = await dialogs.PromptAsync("Description", "What is this group about?", placeholder: Description);
        if (text is not null) await Patch(Empty with { Description = text.Trim() });
    }

    [RelayCommand]
    private Task ToggleAnnouncement() => Patch(Empty with { OnlyAdminsCanSend = !(Conversation?.OnlyAdminsCanSend ?? false) });

    [RelayCommand]
    private Task ToggleApproval() => Patch(Empty with { JoinApproval = !(Conversation?.JoinApproval ?? false) });

    [RelayCommand]
    private Task TogglePublic() => Patch(Empty with { IsPublic = !(Conversation?.IsPublic ?? false) });

    [RelayCommand]
    private async Task SlowMode()
    {
        var choice = await dialogs.ChooseAsync("Slow mode", "How long members wait between messages", "Cancel", "Off", "10 seconds", "30 seconds", "1 minute", "5 minutes");
        int? sec = choice switch { "Off" => 0, "10 seconds" => 10, "30 seconds" => 30, "1 minute" => 60, "5 minutes" => 300, _ => null };
        if (sec is { } s) await Patch(Empty with { SlowModeSec = s });
    }

    [RelayCommand]
    private async Task AddMember()
    {
        List<UserCardDto> friends;
        try
        {
            friends = (await api.GetFriendsAsync()).Data!.Friends.Where(f => Members.All(m => m.UserId != f.UserId)).ToList();
        }
        catch (Exception ex)
        {
            ErrorMessage = ApiError.From(ex).Message;
            return;
        }
        if (friends.Count == 0)
        {
            await dialogs.AlertAsync("Add members", "All your friends are already here. Add more friends from People.");
            return;
        }
        var choice = await dialogs.ChooseAsync("Add a friend", null, "Cancel", [.. friends.Select(f => f.DisplayName)]);
        var picked = friends.FirstOrDefault(f => f.DisplayName == choice);
        if (picked is null) return;
        await RunAsync(async () =>
        {
            await api.AddMembersAsync(_id, new AddMembersRequest([picked.UserId]));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task MemberActions(MemberRow? row)
    {
        if (row is null || row.IsMe) return;
        var options = new List<string> { "View profile" };
        if (row.Dto.IsPending && IsAdmin) options.AddRange(["Approve", "Decline"]);
        else if (IsOwner)
        {
            options.Add(row.Dto.Role == MemberRole.Admin ? "Remove admin" : "Make admin");
            options.Add("Make owner");
            options.Add("Remove from group");
        }
        else if (IsAdmin && row.Dto.Role == MemberRole.Member) options.Add("Remove from group");

        var choice = await dialogs.ChooseAsync(row.Dto.DisplayName, null, "Cancel", [.. options]);
        if (choice is null) return;
        if (choice == "View profile")
        {
            await navigation.GoToAsync(Routes.UserProfile, new Dictionary<string, object> { ["id"] = row.UserId });
            return;
        }
        if (choice == "Make owner" && !await dialogs.ConfirmAsync("Make owner", $"Hand the group over to {row.Dto.DisplayName}? You'll become an admin.")) return;
        await RunAsync(async () =>
        {
            switch (choice)
            {
                case "Approve": await api.ApproveMemberAsync(_id, row.UserId, true); break;
                case "Decline": await api.ApproveMemberAsync(_id, row.UserId, false); break;
                case "Make admin": await api.ChangeRoleAsync(_id, row.UserId, new ChangeRoleRequest(MemberRole.Admin)); break;
                case "Remove admin": await api.ChangeRoleAsync(_id, row.UserId, new ChangeRoleRequest(MemberRole.Member)); break;
                case "Make owner": await api.ChangeRoleAsync(_id, row.UserId, new ChangeRoleRequest(MemberRole.Owner)); break;
                case "Remove from group": await api.RemoveMemberAsync(_id, row.UserId); break;
            }
            await LoadAsync();
        });
    }

    /// <summary>FR-C28: group admins start a battle; a card is posted in the chat for members to join.</summary>
    [RelayCommand]
    private async Task StartBattle()
    {
        var count = await dialogs.ChooseAsync("Group battle", "How many questions?", "Cancel", "5 questions", "10 questions", "15 questions");
        int? n = count switch { "5 questions" => 5, "10 questions" => 10, "15 questions" => 15, _ => null };
        if (n is null) return;
        var wait = await dialogs.ChooseAsync("Group battle", "When does it start?", "Cancel", "In 1 minute", "In 2 minutes", "In 5 minutes");
        int? sec = wait switch { "In 1 minute" => 60, "In 2 minutes" => 120, "In 5 minutes" => 300, _ => null };
        if (sec is null) return;
        await RunAsync(async () =>
        {
            var c = (await competitions.GroupBattleAsync(_id, new LinguaPath.Shared.Contracts.Competitions.GroupBattleRequest(null, null, null, n.Value, 15, sec.Value))).Data!;
            await navigation.GoToAsync(Routes.Match, new Dictionary<string, object> { ["id"] = c.Summary.Id });
        });
    }

    [RelayCommand]
    private async Task Leave()
    {
        var warning = IsOwner ? " The longest-serving admin becomes the owner." : "";
        if (!await dialogs.ConfirmAsync("Leave group", "Leave this group?" + warning, "Leave")) return;
        await RunAsync(async () =>
        {
            await api.LeaveAsync(_id);
            await navigation.GoToAsync($"../..");
        });
    }
}

public sealed record PublicGroupRow(PublicGroupDto Dto)
{
    public int Id => Dto.Id;
    public string Name => Dto.Name;
    public string Icon => SocialText.Icon(Dto.IconKey);
    public string Detail => $"{Dto.Members} members" + (Dto.LanguageName is null ? "" : $" · {Dto.LanguageName}") + (Dto.JoinApproval ? " · approval needed" : "");
    public string? Description => Dto.Description;
    public string ActionText => Dto.IsMember ? "Open" : Dto.JoinApproval ? "Ask to join" : "Join";
}

public sealed partial class PublicGroupsViewModel(ISocialApi api, INavigationService navigation, IDialogService dialogs) : BaseViewModel
{
    public ObservableCollection<PublicGroupRow> Groups { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty] private bool isEmpty;

    public override Task OnAppearingAsync() => Search();

    [RelayCommand]
    private Task Search() => RunAsync(async () =>
    {
        var rows = (await api.PublicGroupsAsync(string.IsNullOrWhiteSpace(Query) ? null : Query.Trim())).Data ?? [];
        Groups.Clear();
        foreach (var g in rows) Groups.Add(new PublicGroupRow(g));
        IsEmpty = Groups.Count == 0;
    });

    [RelayCommand]
    private Task Join(PublicGroupRow? row) => row is null ? Task.CompletedTask : RunAsync(async () =>
    {
        if (!row.Dto.IsMember) await api.JoinAsync(row.Id);
        await navigation.GoToAsync(Routes.Chat, new Dictionary<string, object> { ["id"] = row.Id });
    }, async error =>
    {
        if (error.Code != "join_pending") return false;
        await dialogs.ToastAsync(error.Message);
        return true;
    });
}

// ===================================================================== privacy and blocked list (FR-S22, FR-S24)

public sealed partial class PrivacyViewModel(ISocialApi api, IDialogService dialogs) : BaseViewModel
{
    public IReadOnlyList<string> AudienceOptions { get; } = ["Everyone", "Friends only", "Nobody"];
    public ObservableCollection<BlockedUserDto> Blocked { get; } = [];

    [ObservableProperty] private int whoCanMessage;
    [ObservableProperty] private int whoCanChallenge;
    [ObservableProperty] private bool showOnLeaderboard = true;
    [ObservableProperty] private bool showOnline = true;
    [ObservableProperty] private bool readReceipts = true;
    [ObservableProperty] private bool noBlocked;

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var p = (await api.GetPrivacyAsync()).Data!;
        WhoCanMessage = (int)p.WhoCanMessage;
        WhoCanChallenge = (int)p.WhoCanChallenge;
        ShowOnLeaderboard = p.ShowOnLeaderboard;
        ShowOnline = p.ShowOnline;
        ReadReceipts = p.ReadReceipts;
        await LoadBlockedAsync();
    });

    private async Task LoadBlockedAsync()
    {
        Blocked.Clear();
        foreach (var b in (await api.GetBlockedAsync()).Data ?? []) Blocked.Add(b);
        NoBlocked = Blocked.Count == 0;
    }

    [RelayCommand]
    private Task Save() => RunAsync(async () =>
    {
        await api.UpdatePrivacyAsync(new PrivacyDto((AudienceSetting)Math.Clamp(WhoCanMessage, 0, 2), (AudienceSetting)Math.Clamp(WhoCanChallenge, 0, 2),
            ShowOnLeaderboard, ShowOnline, ReadReceipts));
        await dialogs.ToastAsync("Privacy settings saved.");
    });

    [RelayCommand]
    private Task Unblock(BlockedUserDto? user) => user is null ? Task.CompletedTask : RunAsync(async () =>
    {
        await api.UnblockAsync(user.UserId);
        await LoadBlockedAsync();
    });
}
