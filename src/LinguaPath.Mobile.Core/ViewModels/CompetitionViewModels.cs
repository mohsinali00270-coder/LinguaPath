using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Competitions;
using LinguaPath.Shared.Contracts.Learning;

namespace LinguaPath.Mobile.Core.ViewModels;

internal static class CompeteText
{
    public static string Mode(CompetitionMode m) => m switch
    {
        CompetitionMode.Challenge => "⚔️ Challenge",
        CompetitionMode.QuickMatch => "⚡ Quick match",
        CompetitionMode.GroupBattle => "👥 Group battle",
        _ => "🏆 Tournament"
    };

    public static string Result(CompetitionResult? r) => r switch
    {
        CompetitionResult.Win => "Won",
        CompetitionResult.Draw => "Draw",
        CompetitionResult.Loss => "Lost",
        _ => ""
    };

    public static string Status(CompetitionSummaryDto s) => s.Status switch
    {
        CompetitionStatus.Pending when s.NeedsMyAction && s.IsLive => "Waiting for you to accept",
        CompetitionStatus.Pending when s.NeedsMyAction => "Your turn to play",
        CompetitionStatus.Pending => "Waiting for them to accept",
        CompetitionStatus.Waiting when s.Mode == CompetitionMode.QuickMatch => "Searching…",
        CompetitionStatus.Waiting => "Lobby open",
        CompetitionStatus.Scheduled => "Starting…",
        CompetitionStatus.Running when s.NeedsMyAction => "Your turn to play",
        CompetitionStatus.Running => "In progress",
        CompetitionStatus.Finished => $"{Result(s.MyResult)} · {s.MyScore:N0} pts" + (s.PointsAwarded > 0 ? $" · +{s.PointsAwarded}" : ""),
        _ => s.Status.ToString()
    };
}

public sealed record CompetitionRow(CompetitionSummaryDto Dto)
{
    public int Id => Dto.Id;
    public string Mode => CompeteText.Mode(Dto.Mode);
    public string Title => Dto.Title;
    public string Detail => $"{Dto.LanguageName} {Dto.Level} · {Dto.QuestionCount} questions" + (Dto.IsLive ? "" : " · own time");
    public string Status => CompeteText.Status(Dto);
    public bool NeedsAction => Dto.NeedsMyAction;
    public string When => SocialText.When(Dto.StartsAt ?? Dto.CreatedAt);
}

public sealed record LanguageChoice(int LanguageId, string Name, CefrLevel Level)
{
    public string Label => $"{Name} ({Level})";
}

// ===================================================================== hub screen

