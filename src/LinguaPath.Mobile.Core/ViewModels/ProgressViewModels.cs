using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Certificates;
using LinguaPath.Shared.Contracts.Progress;

namespace LinguaPath.Mobile.Core.ViewModels;

// ===================================================================== points, streak, badges (FR-S01 .. FR-S09, FR-L111)

public sealed record HistoryRow(PointTransactionDto Dto)
{
    public string Title => Dto.Description;
    public string Detail => $"{Dto.CreatedAt.ToLocalTime():d MMM, HH:mm}" + (Dto.LanguageName is null ? "" : $" · {Dto.LanguageName}");
    public string PointsText => (Dto.Points > 0 ? "+" : "") + Dto.Points.ToString("N0");
    public bool IsNegative => Dto.Points < 0 || Dto.IsRevoked;
    public bool IsRevoked => Dto.IsRevoked;
}

public sealed record LanguageRow(string Name, string Points);

public sealed partial class PointsViewModel(IProgressApi api, INavigationService navigation) : BaseViewModel
{
    private int _page = 1;
    private bool _hasMore = true;
    private bool _loadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText), nameof(TierText), nameof(NextTierText), nameof(TierProgress), nameof(StreakText),
        nameof(StreakHint), nameof(WeekText), nameof(CapText))]
    private PointsSummaryDto? summary;

    [ObservableProperty] private bool showOnLeaderboard = true;

    public ObservableCollection<BadgeDto> Badges { get; } = [];
    public ObservableCollection<LanguageRow> Languages { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    public string TotalText => Summary?.Total.ToString("N0") ?? "0";
    public string TierText => Summary is null ? "" : $"{TierIcon(Summary.Tier)} {Summary.Tier}";
    public string NextTierText => Summary?.NextTier is { } next ? $"{Summary.PointsToNextTier:N0} points to {next}" : "Top tier reached 💎";
    public double TierProgress => (Summary?.TierProgressPercent ?? 0) / 100.0;
    public string StreakText => $"🔥 {Summary?.Streak.Current ?? 0}";
    public string StreakHint => Summary?.Streak switch
    {
        null => "",
        { AtRisk: true } => "Study today to keep your streak!",
        { ActiveToday: true } => $"Done for today · best {Summary.Streak.Longest}",
        _ => $"Best {Summary.Streak.Longest} days"
    };
    public string WeekText => Summary is null ? "" : $"{Summary.WeeklyPoints:N0} this week · {Summary.MonthlyPoints:N0} this month";
    public string CapText => Summary is null ? "" : $"Today {Summary.TodayCapped} of {Summary.DailyCap} (exams and certificates don't count)";

    public static string TierIcon(PointTier tier) => tier switch
    {
        PointTier.Silver => "🥈", PointTier.Gold => "🥇", PointTier.Platinum => "💠", PointTier.Diamond => "💎", _ => "🥉"
    };

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var s = (await api.GetPointsAsync()).Data!;
        Summary = s;
        ShowOnLeaderboardSilently(s.ShowOnLeaderboard);
        Badges.Clear();
        foreach (var b in s.Badges.OrderByDescending(b => b.Earned)) Badges.Add(b);
        Languages.Clear();
        foreach (var l in s.ByLanguage) Languages.Add(new LanguageRow(l.Name, l.Points.ToString("N0")));

        _page = 1;
        var rows = (await api.GetHistoryAsync(1)).Data ?? [];
        History.Clear();
        foreach (var r in rows) History.Add(new HistoryRow(r));
        _hasMore = rows.Count == 50;
    });

    [RelayCommand]
    private async Task LoadMore()
    {
        if (!_hasMore || _loadingMore || IsBusy) return;
        _loadingMore = true;
        try
        {
            var rows = (await api.GetHistoryAsync(_page + 1)).Data ?? [];
            _page++;
            foreach (var r in rows) History.Add(new HistoryRow(r));
            _hasMore = rows.Count == 50;
        }
        catch (Exception)
        {
            _hasMore = false;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private bool _settingVisibility;

    private void ShowOnLeaderboardSilently(bool value)
    {
        _settingVisibility = true;
        ShowOnLeaderboard = value;
        _settingVisibility = false;
    }

    partial void OnShowOnLeaderboardChanged(bool value)
    {
        if (_settingVisibility) return;
        _ = RunAsync(() => api.SetLeaderboardVisibilityAsync(new LeaderboardVisibilityRequest(value)));
    }

    [RelayCommand]
    private Task OpenLeaderboard() => navigation.GoToAsync(Routes.Leaderboard);
}

// ===================================================================== leaderboards (FR-S07)

public sealed record LeaderRow(LeaderboardEntryDto Dto)
{
    public string Rank => Dto.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{Dto.Rank}" };
    public string Name => Dto.IsMe ? $"{Dto.DisplayName} (you)" : Dto.DisplayName;
    public string Points => Dto.Points.ToString("N0");
    public string Tier => PointsViewModel.TierIcon(Dto.Tier);
    public bool IsMe => Dto.IsMe;
    public string Initial => string.IsNullOrEmpty(Dto.DisplayName) ? "?" : Dto.DisplayName[..1].ToUpperInvariant();
}

public sealed record ScopeOption(int? LanguageId, string Name, bool Friends = false)
{
    public override string ToString() => Name;
}

public sealed partial class LeaderboardViewModel(IProgressApi api, ILearningApi learning) : BaseViewModel
{
    public ObservableCollection<LeaderRow> Entries { get; } = [];
    public ObservableCollection<ScopeOption> Scopes { get; } = [new(null, "All languages"), new(null, "Friends", true)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekly), nameof(IsMonthly), nameof(IsAllTime))]
    private LeaderboardPeriod period = LeaderboardPeriod.Weekly;

    [ObservableProperty] private ScopeOption? scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMe), nameof(MeText))]
    private LeaderRow? me;

    [ObservableProperty] private string? sinceText;
    [ObservableProperty] private bool isEmpty;

    public bool IsWeekly => Period == LeaderboardPeriod.Weekly;
    public bool IsMonthly => Period == LeaderboardPeriod.Monthly;
    public bool IsAllTime => Period == LeaderboardPeriod.AllTime;
    public bool HasMe => Me is not null && !Entries.Any(e => e.IsMe);
    public string MeText => Me is null ? "" : $"You: {Me.Rank} · {Me.Points} points";

    private bool _ready;

    public override async Task OnAppearingAsync()
    {
        if (!_ready)
        {
            try
            {
                var courses = (await learning.GetMyCoursesAsync()).Data ?? [];
                foreach (var c in courses.GroupBy(c => c.LanguageId).Select(g => g.First()))
                    Scopes.Add(new ScopeOption(c.LanguageId, c.LanguageName));
            }
            catch (Exception)
            {
                // "All languages" still works.
            }
            _ready = true;
            Scope = Scopes[0];
            return; // OnScopeChanged loads
        }
        await LoadAsync();
    }

    partial void OnScopeChanged(ScopeOption? value)
    {
        if (_ready) _ = LoadAsync();
    }

    [RelayCommand]
    private Task SetPeriod(string value)
    {
        if (!Enum.TryParse<LeaderboardPeriod>(value, true, out var p)) return Task.CompletedTask;
        Period = p;
        return LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var board = (await api.GetLeaderboardAsync(Period, Scope?.LanguageId, Scope?.Friends == true ? "friends" : null)).Data!;
        Entries.Clear();
        foreach (var e in board.Entries) Entries.Add(new LeaderRow(e));
        Me = board.Me is null ? null : new LeaderRow(board.Me);
        IsEmpty = Entries.Count == 0;
        SinceText = Period switch
        {
            LeaderboardPeriod.Weekly => "This week · resets Monday 00:00 UTC",
            LeaderboardPeriod.Monthly => "This month",
            _ => "All time"
        };
        OnPropertyChanged(nameof(HasMe));
    });
}

