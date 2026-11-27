namespace LinguaPath.Shared.Contracts.Learning;

// ------------------------------------------------------------------ catalogue

/// <summary>A published course of a language (FR-A12), shown during onboarding.</summary>
public record CourseDto(
    int Id,
    int LanguageId,
    string Title,
    string? Description,
    string? CoverUrl,
    CefrLevel StartLevel,
    CefrLevel EndLevel,
    int ModuleCount,
    int LectureCount,
    decimal EstimatedHours);

/// <summary>"Request this language" for a language that is not available yet (FR-L10, FR-A20).</summary>
public record CreateLanguageRequest(string LanguageName, int? LanguageId, string? Note);

// ------------------------------------------------------------------ onboarding (FR-L11 .. FR-L16)

/// <summary>
/// The learning profile chosen during onboarding. Give either DurationMonths (1, 3, 6, 12) or a custom EndDate.
/// StartDate defaults to today in the learner's time zone.
/// </summary>
public record LearningPlanRequest(
    int CourseId,
    CefrLevel CurrentLevel,
    CefrLevel TargetLevel,
    LearningGoal Goal,
    string? ExamName,
    int? DurationMonths,
    DateOnly? EndDate,
    int DailyMinutes,
    StudyDays StudyDays,
    TimeOnly? PreferredStudyTime,
    FocusSkill FocusSkills,
    DateOnly? StartDate = null);

/// <summary>Changes to an existing course plan (FR-L120). The course and current level stay.</summary>
public record UpdateLearningPlanRequest(
    CefrLevel TargetLevel,
    LearningGoal Goal,
    string? ExamName,
    int? DurationMonths,
    DateOnly? EndDate,
    int DailyMinutes,
    StudyDays StudyDays,
    TimeOnly? PreferredStudyTime,
    FocusSkill FocusSkills);

public record FeasibilityResponse(
    FeasibilityStatus Status,
    string Message,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly EstimatedFinishDate,
    int TotalLectures,
    int PlannedLectures,
    int SkippedOptionalLectures,
    int PlannedMinutes,
    int AvailableMinutes,
    int StudyDaysAvailable,
    decimal LecturesPerWeek,
    DateOnly? SuggestedEndDate,
    int? SuggestedDailyMinutes);

public record EnrolResponse(LearnerCourseDto Course, FeasibilityResponse Plan);

// ------------------------------------------------------------------ learner course

public record LearnerCourseDto(
    int Id,
    int LanguageId,
    string LanguageName,
    string LanguageNativeName,
    string LanguageIsoCode,
    TextDirection Direction,
    int CourseId,
    string CourseTitle,
    CefrLevel CurrentLevel,
    CefrLevel TargetLevel,
    LearningGoal Goal,
    string? ExamName,
    DateOnly StartDate,
    DateOnly EndDate,
    int DailyMinutes,
    StudyDays StudyDays,
    TimeOnly? PreferredStudyTime,
    FocusSkill FocusSkills,
    LearnerCourseStatus Status,
    DateOnly? PausedUntil,
    int RemainingPauseDays,
    int CompletedLectures,
    int TotalLectures,
    int ProgressPercent,
    int DaysLeft);

public record RoadmapLectureDto(
    int ItemId,
    int LectureId,
    int ModuleId,
    string Title,
    string? Topic,
    int EstimatedMinutes,
    bool IsCore,
    RoadmapItemState State,
    DateOnly PlannedDate,
    DateTimeOffset? CompletedAt,
    bool IsOverdue);

public record RoadmapModuleDto(
    int ModuleId,
    string Title,
    string? Objective,
    CefrLevel Level,
    int MonthNo,
    RoadmapItemState State,
    int CompletedLectures,
    IReadOnlyList<RoadmapLectureDto> Lectures);

public record RoadmapDto(LearnerCourseDto Course, IReadOnlyList<RoadmapModuleDto> Modules);

public record CourseSwitchItem(int LearnerCourseId, string LanguageName, string LanguageIsoCode, string CourseTitle, int ProgressPercent);

/// <summary>Home screen data (FR-L20 .. FR-L24). Streak, points and the exam fill in from later phases.</summary>
public record TodayDto(
    LearnerCourseDto Course,
    IReadOnlyList<RoadmapLectureDto> NextLectures,
    int BehindCount,
    int StreakDays,
    int Points,
    DateOnly? NextExamDate,
    IReadOnlyList<CourseSwitchItem> Courses);

/// <summary>Pause a course for vacation (FR-L121, BR-11).</summary>
public record PauseCourseRequest(int Days);