public sealed partial class CompeteViewModel(
    ICompetitionApi api,
    ILearningApi learning,
    ICompetitionConnection live,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private bool _subscribed;

    public ObservableCollection<CompetitionRow> Active { get; } = [];
    public ObservableCollection<CompetitionRow> History { get; } = [];
    public ObservableCollection<LanguageChoice> Languages { get; } = [];

    [ObservableProperty] private LanguageChoice? language;
    [ObservableProperty] private string statsText = "";
    [ObservableProperty] private string limitText = "";
    [ObservableProperty] private bool hasActive;
    [ObservableProperty] private bool noHistory;
    [ObservableProperty] private bool noCourse;

    public override async Task OnAppearingAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            live.ChallengeReceived += OnChallenge;
            live.MatchUpdated += OnUpdated;
        }
        await live.StartAsync();
        await RunAsync(LoadAsync);
    }

    public override Task OnDisappearingAsync()
    {
        if (_subscribed)
        {
            _subscribed = false;
            live.ChallengeReceived -= OnChallenge;
            live.MatchUpdated -= OnUpdated;
        }
        return Task.CompletedTask;
    }

    private void OnChallenge(object? sender, CompetitionSummaryDto e) => _ = RunAsync(LoadAsync);
    private void OnUpdated(object? sender, int e) => _ = RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        if (Languages.Count == 0)
        {
            var courses = (await learning.GetMyCoursesAsync()).Data ?? [];
            foreach (var c in courses.Where(c => c.Status != LearnerCourseStatus.Cancelled).GroupBy(c => c.LanguageId))
                Languages.Add(new LanguageChoice(c.Key, c.First().LanguageName, c.Max(x => x.CurrentLevel)));
            Language ??= Languages.FirstOrDefault();
            NoCourse = Languages.Count == 0;
        }
        var active = (await api.ActiveAsync()).Data ?? [];
        Active.Clear();
        foreach (var a in active.OrderByDescending(a => a.NeedsMyAction)) Active.Add(new CompetitionRow(a));
        HasActive = Active.Count > 0;

        var history = (await api.HistoryAsync()).Data ?? [];
        History.Clear();
        foreach (var h in history.Take(10)) History.Add(new CompetitionRow(h));
        NoHistory = History.Count == 0;

        var stats = (await api.StatsAsync()).Data!;
        var rating = stats.Ratings.FirstOrDefault(r => r.LanguageId == Language?.LanguageId);
        StatsText = $"Played {stats.Played} · won {stats.Won} · {stats.WinRatePercent}% · best streak {stats.BestStreak}"
                    + (rating is null ? "" : $" · {rating.LanguageName} rating {rating.Rating}");
        LimitText = $"Ranked matches today: {stats.RankedToday} of {stats.RankedPerDay}";
    }

    [RelayCommand]
    private Task QuickMatch() => Language is null ? NeedCourseAsync() : RunAsync(async () =>
    {
        var c = (await api.QuickMatchAsync(new QuickMatchRequest(Language.LanguageId, Language.Level))).Data!;
        await navigation.GoToAsync(Routes.Match, new Dictionary<string, object> { ["id"] = c.Summary.Id });
    });

    [RelayCommand]
    private Task Challenge() => navigation.GoToAsync(Routes.ChallengeSetup);

    [RelayCommand]
    private Task Tournaments() => navigation.GoToAsync(Routes.Tournaments);

    [RelayCommand]
    private Task League() => navigation.GoToAsync(Routes.League);

    [RelayCommand]
    private Task Open(CompetitionRow? row) => row is null ? Task.CompletedTask
        : navigation.GoToAsync(row.Dto.Status == CompetitionStatus.Finished ? Routes.MatchResult : Routes.Match,
            new Dictionary<string, object> { ["id"] = row.Id });

    private Task NeedCourseAsync() => dialogs.AlertAsync("Compete", "Start a course first: matches use the language you're learning.");
}

// ===================================================================== new challenge (FR-Q01)

public sealed partial class ChallengeSetupViewModel(
    ICompetitionApi api,
    ILearningApi learning,
    ISocialApi social,
    INavigationService navigation) : BaseViewModel
{
    private Guid? _opponentId;

    public ObservableCollection<LanguageChoice> Languages { get; } = [];
    public ObservableCollection<PersonRow> Friends { get; } = [];
    public IReadOnlyList<int> QuestionCounts { get; } = [5, 10, 15];
    public IReadOnlyList<int> SecondOptions { get; } = [10, 15, 20];

    [ObservableProperty] private LanguageChoice? language;
    [ObservableProperty] private PersonRow? opponent;
    [ObservableProperty] private int questionCount = 10;
    [ObservableProperty] private int seconds = 15;
    [ObservableProperty] private bool live = true;
    [ObservableProperty] private string opponentName = "";
    [ObservableProperty] private bool pickOpponent = true;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (Guid.TryParse(Param(parameters, "opponentId"), out var id)) _opponentId = id;
        OpponentName = Param(parameters, "name") ?? "";
    }

    public override Task OnAppearingAsync() => Languages.Count > 0 ? Task.CompletedTask : RunAsync(async () =>
    {
        foreach (var c in ((await learning.GetMyCoursesAsync()).Data ?? []).Where(c => c.Status != LearnerCourseStatus.Cancelled).GroupBy(c => c.LanguageId))
            Languages.Add(new LanguageChoice(c.Key, c.First().LanguageName, c.Max(x => x.CurrentLevel)));
        Language = Languages.FirstOrDefault();
        PickOpponent = _opponentId is null;
        if (PickOpponent)
        {
            foreach (var f in (await social.GetFriendsAsync()).Data!.Friends) Friends.Add(new PersonRow(f));
            Opponent = Friends.FirstOrDefault();
        }
    });

    [RelayCommand]
    private Task Send()
    {
        var opponentId = _opponentId ?? Opponent?.UserId;
        if (opponentId is null)
        {
            ErrorMessage = "Choose who to challenge (add friends in People).";
            return Task.CompletedTask;
        }
        if (Language is null)
        {
            ErrorMessage = "Start a course first.";
            return Task.CompletedTask;
        }
        return RunAsync(async () =>
        {
            var c = (await api.ChallengeAsync(new ChallengeRequest(opponentId.Value, Language.LanguageId, Language.Level, null, QuestionCount, Seconds, Live))).Data!;
            await navigation.GoToAsync($"../{Routes.Match}", new Dictionary<string, object> { ["id"] = c.Summary.Id });
        });
    }
}