// ===================================================================== my certificates (FR-L80 .. FR-L83)

public sealed record CertificateRow(CertificateDto Dto)
{
    public string Title => $"{Dto.LanguageName} · {Dto.Level}";
    public string Course => Dto.CourseTitle;
    public string Detail => $"{Dto.FinalGrade}% · {Dto.Band} · {Dto.CompletedOn:d MMM yyyy}";
    public string Number => Dto.Number;
    public bool IsRevoked => Dto.IsRevoked;
    public bool IsValid => !Dto.IsRevoked;
}

public sealed record ProgressRow(CertificateProgressDto Dto)
{
    public string Course => Dto.CourseTitle;
    public string Summary => Dto.Eligible ? "Ready: your certificate is being issued" :
        $"Lectures {Dto.CoreLecturesDone}/{Dto.CoreLecturesTotal} · exams {Dto.ExamsPassed}/{Dto.ExamsTotal} · grade " +
        (Dto.CourseGrade is { } g ? $"{g}%" : "—") + $" (needs {Dto.MinGrade}%)";
    public double Fraction
    {
        get
        {
            var total = Dto.CoreLecturesTotal + Dto.ExamsTotal;
            return total == 0 ? 0 : (double)(Dto.CoreLecturesDone + Dto.ExamsPassed) / total;
        }
    }
    public IReadOnlyList<string> Items => Dto.Checklist.Select(c => $"{(c.Done ? "✅" : "⬜")} {c.Text}" + (c.Detail is null ? "" : $" · {c.Detail}")).ToList();
}

public sealed partial class CertificatesViewModel(
    IProgressApi api,
    ILearningApi learning,
    IMediaService media,
    IDialogService dialogs) : BaseViewModel
{
    public ObservableCollection<CertificateRow> Certificates { get; } = [];
    public ObservableCollection<ProgressRow> InProgress { get; } = [];

    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool hasInProgress;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var list = (await api.GetCertificatesAsync()).Data ?? [];
        Certificates.Clear();
        foreach (var c in list) Certificates.Add(new CertificateRow(c));

        // Courses still on their way to a certificate (BR-06 checklist).
        InProgress.Clear();
        var courses = (await learning.GetMyCoursesAsync()).Data ?? [];
        foreach (var c in courses.Where(c => c.Status is LearnerCourseStatus.Active or LearnerCourseStatus.Paused)
                     .Where(c => list.All(x => x.LearnerCourseId != c.Id)))
        {
            var progress = (await api.GetCertificateProgressAsync(c.Id)).Data;
            if (progress is not null) InProgress.Add(new ProgressRow(progress));
        }
        HasInProgress = InProgress.Count > 0;
        IsEmpty = Certificates.Count == 0 && InProgress.Count == 0;
    });

    [RelayCommand]
    private Task Download(CertificateRow row) => RunAsync(async () =>
    {
        using var response = await api.DownloadCertificateAsync(row.Dto.Id);
        if (!response.IsSuccessStatusCode)
        {
            await dialogs.AlertAsync("Certificate", row.IsRevoked ? "This certificate has been revoked." : "The PDF couldn't be downloaded. Try again.");
            return;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await media.OpenFileAsync($"{row.Number}.pdf", bytes, "application/pdf");
    });

    [RelayCommand]
    private Task Share(CertificateRow row) => media.ShareLinkAsync($"My {row.Title} certificate", row.Dto.VerifyUrl);

    [RelayCommand]
    private Task Verify(CertificateRow row) => media.OpenUrlAsync(row.Dto.VerifyUrl);
}
