using System.ComponentModel.DataAnnotations;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Admin;

// ------------------------------------------------------------------ languages (FR-A10, FR-A11)

public record LanguageListItem(
    int Id, string Name, string NativeName, string IsoCode, TextDirection Direction, LanguageStatus Status,
    int Courses, int PublishedCourses, int ActiveLearners, int PendingRequests, int SortOrder);

public class LanguageInput
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(80)]
    [Display(Name = "Native name")]
    public string NativeName { get; set; } = string.Empty;

    [Required, RegularExpression("^[a-z]{2,3}(-[A-Za-z]{2,4})?$", ErrorMessage = "Use an ISO 639 code such as es, ar or zh-Hans.")]
    [Display(Name = "ISO code")]
    public string IsoCode { get; set; } = string.Empty;

    [StringLength(500), Url]
    [Display(Name = "Flag image URL")]
    public string? FlagUrl { get; set; }

    [Display(Name = "Script direction")]
    public TextDirection Direction { get; set; } = TextDirection.Ltr;

    [StringLength(100)]
    [Display(Name = "Text-to-speech voice")]
    public string? TtsVoice { get; set; }

    [StringLength(20)]
    [Display(Name = "Speech-to-text locale")]
    public string? SttLocale { get; set; }

    [Display(Name = "Lowest level offered")]
    public CefrLevel MinLevel { get; set; } = CefrLevel.A1;

    [Display(Name = "Highest level offered")]
    public CefrLevel MaxLevel { get; set; } = CefrLevel.C1;

    public LanguageStatus Status { get; set; } = LanguageStatus.Draft;

    [Range(0, 1000)]
    [Display(Name = "Sort order")]
    public int SortOrder { get; set; }
}

// ------------------------------------------------------------------ language requests (FR-A20)

public record LanguageRequestGroup(
    string NormalizedName, string LanguageName, int? LanguageId, LanguageStatus? LanguageStatus,
    int Count, int PendingCount, LanguageRequestStatus Status, DateTimeOffset FirstRequestedAt, DateTimeOffset LastRequestedAt);

// ------------------------------------------------------------------ courses and roadmap (FR-A12, FR-A30 .. FR-A33)

public record CourseListItem(
    int Id, int LanguageId, string LanguageName, string Title, CefrLevel StartLevel, CefrLevel EndLevel, ContentStatus Status,
    int Modules, int Lectures, decimal Hours, int Learners);

public class CourseInput
{
    public int Id { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a language.")]
    [Display(Name = "Language")]
    public int LanguageId { get; set; }

    [Required, StringLength(150)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(500), Url]
    [Display(Name = "Cover image URL")]
    public string? CoverUrl { get; set; }

    [Display(Name = "Start level")]
    public CefrLevel StartLevel { get; set; } = CefrLevel.A1;

    [Display(Name = "End level")]
    public CefrLevel EndLevel { get; set; } = CefrLevel.A2;

    [Range(0, 1000)]
    [Display(Name = "Sort order")]
    public int SortOrder { get; set; }
}

public record CourseBuilderModel(
    int Id, int LanguageId, string LanguageName, LanguageStatus LanguageStatus, string Title, string? Description,
    CefrLevel StartLevel, CefrLevel EndLevel, ContentStatus Status, int Learners,
    IReadOnlyList<BuilderModule> Modules)
{
    public int LectureCount => Modules.Sum(m => m.Lectures.Count);
    public int CoreMinutes => Modules.Sum(m => m.Lectures.Where(l => l.IsCore).Sum(l => l.EstimatedMinutes));
    public int TotalMinutes => Modules.Sum(m => m.Lectures.Sum(l => l.EstimatedMinutes));
}

public record BuilderModule(int Id, string Title, string? Objective, CefrLevel Level, int MonthNo, int SortOrder,
    string? PrerequisiteTitle, IReadOnlyList<BuilderLecture> Lectures);

public record BuilderLecture(int Id, string Title, string? Topic, CefrLevel Level, int EstimatedMinutes, bool IsCore,
    ContentStatus Status, ContentSource Source, int SortOrder);

public class ModuleInput
{
    public int Id { get; set; }
    public int CourseId { get; set; }

    [Required, StringLength(150)]
    public string Title { get; set; } = string.Empty;

    [StringLength(500)]
    [Display(Name = "Learning objective")]
    public string? Objective { get; set; }

    [Display(Name = "CEFR level")]
    public CefrLevel Level { get; set; } = CefrLevel.A1;

    [Range(1, 24)]
    [Display(Name = "Roadmap month (monthly exam checkpoint)")]
    public int MonthNo { get; set; } = 1;

    [Display(Name = "Prerequisite module")]
    public int? PrerequisiteModuleId { get; set; }
}

public class LectureInput
{
    public int Id { get; set; }
    public int ModuleId { get; set; }

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [StringLength(150)]
    public string? Topic { get; set; }

    [StringLength(1000)]
    [Display(Name = "Objectives")]
    public string? Objectives { get; set; }

    [Display(Name = "CEFR level")]
    public CefrLevel Level { get; set; } = CefrLevel.A1;

    [Range(5, 180)]
    [Display(Name = "Estimated minutes")]
    public int EstimatedMinutes { get; set; } = 15;

    [Display(Name = "Core lecture (optional lectures are dropped first when a learner's time is short)")]
    public bool IsCore { get; set; } = true;
}

public record ReorderInput(int ParentId, int[] Ids);