// ===================================================================== the match (FR-Q03 .. FR-Q06)

public sealed partial class OptionRow(CompetitionOptionDto dto) : ObservableObject
{
    public int Id => dto.Id;
    public string Text => dto.Text;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isRight;
    [ObservableProperty] private bool isWrong;
}

public sealed partial class PlayerRow(PlayerDto dto) : ObservableObject
{
    public PlayerDto Dto { get; } = dto;
    public string Name => Dto.IsMe ? $"{Dto.DisplayName} (you)" : Dto.DisplayName;
    public string Initial => SocialText.Initial(Dto.DisplayName);
    [ObservableProperty] private int score = dto.Score;
    [ObservableProperty] private int answered = dto.Answered;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string rankText = dto.Rank is { } r ? $"#{r}" : "";
}

public sealed partial class MatchViewModel(
    ICompetitionApi api,
    ICompetitionConnection live,
    ISpeechService speech,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _id;
    private bool _subscribed;
    private TimeSpan _offset;
    private CancellationTokenSource? _loop;
    private DateTimeOffset _nextPoll;

    public ObservableCollection<PlayerRow> Players { get; } = [];
    public ObservableCollection<OptionRow> Options { get; } = [];
    public ObservableCollection<PlayerScoreDto> Leaderboard { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(IsLobby), nameof(CanAccept), nameof(CanPlay), nameof(CanBot), nameof(CanJoin),
        nameof(CanStart), nameof(CanCancel), nameof(CanLeave))]
    private CompetitionDto? competition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestion), nameof(IsFillBlank), nameof(IsListen), nameof(QuestionNumber), nameof(Stem))]
    private CompetitionQuestionDto? question;

    [ObservableProperty] private string lobbyText = "";
    [ObservableProperty] private int secondsLeft;
    [ObservableProperty] private double timeProgress;
    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private bool answeredCurrent;
    [ObservableProperty] private string fillText = "";
    [ObservableProperty] private bool showingResult;
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string statusLine = "";

    public string Title => Competition is null ? "Match" : CompeteText.Mode(Competition.Summary.Mode);
    public string Subtitle => Competition is null ? "" : $"{Competition.Summary.LanguageName} {Competition.Summary.Level} · {Competition.Summary.QuestionCount} × {Competition.Summary.SecondsPerQuestion}s";
    public bool IsLobby => Competition is { } c && Question is null && c.Summary.Status is CompetitionStatus.Pending or CompetitionStatus.Waiting or CompetitionStatus.Scheduled;
    public bool HasQuestion => Question is not null;
    public bool IsFillBlank => Question?.Type == QuestionType.FillBlank;
    public bool IsListen => Question?.Type == QuestionType.ListenChoose;
    public string QuestionNumber => Question is null ? "" : $"Question {Question.Index + 1} of {Question.Count}";
    public string Stem => Question?.Stem ?? "";
    public bool CanAccept => Competition?.CanAccept ?? false;
    public bool CanPlay => Competition?.CanPlay ?? false;
    public bool CanBot => Competition?.CanBot ?? false;
    public bool CanJoin => Competition?.CanJoin ?? false;
    public bool CanStart => Competition?.CanStart ?? false;
    public bool CanCancel => Competition?.Summary is { Status: CompetitionStatus.Waiting or CompetitionStatus.Pending } s
                             && !s.NeedsMyAction && Competition.Summary.Mode is CompetitionMode.QuickMatch or CompetitionMode.Challenge;
    public bool CanLeave => Competition?.Summary.Status is CompetitionStatus.Running or CompetitionStatus.Scheduled && Competition.Players.Any(p => p.IsMe);

    private DateTimeOffset ServerNow => DateTimeOffset.UtcNow + _offset;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override async Task OnAppearingAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            live.QuestionStarted += OnQuestion;
            live.QuestionResult += OnResult;
            live.OpponentAnswered += OnOpponent;
            live.MatchUpdated += OnUpdated;
            live.MatchEnded += OnEnded;
        }
        await live.StartAsync();
        await RunAsync(ReloadAsync);
        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        _ = ClockAsync(_loop.Token);
    }

    public override Task OnDisappearingAsync()
    {
        _loop?.Cancel();
        if (_subscribed)
        {
            _subscribed = false;
            live.QuestionStarted -= OnQuestion;
            live.QuestionResult -= OnResult;
            live.OpponentAnswered -= OnOpponent;
            live.MatchUpdated -= OnUpdated;
            live.MatchEnded -= OnEnded;
        }
        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        var c = (await api.GetAsync(_id)).Data!;
        Apply(c);
        if (c.Summary.Status == CompetitionStatus.Finished) await ToResultAsync();
    }

    private void Apply(CompetitionDto c)
    {
        _offset = c.ServerNow - DateTimeOffset.UtcNow;
        Competition = c;
        Players.Clear();
        foreach (var p in c.Players) Players.Add(new PlayerRow(p) { Progress = (double)p.Answered / Math.Max(1, c.Summary.QuestionCount) });
        if (c.CurrentQuestion is { } q && (Question is null || Question.Index != q.Index || q.CompetitionId != Question.CompetitionId))
            ShowQuestion(q, c.IAnsweredCurrent);
        else if (c.CurrentQuestion is null && !ShowingResult) Question = null;
        UpdateLobby();
    }

    private void ShowQuestion(CompetitionQuestionDto q, bool answered = false)
    {
        _offset = q.ServerNow - DateTimeOffset.UtcNow;
        Question = q;
        Options.Clear();
        foreach (var o in q.Options) Options.Add(new OptionRow(o));
        FillText = "";
        AnsweredCurrent = answered;
        ShowingResult = false;
        StatusLine = answered ? "Waiting for the others…" : "";
        OnPropertyChanged(nameof(IsLobby));
    }

    private void UpdateLobby()
    {
        if (Competition is not { } c) return;
        var s = c.Summary;
        var others = c.Players.Where(p => !p.IsMe).Select(p => p.DisplayName).ToList();
        LobbyText = s.Status switch
        {
            CompetitionStatus.Pending when c.CanAccept => $"{string.Join(", ", others)} challenged you" + (s.IsLive ? " to a live match." : " — play in your own time."),
            CompetitionStatus.Pending when c.CanPlay => "Play your run now; they'll play theirs when they accept.",
            CompetitionStatus.Pending => $"Waiting for {string.Join(", ", others)} to accept (24 hours).",
            CompetitionStatus.Running when c.CanPlay => "Your turn: play the same questions in your own time.",
            CompetitionStatus.Running when !s.IsLive => "Done! Waiting for the other player to finish.",
            CompetitionStatus.Waiting when s.Mode == CompetitionMode.QuickMatch =>
                c.CanBot ? "Nobody's free right now. Play the practice bot?" : $"Looking for an opponent… {Seconds(s.ExpiresAt)}",
            CompetitionStatus.Waiting => $"{c.Players.Count} joined · starts in {Seconds(s.StartsAt)}",
            CompetitionStatus.Scheduled => $"Starting in {Seconds(s.StartsAt)}",
            CompetitionStatus.Declined => "The challenge was declined.",
            CompetitionStatus.Expired => "The challenge expired.",
            CompetitionStatus.Cancelled => "Cancelled.",
            _ => ""
        };
    }

    private string Seconds(DateTimeOffset? at)
    {
        if (at is null) return "";
        var left = (at.Value - ServerNow).TotalSeconds;
        return left <= 0 ? "a moment…" : left >= 90 ? $"{Math.Ceiling(left / 60)} min" : $"{Math.Ceiling(left)} s";
    }

    /// <summary>Counts down on screen; without a live connection it polls the server every 2 seconds.</summary>
    private async Task ClockAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
                if (Question is { } q)
                {
                    var now = ServerNow;
                    IsOpen = now >= q.OpensAt && now < q.ClosesAt && !AnsweredCurrent && !ShowingResult;
                    var total = (q.ClosesAt - q.OpensAt).TotalSeconds;
                    var left = now < q.OpensAt ? total : Math.Max(0, (q.ClosesAt - now).TotalSeconds);
                    SecondsLeft = (int)Math.Ceiling(left);
                    TimeProgress = total <= 0 ? 0 : left / total;
                    if (now < q.OpensAt) StatusLine = $"Get ready… {Math.Ceiling((q.OpensAt - now).TotalSeconds)}";
                    else if (StatusLine.StartsWith("Get ready")) StatusLine = "";
                }
                UpdateLobby();
                if (!live.IsConnected && DateTimeOffset.UtcNow >= _nextPoll && Competition?.Summary.Status is not CompetitionStatus.Finished)
                {
                    _nextPoll = DateTimeOffset.UtcNow.AddSeconds(2);
                    try { await ReloadAsync(); } catch (Exception) { /* next poll */ }
                }
                else if (Competition?.Summary.Status is CompetitionStatus.Scheduled or CompetitionStatus.Waiting && DateTimeOffset.UtcNow >= _nextPoll
                         && (Competition.Summary.StartsAt ?? Competition.Summary.ExpiresAt) <= ServerNow)
                {
                    _nextPoll = DateTimeOffset.UtcNow.AddSeconds(2);
                    try { await ReloadAsync(); } catch (Exception) { /* next poll */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
    }

    [RelayCommand]
    private Task Choose(OptionRow? option)
    {
        if (option is null || !IsOpen || Question is null) return Task.CompletedTask;
        foreach (var o in Options) o.IsSelected = o == option;
        return SubmitAsync(new SubmitAnswerRequest(Question.Index, option.Id, null));
    }

    [RelayCommand]
    private Task SubmitText()
    {
        if (!IsOpen || Question is null || string.IsNullOrWhiteSpace(FillText)) return Task.CompletedTask;
        return SubmitAsync(new SubmitAnswerRequest(Question.Index, null, FillText.Trim()));
    }

    [RelayCommand]
    private Task Listen() =>
        Question?.Prompt is { Length: > 0 } text ? speech.SpeakAsync(text, Competition?.Summary.LanguageCode) : Task.CompletedTask;

    private async Task SubmitAsync(SubmitAnswerRequest request)
    {
        AnsweredCurrent = true;
        IsOpen = false;
        StatusLine = "Answer sent…";
        var ack = await live.SubmitAsync(_id, request);
        if (!ack.Accepted)
        {
            StatusLine = ack.Message ?? "That answer didn't count.";
            if (ack.Code is "not_open") AnsweredCurrent = false;
            return;
        }
        StatusLine = Competition?.Summary.IsLive == true ? "Waiting for the others…" : "";
        if (ack.Result is { } result) ShowResult(result);
        if (ack.Finished)
        {
            await Task.Delay(1500);
            await ToResultAsync();
        }
        else if (ack.Next is { } next)
        {
            await Task.Delay(1500);
            ShowQuestion(next);
        }
    }

    private void ShowResult(QuestionResultDto r)
    {
        if (Question is null || r.Index != Question.Index) return;
        ShowingResult = true;
        IsOpen = false;
        var mine = r.Leaderboard.FirstOrDefault(p => Competition?.Players.FirstOrDefault(x => x.IsMe)?.PlayerId == p.PlayerId);
        ResultText = mine is null ? $"Answer: {r.CorrectAnswer}"
            : mine.Correct ? $"✅ Correct! +{mine.QuestionScore}"
            : mine.Answered ? $"❌ The answer was: {r.CorrectAnswer}"
            : $"⏰ Time's up. The answer was: {r.CorrectAnswer}";
        foreach (var o in Options)
        {
            o.IsRight = string.Equals(o.Text.Trim(), r.CorrectAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
            o.IsWrong = o.IsSelected && !o.IsRight;
        }
        Leaderboard.Clear();
        foreach (var p in r.Leaderboard) Leaderboard.Add(p);
        foreach (var row in Players)
        {
            var s = r.Leaderboard.FirstOrDefault(p => p.PlayerId == row.Dto.PlayerId);
            if (s is null) continue;
            row.Score = s.Score;
            row.RankText = $"#{s.Rank}";
        }
        StatusLine = "";
    }

    // ------------------------------------------------------------------ live events

    private void OnQuestion(object? sender, CompetitionQuestionDto q)
    {
        if (q.CompetitionId != _id) return;
        // Leave the last result up until the next question actually opens.
        if (ShowingResult)
            _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((q.OpensAt - q.ServerNow).TotalMilliseconds, 800, 3000)))
                .ContinueWith(_ => ShowQuestion(q), TaskScheduler.FromCurrentSynchronizationContext());
        else ShowQuestion(q);
    }

    private void OnResult(object? sender, QuestionResultDto r)
    {
        if (r.CompetitionId == _id) ShowResult(r);
    }

    private void OnOpponent(object? sender, OpponentAnsweredDto e)
    {
        if (e.CompetitionId != _id) return;
        var row = Players.FirstOrDefault(p => p.Dto.PlayerId == e.PlayerId);
        if (row is null || Competition is null) return;
        row.Answered = e.Answered;
        row.Progress = (double)e.Answered / Math.Max(1, Competition.Summary.QuestionCount);
    }

    private void OnUpdated(object? sender, int id)
    {
        if (id == _id) _ = RunAsync(ReloadAsync);
    }

    private void OnEnded(object? sender, int id)
    {
        if (id == _id) _ = Task.Delay(1500).ContinueWith(_ => ToResultAsync(), TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }

    private Task ToResultAsync()
    {
        _loop?.Cancel();
        return navigation.GoToAsync($"../{Routes.MatchResult}", new Dictionary<string, object> { ["id"] = _id });
    }

    // ------------------------------------------------------------------ lobby actions

    [RelayCommand]
    private Task Accept() => RunAsync(async () => Apply((await api.AcceptAsync(_id)).Data!));

    [RelayCommand]
    private Task Decline() => RunAsync(async () =>
    {
        await api.DeclineAsync(_id);
        await navigation.GoBackAsync();
    });

    [RelayCommand]
    private Task Play() => RunAsync(async () => Apply((await api.PlayAsync(_id)).Data!));

    [RelayCommand]
    private Task Bot() => RunAsync(async () => Apply((await api.PlayBotAsync(_id)).Data!));

    [RelayCommand]
    private Task Join() => RunAsync(async () => Apply((await api.JoinAsync(_id)).Data!));

    [RelayCommand]
    private Task StartNow() => RunAsync(async () => Apply((await api.StartNowAsync(_id)).Data!));

    [RelayCommand]
    private Task Cancel() => RunAsync(async () =>
    {
        await api.CancelAsync(_id);
        await navigation.GoBackAsync();
    });

    [RelayCommand]
    private async Task Leave()
    {
        if (!await dialogs.ConfirmAsync("Leave match", "Leaving now counts as a loss. Leave?", "Leave")) return;
        await RunAsync(async () =>
        {
            await api.LeaveAsync(_id);
            await navigation.GoBackAsync();
        });
    }
}

