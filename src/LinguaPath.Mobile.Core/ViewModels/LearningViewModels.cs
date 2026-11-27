using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Learning;

namespace LinguaPath.Mobile.Core.ViewModels;

/// <summary>Shared by Home and Learn: picks the course to show (remembered choice, else the first one).</summary>
internal static class CourseSelection
{
    public static async Task<LearnerCourseDto?> ResolveAsync(ILearningApi api, ICourseContext context, IList<LearnerCourseDto>? into = null)
    {
        var courses = (await api.GetMyCoursesAsync()).Data ?? [];
        if (into is not null)
        {
            into.Clear();
            foreach (var c in courses.Where(c => c.Status is LearnerCourseStatus.Active or LearnerCourseStatus.Paused)) into.Add(c);
        }
        var active = courses.Where(c => c.Status is LearnerCourseStatus.Active or LearnerCourseStatus.Paused).ToList();
        if (active.Count == 0) return null;

        var remembered = await context.GetAsync();
        var chosen = active.FirstOrDefault(c => c.Id == remembered) ?? active[0];
        if (chosen.Id != remembered) await context.SetAsync(chosen.Id);
        return chosen;
    }
}

/// <summary>Home tab (FR-L20 .. FR-L24): today's lectures, progress, days left, course switcher.</summary>
public sealed partial class HomeViewModel(
    ILearningApi learningApi,
    IAssessmentApi assessmentApi,
    ICourseContext courseContext,
    IAuthSession session,
    INavigationService navigation,
    IDialogService dialogs,
    INotificationsApi notificationsApi,
    SessionStartup startup) : BaseViewModel
{
    [ObservableProperty] private string greeting = "Welcome";

    /// <summary>Phase 10: unread inbox entries on the 🔔 button (FR-L112).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadBadge))]
    private int unreadCount;

    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadge => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    [RelayCommand]
    private Task OpenNotifications() => navigation.GoToAsync(Routes.Notifications);

    /// <summary>Phase 10: the next monthly exam, now filled in by the API.</summary>
    public bool HasNextExam => Today?.NextExamDate is not null;
    public string NextExamText => Today?.NextExamDate is { } d ? $"🎓 {d:d MMM}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCourse), nameof(NoCourse))]
    private TodayDto? today;

    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isRefreshing;

    /// <summary>Phase 8: chats live behind the 💬 button on Home (and in Profile).</summary>
    [RelayCommand]
    private Task OpenChats() => navigation.GoToAsync(Routes.Chats);

    /// <summary>Phase 9: quiz competitions.</summary>
    [RelayCommand]
    private Task OpenCompete() => navigation.GoToAsync(Routes.Compete);

    public bool HasCourse => Today is not null;
    public bool NoCourse => IsLoaded && Today is null;
    public bool HasSeveralCourses => MyCourses.Count > 1;

    public ObservableCollection<LearnerCourseDto> MyCourses { get; } = [];
    public ObservableCollection<RoadmapLectureDto> NextLectures { get; } = [];

    [ObservableProperty] private LearnerCourseDto? selectedCourse;

    // Daily quiz card (FR-L50)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDailyQuiz), nameof(DailyQuizText), nameof(DailyQuizButton), nameof(CanStartDailyQuiz),
        nameof(DailyQuizDone), nameof(ReviewDueText))]
    private LinguaPath.Shared.Contracts.Assessment.DailyQuizDto? dailyQuiz;

    public bool HasDailyQuiz => DailyQuiz is not null;
    public string DailyQuizText => DailyQuiz?.Message ?? "";
    public bool DailyQuizDone => DailyQuiz?.State == DailyQuizState.Done;
    public bool CanStartDailyQuiz => DailyQuiz?.State is DailyQuizState.Ready or DailyQuizState.InProgress or DailyQuizState.Done;
    public string DailyQuizButton => DailyQuiz?.State switch
    {
        DailyQuizState.InProgress => "Continue quiz",
        DailyQuizState.Done => "Practise again",
        _ => "Start quiz"
    };
    public string ReviewDueText => DailyQuiz is { ReviewItemsDue: > 0 } d ? $"{d.ReviewItemsDue} word(s) from past mistakes are due for review." : "";

    // Display helpers
    public string CourseTitle => Today is null ? "" : $"{Today.Course.LanguageName} · {Today.Course.CourseTitle}";
    public string LevelText => Today is null ? "" : $"{Today.Course.CurrentLevel} → {Today.Course.TargetLevel}";
    public double ProgressFraction => (Today?.Course.ProgressPercent ?? 0) / 100.0;
    public string ProgressText => Today is null ? "" : $"{Today.Course.ProgressPercent}% · {Today.Course.CompletedLectures} of {Today.Course.TotalLectures} lectures";
    public string DaysLeftText => Today is null ? "" : Today.Course.DaysLeft == 1 ? "1 day left" : $"{Today.Course.DaysLeft} days left";
    public bool IsPaused => Today?.Course.Status == LearnerCourseStatus.Paused;
    public string PausedText => Today?.Course.PausedUntil is { } until ? $"Paused until {until:d MMM}. Enjoy your break!" : "";
    public bool IsBehind => (Today?.BehindCount ?? 0) > 0 && !IsPaused;
    public string BehindText => Today is null ? "" : $"{Today.BehindCount} lecture(s) behind plan. You've got this!";
    public string TodayHeader => NextLectures.Any(l => l.PlannedDate <= DateOnly.FromDateTime(DateTime.Today)) ? "Today" : "Up next";

    private bool _switching;

    partial void OnTodayChanged(TodayDto? value)
    {
        NextLectures.Clear();
        foreach (var l in value?.NextLectures ?? []) NextLectures.Add(l);
        foreach (var name in new[] { nameof(CourseTitle), nameof(LevelText), nameof(ProgressFraction), nameof(ProgressText),
                     nameof(DaysLeftText), nameof(IsPaused), nameof(PausedText), nameof(IsBehind), nameof(BehindText), nameof(TodayHeader),
                     nameof(HasNextExam), nameof(NextExamText) })
            OnPropertyChanged(name);
    }

    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(NoCourse));

    partial void OnSelectedCourseChanged(LearnerCourseDto? value)
    {
        if (_switching || value is null || value.Id == Today?.Course.Id) return;
        _ = SwitchToAsync(value.Id);
    }

    public override Task OnAppearingAsync()
    {
        var hour = DateTime.Now.Hour;
        var part = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        Greeting = session.User is null ? part : $"{part}, {session.User.Greeting}";
        _ = startup.RunAsync();
        _ = RefreshUnreadAsync();
        return LoadAsync();
    }

    private async Task RefreshUnreadAsync()
    {
        try { UnreadCount = (await notificationsApi.UnreadCountAsync()).Data?.Count ?? 0; }
        catch { /* the badge is a nicety */ }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync();
        IsRefreshing = false;
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        var course = await CourseSelection.ResolveAsync(learningApi, courseContext, MyCourses);
        OnPropertyChanged(nameof(HasSeveralCourses));
        Today = course is null ? null : (await learningApi.GetTodayAsync(course.Id)).Data;
        try
        {
            DailyQuiz = course is null ? null : (await assessmentApi.GetDailyQuizAsync(course.Id)).Data;
        }
        catch
        {
            DailyQuiz = null; // the rest of Home still works without the quiz card
        }
        _switching = true;
        SelectedCourse = MyCourses.FirstOrDefault(c => c.Id == course?.Id);
        _switching = false;
        IsLoaded = true;
    });

    private async Task SwitchToAsync(int learnerCourseId)
    {
        await courseContext.SetAsync(learnerCourseId);
        await LoadAsync();
    }

    [RelayCommand]
    private Task StartCourse() => navigation.GoToAsync(Routes.Onboarding);

    [RelayCommand]
    private Task OpenRoadmap() => navigation.GoToAsync(Routes.Learn);

    [RelayCommand]
    private Task OpenPosts() => navigation.GoToAsync(Routes.Posts);

    [RelayCommand]
    private Task StartDailyQuiz()
    {
        if (Today is null || DailyQuiz is null) return Task.CompletedTask;
        var parameters = new Dictionary<string, object> { ["mode"] = "daily", ["learnerCourseId"] = Today.Course.Id };
        if (DailyQuiz.State == DailyQuizState.Done) parameters["practice"] = "true";
        return navigation.GoToAsync(Routes.Quiz, parameters);
    }

    [RelayCommand]
    private Task OpenExams() => Today is null ? Task.CompletedTask
        : navigation.GoToAsync(Routes.Exams, new Dictionary<string, object> { ["learnerCourseId"] = Today.Course.Id });

    [RelayCommand]
    private Task OpenMarks() => Today is null ? Task.CompletedTask
        : navigation.GoToAsync(Routes.Marks, new Dictionary<string, object> { ["learnerCourseId"] = Today.Course.Id });

    [RelayCommand]
    private Task OpenBookmarks() => navigation.GoToAsync(Routes.Bookmarks);

    [RelayCommand]
    private Task OpenSettings() => Today is null
        ? Task.CompletedTask
        : navigation.GoToAsync(Routes.CourseSettings, new Dictionary<string, object> { ["id"] = Today.Course.Id });

    [RelayCommand]
    private Task OpenLecture(RoadmapLectureDto lecture) =>
        Today is null ? Task.CompletedTask : LectureOpener.OpenAsync(lecture, Today.Course.Id, navigation, dialogs);
}

