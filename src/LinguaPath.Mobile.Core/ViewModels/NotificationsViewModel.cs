using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Notifications;
using static LinguaPath.Mobile.Core.Localization.Localizer;

namespace LinguaPath.Mobile.Core.ViewModels;

/// <summary>One inbox row.</summary>
public sealed partial class NotificationItem(NotificationDto dto) : ObservableObject
{
    public NotificationDto Dto { get; } = dto;
    public long Id => Dto.Id;
    public string Title => Dto.Title;
    public string Body => Dto.Body;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    private bool isRead = dto.IsRead;

    public bool IsUnread => !IsRead;

    public string Icon => Dto.Type switch
    {
        NotificationType.StudyReminder => "📚",
        NotificationType.DailyQuizReady => "✅",
        NotificationType.ExamSoon => "📝",
        NotificationType.StreakAtRisk => "🔥",
        NotificationType.Certificate => "🎓",
        NotificationType.NewPost => "💡",
        _ => "🔔"
    };

    public string When => Ago(Dto.CreatedAt, DateTimeOffset.UtcNow);

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "·";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays}d";
        return at.ToLocalTime().ToString("d MMM", Culture);
    }
}

/// <summary>FR-L112 in-app inbox: newest first, tap to open, mark read, delete.</summary>
public sealed partial class NotificationsViewModel(INotificationsApi api, INavigationService navigation) : BaseViewModel
{
    public ObservableCollection<NotificationItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText), nameof(HasUnread))]
    private int unreadCount;

    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool isLoaded;

    public bool IsEmpty => IsLoaded && Items.Count == 0;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 0 ? T("Inbox_UnreadFormat", UnreadCount) : "";

    public override Task OnAppearingAsync() => LoadAsync(reset: true);

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync(reset: true);
        IsRefreshing = false;
    }

    [RelayCommand]
    private Task LoadMore() => HasMore ? LoadAsync(reset: false) : Task.CompletedTask;

    private Task LoadAsync(bool reset) => RunAsync(async () =>
    {
        var before = reset || Items.Count == 0 ? (long?)null : Items[^1].Id;
        var page = (await api.ListAsync(before)).Data!;
        if (reset) Items.Clear();
        foreach (var n in page.Items) Items.Add(new NotificationItem(n));
        UnreadCount = page.UnreadCount;
        HasMore = page.HasMore;
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    });

    [RelayCommand]
    private async Task Open(NotificationItem? item)
    {
        if (item is null) return;
        if (!item.IsRead)
        {
            item.IsRead = true;
            UnreadCount = Math.Max(0, UnreadCount - 1);
            try { await api.MarkReadAsync(item.Id); } catch { /* the badge corrects itself on the next load */ }
        }
        if (NotificationRouter.Resolve(item.Dto.Data) is { } target)
            await navigation.GoToAsync(target.Route, target.Parameters);
    }

    [RelayCommand]
    private Task MarkAllRead() => RunAsync(async () =>
    {
        await api.MarkAllReadAsync();
        foreach (var i in Items) i.IsRead = true;
        UnreadCount = 0;
    });

    [RelayCommand]
    private Task Delete(NotificationItem? item) => item is null ? Task.CompletedTask : RunAsync(async () =>
    {
        await api.DeleteAsync(item.Id);
        Items.Remove(item);
        if (!item.IsRead) UnreadCount = Math.Max(0, UnreadCount - 1);
        OnPropertyChanged(nameof(IsEmpty));
    });
}