// ===================================================================== result (FR-Q07)

public sealed partial class MatchResultViewModel(
    ICompetitionApi api,
    ISocialApi social,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _id;

    public ObservableCollection<PlayerDto> Players { get; } = [];
    public ObservableCollection<ReviewItemDto> Review { get; } = [];

    [ObservableProperty] private string headline = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private bool canRematch;
    [ObservableProperty] private bool isFinished;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var r = (await api.ResultAsync(_id)).Data!;
        var me = r.Competition.Players.FirstOrDefault(p => p.IsMe);
        IsFinished = r.Competition.Summary.Status == CompetitionStatus.Finished;
        Headline = !IsFinished ? "Waiting for the result…"
            : me?.Result switch
            {
                CompetitionResult.Win => "🏆 You won!",
                CompetitionResult.Draw => "🤝 It's a draw",
                _ when r.Competition.Players.Count > 2 => $"You finished #{me?.Rank}",
                _ => "Good game!"
            };
        Detail = me is null ? "" : $"{me.Score:N0} points in the match · {me.Correct} of {r.Competition.Summary.QuestionCount} right"
                                   + (me.PointsAwarded > 0 ? $" · +{me.PointsAwarded} to your wallet" : "")
                                   + (me.RatingChange is { } rc ? $" · rating {(rc >= 0 ? "+" : "")}{rc}" : "");
        CanRematch = r.CanRematch;
        Players.Clear();
        foreach (var p in r.Competition.Players) Players.Add(p);
        Review.Clear();
        foreach (var item in r.Review) Review.Add(item);
    });

    [RelayCommand]
    private Task Rematch() => RunAsync(async () =>
    {
        var c = (await api.RematchAsync(_id)).Data!;
        await navigation.GoToAsync($"../{Routes.Match}", new Dictionary<string, object> { ["id"] = c.Summary.Id });
    });

    /// <summary>FR-Q07: share into one of my chats as a card.</summary>
    [RelayCommand]
    private async Task Share()
    {
        List<LinguaPath.Shared.Contracts.Chat.ConversationSummaryDto> chats;
        try
        {
            chats = (await social.GetConversationsAsync()).Data ?? [];
        }
        catch (Exception ex)
        {
            ErrorMessage = ApiError.From(ex).Message;
            return;
        }
        if (chats.Count == 0)
        {
            await dialogs.AlertAsync("Share", "You don't have any chats yet.");
            return;
        }
        var titles = chats.Take(15).Select(c => c.Title).ToArray();
        var choice = await dialogs.ChooseAsync("Share result to…", null, "Cancel", titles);
        var chat = chats.FirstOrDefault(c => c.Title == choice);
        if (chat is null) return;
        await RunAsync(async () =>
        {
            await api.ShareAsync(_id, new ShareRequest(chat.Id));
            await dialogs.ToastAsync($"Shared to {chat.Title}.");
        });
    }

    [RelayCommand]
    private Task Done() => navigation.GoBackAsync();
}

