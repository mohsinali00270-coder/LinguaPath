using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>One course per language and starting level band (FR-A12, SRS 7.2).</summary>
public class Course : AuditableEntity<int>
{
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }
    public CefrLevel StartLevel { get; set; } = CefrLevel.A1;
    public CefrLevel EndLevel { get; set; } = CefrLevel.A2;
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public ContentSource Source { get; set; } = ContentSource.Manual;
    public int SortOrder { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public ICollection<CourseModule> Modules { get; set; } = new List<CourseModule>();
}

/// <summary>A roadmap module (FR-A30). MonthNo marks which monthly exam covers it (FR-A33).</summary>
public class CourseModule : AuditableEntity<int>
{
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Objective { get; set; }
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    public int SortOrder { get; set; }
    /// <summary>Roadmap month this module belongs to; the monthly exam of that month covers it.</summary>
    public int MonthNo { get; set; } = 1;
    public int? PrerequisiteModuleId { get; set; }

    public ICollection<Lecture> Lectures { get; set; } = new List<Lecture>();
}

/// <summary>
/// A lecture in the roadmap. Phase 2 uses the planning fields (title, minutes, core/optional);
/// the written body, examples, vocabulary and links arrive in Phase 3.
/// </summary>
public class Lecture : AuditableEntity<int>
{
    public int ModuleId { get; set; }
    public CourseModule Module { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public string? Objectives { get; set; }
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    public int EstimatedMinutes { get; set; } = 15;
    /// <summary>Optional lectures are dropped first when the learner's time is short (FR-A32).</summary>
    public bool IsCore { get; set; } = true;
    public int SortOrder { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public ContentSource Source { get; set; } = ContentSource.Manual;
    public string? BodyHtml { get; set; }
    public string? CoverUrl { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset? PublishedAt { get; set; }

    public ICollection<LectureExample> Examples { get; set; } = new List<LectureExample>();
    public ICollection<LectureVocabulary> Vocabulary { get; set; } = new List<LectureVocabulary>();
    public ICollection<LectureLink> Links { get; set; } = new List<LectureLink>();
}

/// <summary>"Request this language" from learners (FR-L10, FR-A20).</summary>
public class LanguageRequest
{
    public int Id { get; set; }
    public string LanguageName { get; set; } = string.Empty;
    /// <summary>Lower-case, trimmed name used to group requests for the same language.</summary>
    public string NormalizedName { get; set; } = string.Empty;
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string? Note { get; set; }
    public LanguageRequestStatus Status { get; set; } = LanguageRequestStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A learner's enrolment and learning profile (SRS 7.3, FR-L11 .. FR-L14).</summary>
public class LearnerCourse
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    /// <summary>Denormalised for the one-active-course-per-language rule.</summary>
    public int LanguageId { get; set; }

    public CefrLevel CurrentLevel { get; set; }
    public CefrLevel TargetLevel { get; set; }
    public LearningGoal Goal { get; set; }
    public string? ExamName { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int DailyMinutes { get; set; }
    public StudyDays StudyDays { get; set; } = StudyDays.Everyday;
    public TimeOnly? PreferredStudyTime { get; set; }
    public FocusSkill FocusSkills { get; set; } = FocusSkill.All;

    public LearnerCourseStatus Status { get; set; } = LearnerCourseStatus.Active;
    public DateOnly? PausedFrom { get; set; }
    public DateOnly? PausedUntil { get; set; }
    public int TotalPausedDays { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<LearnerRoadmapItem> Items { get; set; } = new List<LearnerRoadmapItem>();
}

/// <summary>One lecture in a learner's personal roadmap (SRS 7.3).</summary>
public class LearnerRoadmapItem
{
    public int Id { get; set; }
    public int LearnerCourseId { get; set; }
    public LearnerCourse LearnerCourse { get; set; } = null!;
    public int LectureId { get; set; }
    public Lecture Lecture { get; set; } = null!;

    public int SortOrder { get; set; }
    public DateOnly PlannedDate { get; set; }
    public RoadmapItemState State { get; set; } = RoadmapItemState.Locked;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TimeSpentSec { get; set; }
}
