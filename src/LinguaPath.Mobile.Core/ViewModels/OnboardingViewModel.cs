using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Learning;

namespace LinguaPath.Mobile.Core.ViewModels;

public sealed partial class LanguageCard(int id, string name, string nativeName, LanguageStatus status) : ObservableObject
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string NativeName { get; } = nativeName;
    public bool IsAvailable { get; } = status == LanguageStatus.Published;
    public bool IsComingSoon => !IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool isRequested;

    [ObservableProperty] private bool isSelected;

    public string ActionText => IsAvailable ? "" : IsRequested ? "Requested" : "Request";
}

/// <summary>
/// Onboarding wizard (FR-L10 .. FR-L16): language → current level → goal → schedule → plan check → start.
/// "Not sure?" opens a short placement test that sets the level automatically (FR-L11).
/// </summary>
public sealed partial class OnboardingViewModel(
    ILearningApi learningApi,
    ICourseContext courseContext,
    IAuthSession session,
    INavigationService navigation,
    IDialogService dialogs,
    PlacementResults placement) : BaseViewModel
{
    public const int LastStep = 4;

    // ------------------------------------------------------------- step state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanguageStep), nameof(IsLevelStep), nameof(IsGoalStep), nameof(IsScheduleStep),
        nameof(IsSummaryStep), nameof(Progress), nameof(StepTitle), nameof(CanGoBack), nameof(NextText))]
    private int step;

    public bool IsLanguageStep => Step == 0;
    public bool IsLevelStep => Step == 1;
    public bool IsGoalStep => Step == 2;
    public bool IsScheduleStep => Step == 3;
    public bool IsSummaryStep => Step == 4;
    public double Progress => (Step + 1) / (double)(LastStep + 1);
    public bool CanGoBack => Step > 0;
    public string NextText => Step == LastStep ? "Start learning" : "Continue";

    public string StepTitle => Step switch
    {
        0 => "Which language do you want to learn?",
        1 => "How well do you know it already?",
        2 => "What's your goal?",
        3 => "How much time do you have?",
        _ => "Your plan"
    };

    // ------------------------------------------------------------- step 0: language and course

    public ObservableCollection<LanguageCard> Languages { get; } = [];
    public ObservableCollection<CourseDto> Courses { get; } = [];

    [ObservableProperty] private LanguageCard? selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseSummary))]
    private CourseDto? selectedCourse;

    public bool HasManyCourses => Courses.Count > 1;
    public string CourseSummary => SelectedCourse is null ? "" :
        $"{SelectedCourse.Title} · {SelectedCourse.StartLevel} → {SelectedCourse.EndLevel} · {SelectedCourse.LectureCount} lectures · about {SelectedCourse.EstimatedHours} h";

    // ------------------------------------------------------------- step 1: current level

    public IReadOnlyList<Option<CefrLevel>> Levels { get; } = LearningText.Levels();

    [ObservableProperty] private CefrLevel? currentLevel;

    // ------------------------------------------------------------- step 2: target level and goal

    public ObservableCollection<Option<CefrLevel>> TargetLevels { get; } = [];
    public IReadOnlyList<Option<LearningGoal>> Goals { get; } = LearningText.Goals();

    [ObservableProperty] private CefrLevel? targetLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExamGoal))]
    private LearningGoal? goal;

    [ObservableProperty] private string? examName;

    public bool IsExamGoal => Goal == LearningGoal.ExamPrep;

    // ------------------------------------------------------------- step 3: schedule

    public IReadOnlyList<Option<int>> Durations { get; } = LearningText.Durations();
    public IReadOnlyList<Option<int>> MinuteOptions { get; } = LearningText.DailyMinutes();
    public IReadOnlyList<Option<DayOfWeek>> Days { get; } = LearningText.Days();
    public IReadOnlyList<Option<FocusSkill>> Skills { get; } = LearningText.Skills();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomEnd))]
    private int durationMonths = 3;

    [ObservableProperty] private DateTime customEndDate = DateTime.Today.AddMonths(3);
    [ObservableProperty] private int dailyMinutes = 20;
    [ObservableProperty] private bool hasStudyTime = true;
    [ObservableProperty] private TimeSpan studyTime = new(19, 0, 0);

    public bool IsCustomEnd => DurationMonths == 0;
    public DateTime MinEndDate => DateTime.Today.AddDays(7);
    public DateTime MaxEndDate => DateTime.Today.AddYears(2);

    // ------------------------------------------------------------- step 4: plan check

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanIsOk), nameof(PlanHasWarning), nameof(PlanNotFeasible), nameof(HasSuggestedMinutes), nameof(HasSuggestedEnd), nameof(PlanStats))]
    private FeasibilityResponse? plan;

    public bool PlanIsOk => Plan?.Status == FeasibilityStatus.Ok;
    public bool PlanHasWarning => Plan?.Status == FeasibilityStatus.OptionalDropped;
    public bool PlanNotFeasible => Plan?.Status == FeasibilityStatus.NotFeasible;
    public bool HasSuggestedMinutes => PlanNotFeasible && Plan?.SuggestedDailyMinutes is not null;
    public bool HasSuggestedEnd => PlanNotFeasible && Plan?.SuggestedEndDate is not null;
    public string PlanStats => Plan is null ? "" :
        $"{Plan.PlannedLectures} lectures · about {Plan.LecturesPerWeek} a week · {Plan.StartDate:d MMM} to {Plan.EndDate:d MMM yyyy}";

    private bool _initialised;

    public override async Task OnAppearingAsync()
    {
        if (_initialised)
        {
            // Back from the placement test: use its level (FR-L11).
            if (SelectedCourse is not null && placement.Take(SelectedCourse.Id) is { } level &&
                Levels.FirstOrDefault(l => l.Value == level) is { } option)
            {
                var course = SelectedCourse;
                SelectLevel(option);
                if (SelectedCourse is null) SelectedCourse = course;
                await dialogs.ToastAsync($"Level set to {level} from your placement test.");
            }
            return;
        }
        _initialised = true;
        LearningText.Select(Durations, 3);
        LearningText.Select(MinuteOptions, 20);
        foreach (var d in Days) d.IsSelected = d.Value is not DayOfWeek.Sunday;
        foreach (var s in Skills) s.IsSelected = true;

        await RunAsync(async () =>
        {
            var response = await learningApi.GetLanguagesAsync();
            Languages.Clear();
            foreach (var l in response.Data ?? [])
                Languages.Add(new LanguageCard(l.Id, l.Name, l.NativeName, l.Status));
        });
    }

    // ------------------------------------------------------------- step 0 commands

    [RelayCommand]
    private async Task SelectLanguage(LanguageCard card)
    {
        if (!card.IsAvailable)
        {
            await RequestLanguage(card);
            return;
        }

        foreach (var l in Languages) l.IsSelected = l == card;
        SelectedLanguage = card;
        SelectedCourse = null;
        await RunAsync(async () =>
        {
            var response = await learningApi.GetCoursesAsync(card.Id);
            Courses.Clear();
            foreach (var c in response.Data ?? []) Courses.Add(c);
            OnPropertyChanged(nameof(HasManyCourses));
            if (Courses.Count == 0)
            {
                ErrorMessage = $"{card.Name} has no courses yet. Please pick another language.";
                return;
            }
            SelectedCourse = Courses[0];
            Step = 1;
        });
    }

    [RelayCommand]
    private async Task RequestLanguage(LanguageCard card)
    {
        if (card.IsRequested) return;
        if (!await dialogs.ConfirmAsync($"{card.Name} is coming soon",
                "Would you like us to tell the team you want it? We'll prioritise the most requested languages.", "Request it", "Not now"))
            return;

        await RunAsync(async () =>
        {
            await learningApi.RequestLanguageAsync(new CreateLanguageRequest(card.Name, card.Id, null));
            card.IsRequested = true;
            await dialogs.ToastAsync("Thanks! We've noted your request.");
        });
    }

    [RelayCommand]
    private async Task RequestOtherLanguage()
    {
        var name = await dialogs.PromptAsync("Request a language", "Which language would you like to learn?", "Send", "e.g. Swahili");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(async () =>
        {
            await learningApi.RequestLanguageAsync(new CreateLanguageRequest(name.Trim(), null, null));
            await dialogs.ToastAsync("Thanks! We've noted your request.");
        });
    }

    // ------------------------------------------------------------- step 1 + 2 commands

    [RelayCommand]
    private void SelectLevel(Option<CefrLevel> option)
    {
        LearningText.Select(Levels, option.Value);
        CurrentLevel = option.Value;

        // Best course for this level when the language has several (one per starting band).
        var best = Courses.FirstOrDefault(c => c.StartLevel <= option.Value && option.Value < c.EndLevel)
                   ?? Courses.FirstOrDefault(c => c.StartLevel <= option.Value && option.Value <= c.EndLevel)
                   ?? Courses.OrderBy(c => Math.Abs((int)c.StartLevel - (int)option.Value)).FirstOrDefault();
        if (best is not null) SelectedCourse = best;
        BuildTargetLevels();
    }

    partial void OnSelectedCourseChanged(CourseDto? value) => BuildTargetLevels();

    /// <summary>FR-L11: about 10 questions across the course's levels; the result comes back via <see cref="PlacementResults"/>.</summary>
    [RelayCommand]
    private Task TakePlacementTest() => SelectedCourse is null
        ? dialogs.ToastAsync("Pick a language first.")
        : navigation.GoToAsync(Routes.Quiz, new Dictionary<string, object> { ["mode"] = "placement", ["courseId"] = SelectedCourse.Id });

    private void BuildTargetLevels()
    {
        TargetLevels.Clear();
        if (SelectedCourse is null || CurrentLevel is null) return;
        var from = CurrentLevel.Value < SelectedCourse.StartLevel ? SelectedCourse.StartLevel : CurrentLevel.Value;
        foreach (var level in LearningText.Levels().Where(l => l.Value >= from && l.Value <= SelectedCourse.EndLevel))
            TargetLevels.Add(level);

        // Default goal: one level up, within the course.
        var preferred = TargetLevels.FirstOrDefault(t => t.Value > CurrentLevel) ?? TargetLevels.LastOrDefault();
        if (preferred is not null)
        {
            LearningText.Select(TargetLevels, preferred.Value);
            TargetLevel = preferred.Value;
        }
    }

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

    // ------------------------------------------------------------- step 3 commands

    [RelayCommand]
    private void SelectDuration(Option<int> option)
    {
        LearningText.Select(Durations, option.Value);
        DurationMonths = option.Value;
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

    // ------------------------------------------------------------- step 4 commands

    [RelayCommand]
    private Task UseSuggestedMinutes()
    {
        if (Plan?.SuggestedDailyMinutes is not { } m) return Task.CompletedTask;
        DailyMinutes = m;
        LearningText.Select(MinuteOptions, m);
        return CheckPlanAsync();
    }

    [RelayCommand]
    private Task UseSuggestedEnd()
    {
        if (Plan?.SuggestedEndDate is not { } end) return Task.CompletedTask;
        DurationMonths = 0;
        LearningText.Select(Durations, 0);
        CustomEndDate = end.ToDateTime(TimeOnly.MinValue);
        return CheckPlanAsync();
    }

    // ------------------------------------------------------------- navigation between steps

    [RelayCommand]
    private async Task Next()
    {
        ErrorMessage = null;
        switch (Step)
        {
            case 0:
                if (SelectedLanguage is null || SelectedCourse is null) { ErrorMessage = "Pick a language to continue."; return; }
                Step = 1;
                break;
            case 1:
                if (CurrentLevel is null) { ErrorMessage = "Pick the level that sounds most like you."; return; }
                Step = 2;
                break;
            case 2:
                if (TargetLevel is null) { ErrorMessage = "Pick the level you want to reach."; return; }
                if (Goal is null) { ErrorMessage = "Pick your main goal."; return; }
                Step = 3;
                break;
            case 3:
                if (!Days.Any(d => d.IsSelected)) { ErrorMessage = "Pick at least one study day."; return; }
                if (IsCustomEnd && CustomEndDate.Date <= DateTime.Today) { ErrorMessage = "Pick an end date in the future."; return; }
                await CheckPlanAsync();
                if (Plan is not null) Step = 4;
                break;
            default:
                await StartAsync();
                break;
        }
    }

    [RelayCommand]
    private Task Back()
    {
        ErrorMessage = null;
        if (Step > 0) Step--;
        else return navigation.GoBackAsync();
        return Task.CompletedTask;
    }

    private LearningPlanRequest BuildRequest() => new(
        SelectedCourse!.Id,
        CurrentLevel!.Value,
        TargetLevel!.Value,
        Goal!.Value,
        IsExamGoal ? ExamName : null,
        IsCustomEnd ? null : DurationMonths,
        IsCustomEnd ? DateOnly.FromDateTime(CustomEndDate) : null,
        DailyMinutes,
        LearningText.ToStudyDays(Days),
        HasStudyTime ? TimeOnly.FromTimeSpan(StudyTime) : null,
        LearningText.ToSkills(Skills));

    private Task CheckPlanAsync() => RunAsync(async () =>
    {
        var response = await learningApi.CheckPlanAsync(BuildRequest());
        Plan = response.Data;
    });

    private Task StartAsync() => RunAsync(async () =>
    {
        var response = await learningApi.EnrolAsync(BuildRequest());
        var course = response.Data!.Course;
        await courseContext.SetAsync(course.Id);
        if (session.User is not null) await session.UpdateUserAsync(session.User);
        await dialogs.ToastAsync("Your roadmap is ready!");
        await navigation.GoToAsync(Routes.Home);
    }, async error =>
    {
        if (error.Code != "course_already_active") return false;
        await dialogs.AlertAsync("Already learning this language", error.Message);
        await navigation.GoToAsync(Routes.Home);
        return true;
    });
}