// ===================================================================== tournaments (FR-Q11) and league (FR-S08)

public sealed record TournamentRow(TournamentDto Dto)
{
    public int Id => Dto.Id;
    public string Title => Dto.Title;
    public string Detail => $"{Dto.LanguageName} {Dto.Level} · {Dto.Rounds} round(s) · {Dto.Entrants} joined";
    public string When => Dto.Status switch
    {
        TournamentStatus.Open => $"Starts {Dto.StartsAt.ToLocalTime():ddd d MMM HH:mm}",
        TournamentStatus.Running => $"Round {Dto.CurrentRound} of {Dto.Rounds} — live now",
        TournamentStatus.Finished => Dto.MyPlace is { } p ? $"Finished · you placed #{p}" : "Finished",
        _ => Dto.Status.ToString()
    };
    public string Prizes => $"🥇 {Dto.PrizeFirst} · 🥈 {Dto.PrizeSecond} · 🥉 {Dto.PrizeThird}";
    public bool IJoined => Dto.IJoined;
}

public sealed partial class TournamentsViewModel(ICompetitionApi api, INavigationService navigation) : BaseViewModel
{
    public ObservableCollection<TournamentRow> Tournaments { get; } = [];

    [ObservableProperty] private bool isEmpty;

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var list = (await api.TournamentsAsync()).Data ?? [];
        Tournaments.Clear();
        foreach (var t in list) Tournaments.Add(new TournamentRow(t));
        IsEmpty = Tournaments.Count == 0;
    });

    [RelayCommand]
    private Task Open(TournamentRow? row) =>
        row is null ? Task.CompletedTask : navigation.GoToAsync(Routes.Tournament, new Dictionary<string, object> { ["id"] = row.Id });
}