/// <summary>A module header with its lectures, for a grouped list (FR-L30).</summary>
public sealed class ModuleGroup(RoadmapModuleDto module) : List<RoadmapLectureDto>(module.Lectures)
{
    public string Title { get; } = module.Title;
    public string? Objective { get; } = module.Objective;
    public string Subtitle { get; } = $"{module.Level} · Month {module.MonthNo} · {module.CompletedLectures}/{module.Lectures.Count} done";
    public RoadmapItemState State { get; } = module.State;
    public bool IsLocked => State == RoadmapItemState.Locked;
    public bool IsDone => State == RoadmapItemState.Completed;
}

/// <summary>Learn tab: the personal roadmap with lecture states (FR-L30, FR-L31).</summary>
public sealed partial class RoadmapViewModel(
    ILearningApi learningApi,
    ICourseContext courseContext,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    public ObservableCollection<ModuleGroup> Modules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCourse), nameof(Title), nameof(Summary))]
    private LearnerCourseDto? course;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoCourse))]
    private bool isLoaded;

    public bool HasCourse => Course is not null;
    public bool NoCourse => IsLoaded && Course is null;
    public string Title => Course is null ? "Your roadmap" : $"{Course.LanguageName} roadmap";
    public string Summary => Course is null ? "" :
        $"{Course.CourseTitle} · {Course.CurrentLevel} → {Course.TargetLevel} · {Course.ProgressPercent}% done · ends {Course.EndDate:d MMM yyyy}";

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var chosen = await CourseSelection.ResolveAsync(learningApi, courseContext);
        Modules.Clear();
        if (chosen is null)
        {
            Course = null;
            IsLoaded = true;
            return;
        }
        var roadmap = (await learningApi.GetRoadmapAsync(chosen.Id)).Data!;
        Course = roadmap.Course;
        foreach (var m in roadmap.Modules) Modules.Add(new ModuleGroup(m));
        IsLoaded = true;
    });

    [RelayCommand]
    private Task OpenLecture(RoadmapLectureDto lecture) =>
        Course is null ? Task.CompletedTask : LectureOpener.OpenAsync(lecture, Course.Id, navigation, dialogs);

    [RelayCommand]
    private Task StartCourse() => navigation.GoToAsync(Routes.Onboarding);

    [RelayCommand]
    private Task OpenSettings() => Course is null
        ? Task.CompletedTask
        : navigation.GoToAsync(Routes.CourseSettings, new Dictionary<string, object> { ["id"] = Course.Id });
}

