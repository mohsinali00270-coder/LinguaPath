using System.ComponentModel.DataAnnotations;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Admin;

// ------------------------------------------------------------------ lectures (FR-A40 .. FR-A47)

public record LectureListItem(
    int Id, string Title, string? Topic, int CourseId, string CourseTitle, string LanguageName, string ModuleTitle, CefrLevel Level,
    ContentStatus Status, ContentSource Source, int Version, bool HasBody, DateTimeOffset? UpdatedAt);

public record LectureFilter(int? CourseId, ContentStatus? Status, string? Search, int Page = 1, int PageSize = 30);

public class ExampleInput
{
    [Required, StringLength(500)] public string TargetText { get; set; } = string.Empty;
    [StringLength(500)] public string? Translation { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class VocabularyInput
{
    [Required, StringLength(100)] public string Word { get; set; } = string.Empty;
    [Required, StringLength(300)] public string Meaning { get; set; } = string.Empty;
    [StringLength(150)] public string? Pronunciation { get; set; }
    [StringLength(30)] public string? PartOfSpeech { get; set; }
}

public class LinkInput
{
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [Required, StringLength(1000), Url] public string Url { get; set; } = string.Empty;
    public LinkType LinkType { get; set; } = LinkType.Website;
}

/// <summary>Everything the lecture editor saves (FR-A40, FR-A41).</summary>
public class LectureContentInput
{
    public int Id { get; set; }

    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(150)] public string? Topic { get; set; }
    [StringLength(1000)] public string? Objectives { get; set; }
    [Display(Name = "CEFR level")] public CefrLevel Level { get; set; }
    [Range(5, 180), Display(Name = "Estimated minutes")] public int EstimatedMinutes { get; set; } = 15;
    [Display(Name = "Core lecture")] public bool IsCore { get; set; } = true;
    [StringLength(500), Url, Display(Name = "Cover image URL")] public string? CoverUrl { get; set; }
    [Display(Name = "Lesson text")] public string? BodyHtml { get; set; }
    [StringLength(300), Display(Name = "What changed? (saved with the version)")] public string? ChangeNote { get; set; }

    public List<ExampleInput> Examples { get; set; } = [];
    public List<VocabularyInput> Vocabulary { get; set; } = [];
    public List<LinkInput> Links { get; set; } = [];
}

public record LectureEditorModel(
    LectureContentInput Input, int ModuleId, string ModuleTitle, int CourseId, string CourseTitle, string LanguageName,
    string LanguageIsoCode, TextDirection Direction, ContentStatus Status, ContentSource Source, int Version,
    string? LastReviewComment, ReviewStatus? LastReviewStatus);

public record LectureVersionItem(int Id, int Version, string Title, string? Note, string? ChangedBy, DateTimeOffset ChangedAt, int BodyLength);

public record LectureVersionView(int Id, int LectureId, int Version, string Title, string? BodyHtml, string? Note, string? ChangedBy,
    DateTimeOffset ChangedAt, string CurrentTitle, string? CurrentBodyHtml, int CurrentVersion, TextDirection Direction);

// ------------------------------------------------------------------ posts (FR-A50, FR-A51)

public record PostListItem(int Id, string Title, PostType PostType, string LanguageName, CefrLevel? Level, ContentStatus Status,
    ContentSource Source, DateTimeOffset? PublishAt, bool IsPinned, int Likes);

public class PostInput
{
    public int Id { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    [Display(Name = "Type")] public PostType PostType { get; set; } = PostType.Tip;
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(4000)] public string? Body { get; set; }
    [StringLength(1000), Url, Display(Name = "Link (article or video)")] public string? Url { get; set; }
    [StringLength(500), Url, Display(Name = "Image URL")] public string? ImageUrl { get; set; }
    [Display(Name = "Level (leave empty for everyone)")] public CefrLevel? Level { get; set; }
    [StringLength(300), Display(Name = "Tags (comma-separated)")] public string? Tags { get; set; }
    [Display(Name = "Show at the end of lecture (optional)")] public int? LectureId { get; set; }
    [Display(Name = "Publish at, UTC (empty = as soon as it is published)")] public DateTime? PublishAtUtc { get; set; }
    [Display(Name = "Pin to the top of the feed")] public bool IsPinned { get; set; }
}

// ------------------------------------------------------------------ review queue (FR-A70 .. FR-A72) and reports (FR-L37)

public record ReviewQueueItem(int Id, ReviewEntityType EntityType, int EntityId, string Title, string Context, ContentSource Source,
    string? SubmittedBy, DateTimeOffset SubmittedAt, string? EditUrlHint);

public record ReviewHistoryItem(ReviewStatus Status, string? SubmittedBy, DateTimeOffset SubmittedAt, string? Reviewer,
    DateTimeOffset? ReviewedAt, string? Comment);

public record ContentReportItem(int Id, ReportEntityType EntityType, int EntityId, string Title, string Message, string Reporter,
    ReportStatus Status, DateTimeOffset CreatedAt, string? ResolutionNote);