public sealed partial class TournamentViewModel(ICompetitionApi api, INavigationService navigation, IDialogService dialogs) : BaseViewModel
{
    private int _id;

    public ObservableCollection<TournamentStandingDto> Standings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Row), nameof(CanJoin), nameof(CanWithdraw), nameof(CanPlay), nameof(StatusText), nameof(HasStandings))]
    private TournamentDto? tournament;

    public TournamentRow? Row => Tournament is null ? null : new TournamentRow(Tournament);
    public bool CanJoin => Tournament?.CanJoin ?? false;
    public bool CanWithdraw => Tournament is { IJoined: true, Status: TournamentStatus.Open };
    public bool CanPlay => Tournament?.MyCompetitionId is not null;
    public bool HasStandings => Standings.Count > 0;
    public string StatusText => Tournament switch
    {
        null => "",
        { IDisqualified: true } => "You were disqualified from this tournament.",
        { Status: TournamentStatus.Open, IJoined: true } => "You're in! Come back when it starts — we'll notify you.",
        { Status: TournamentStatus.Open } t when !t.CanJoin => $"Entry opens {t.EntryOpensAt.ToLocalTime():ddd d MMM HH:mm}",
        { Status: TournamentStatus.Running, MyCompetitionId: not null } => "Your round is on — play now!",
        { Status: TournamentStatus.Running, IJoined: true } => "You're out of the running this time. Watch the standings.",
        { Status: TournamentStatus.Cancelled } => "This tournament was cancelled.",
        _ => ""
    };

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override Task OnAppearingAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Tournament = (await api.TournamentAsync(_id)).Data;
        Standings.Clear();
        foreach (var s in Tournament?.Standings ?? []) Standings.Add(s);
        OnPropertyChanged(nameof(HasStandings));
    }

    [RelayCommand]
    private Task Join() => RunAsync(async () =>
    {
        Tournament = (await api.JoinTournamentAsync(_id)).Data;
        await dialogs.ToastAsync("You're in!");
    });

    [RelayCommand]
    private Task Withdraw() => RunAsync(async () =>
    {
        await api.WithdrawAsync(_id);
        await LoadAsync();
    });

    [RelayCommand]
    private Task Play() => Tournament?.MyCompetitionId is { } id
        ? navigation.GoToAsync(Routes.Match, new Dictionary<string, object> { ["id"] = id })
        : Task.CompletedTask;

    [RelayCommand]
    private Task Refresh() => RunAsync(LoadAsync);
}