/// <summary>Change goal, duration, daily time, days; pause or resume (FR-L120, FR-L121).</summary>
public sealed partial class CourseSettingsViewModel(
    ILearningApi learningApi,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(IsActive), nameof(PauseInfo), nameof(Header))]
    private LearnerCourseDto? course;

    public ObservableCollection<Option<CefrLevel>> TargetLevels { get; } = [];
    public IReadOnlyList<Option<LearningGoal>> Goals { get; } = LearningText.Goals();
    public IReadOnlyList<Option<int>> MinuteOptions { get; } = LearningText.DailyMinutes();
    public IReadOnlyList<Option<DayOfWeek>> Days { get; } = LearningText.Days();
    public IReadOnlyList<Option<FocusSkill>> Skills { get; } = LearningText.Skills();

    [ObservableProperty] private CefrLevel targetLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExamGoal))]
    private LearningGoal goal;

    [ObservableProperty] private string? examName;
    [ObservableProperty] private DateTime endDate = DateTime.Today.AddMonths(3);
    [ObservableProperty] private int dailyMinutes = 20;
    [ObservableProperty] private bool hasStudyTime;
    [ObservableProperty] private TimeSpan studyTime = new(19, 0, 0);

    public bool IsExamGoal => Goal == LearningGoal.ExamPrep;
    public bool IsPaused => Course?.Status == LearnerCourseStatus.Paused;
    public bool IsActive => Course?.Status == LearnerCourseStatus.Active;
    public string Header => Course is null ? "" : $"{Course.LanguageName} · {Course.CourseTitle}";
    public string PauseInfo => Course is null ? ""
        : IsPaused ? $"Paused until {Course.PausedUntil:d MMM}. Your plan moves by the days you're away."
        : $"Going on holiday? Pause for up to {Course.RemainingPauseDays} more day(s); your deadlines move with you.";
    public DateTime MinEndDate => DateTime.Today.AddDays(1);
    public DateTime MaxEndDate => DateTime.Today.AddYears(2);

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _id = id;
    }

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var c = (await learningApi.GetMyCourseAsync(_id)).Data!;
        Course = c;
        TargetLevels.Clear();
        foreach (var l in LearningText.Levels().Where(l => l.Value >= c.CurrentLevel)) TargetLevels.Add(l);
        TargetLevel = c.TargetLevel;
        LearningText.Select(TargetLevels, c.TargetLevel);
        Goal = c.Goal;
        LearningText.Select(Goals, c.Goal);
        ExamName = c.ExamName;
        EndDate = c.EndDate.ToDateTime(TimeOnly.MinValue);
        DailyMinutes = c.DailyMinutes;
        LearningText.Select(MinuteOptions, c.DailyMinutes);
        foreach (var d in Days) d.IsSelected = c.StudyDays.Includes(d.Value);
        foreach (var s in Skills) s.IsSelected = c.FocusSkills.HasFlag(s.Value);
        HasStudyTime = c.PreferredStudyTime is not null;
        if (c.PreferredStudyTime is { } t) StudyTime = t.ToTimeSpan();
    });

    [RelayCommand]
    private void SelectTarget(Option<CefrLevel> option)
    {
        LearningText.Select(TargetLevels, option.Value);
        TargetLevel = option.Value;
    }

    [RelayCommand]
    private void SelectGoal(Option<LearningGoal> option)
    {
        LearningText.Select(Goals, option.Value);
        Goal = option.Value;
    }

    [RelayCommand]
    private void SelectMinutes(Option<int> option)
    {
        LearningText.Select(MinuteOptions, option.Value);
        DailyMinutes = option.Value;
    }

    [RelayCommand]
    private void ToggleDay(Option<DayOfWeek> option) => option.IsSelected = !option.IsSelected;

    [RelayCommand]
    private void ToggleSkill(Option<FocusSkill> option) => option.IsSelected = !option.IsSelected;

    [RelayCommand]
    private Task Save()
    {
        if (!Days.Any(d => d.IsSelected))
        {
            ErrorMessage = "Pick at least one study day.";
            return Task.CompletedTask;
        }

        return RunAsync(async () =>
        {
            var request = new UpdateLearningPlanRequest(TargetLevel, Goal, IsExamGoal ? ExamName : null, null,
                DateOnly.FromDateTime(EndDate), DailyMinutes, LearningText.ToStudyDays(Days),
                HasStudyTime ? TimeOnly.FromTimeSpan(StudyTime) : null, LearningText.ToSkills(Skills));
            var response = await learningApi.UpdatePlanAsync(_id, request);
            await dialogs.AlertAsync("Plan updated", response.Data!.Plan.Message);
            await navigation.GoBackAsync();
        });
    }

    [RelayCommand]
    private async Task Pause()
    {
        if (Course is null) return;
        var answer = await dialogs.PromptAsync("Pause course", $"For how many days? (1–{Course.RemainingPauseDays})", "Pause", "7");
        if (!int.TryParse(answer, out var days)) return;
        await RunAsync(async () =>
        {
            Course = (await learningApi.PauseAsync(_id, new PauseCourseRequest(days))).Data;
            await dialogs.ToastAsync("Course paused. See you soon!");
        });
    }

    [RelayCommand]
    private Task Resume() => RunAsync(async () =>
    {
        Course = (await learningApi.ResumeAsync(_id)).Data;
        await dialogs.ToastAsync("Welcome back! Your plan has moved by the days you were away.");
    });
}