public sealed record LeagueRow(LeagueMemberDto Dto)
{
    public string RankText => $"{Dto.Rank}";
    public string Name => Dto.IsMe ? $"{Dto.DisplayName} (you)" : Dto.DisplayName;
    public string Points => $"{Dto.WeeklyPoints:N0}";
    public string Zone => Dto.Zone switch { LeagueOutcome.Promoted => "⬆️", LeagueOutcome.Relegated => "⬇️", _ => "" };
    public bool IsMe => Dto.IsMe;
}

public sealed partial class LeagueViewModel(ICompetitionApi api) : BaseViewModel
{
    public ObservableCollection<LeagueRow> Members { get; } = [];

    [ObservableProperty] private string header = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string lastWeek = "";
    [ObservableProperty] private bool hasLastWeek;

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var l = (await api.LeagueAsync()).Data!;
        Header = $"{Icon(l.Tier)} {l.Tier} league";
        var left = l.EndsAt - DateTimeOffset.UtcNow;
        Detail = $"You're #{l.MyRank} with {l.MyPoints:N0} points this week · ends in {(left.TotalDays >= 1 ? $"{(int)left.TotalDays} d " : "")}{left.Hours} h"
                 + (l.PromoteCount > 0 ? $" · top {l.PromoteCount} go up" : "") + (l.RelegateCount > 0 ? $" · bottom {l.RelegateCount} go down" : "");
        LastWeek = l.LastWeekOutcome switch
        {
            LeagueOutcome.Promoted => $"Promoted from {l.LastWeekTier} last week 🎉",
            LeagueOutcome.Relegated => $"Moved down from {l.LastWeekTier} last week — you can climb back!",
            LeagueOutcome.Stayed => $"You stayed in {l.LastWeekTier} last week.",
            _ => ""
        };
        HasLastWeek = LastWeek.Length > 0;
        Members.Clear();
        foreach (var m in l.Members) Members.Add(new LeagueRow(m));
    });

    public static string Icon(LeagueTier tier) => tier switch
    {
        LeagueTier.Bronze => "🥉", LeagueTier.Silver => "🥈", LeagueTier.Gold => "🥇", LeagueTier.Platinum => "💠", _ => "💎"
    };
}
