using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>Example sentence in a lecture: target text, translation, optional audio (FR-A40, SRS 7.2).</summary>
public class LectureExample
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public Lecture Lecture { get; set; } = null!;
    public string TargetText { get; set; } = string.Empty;
    public string? Translation { get; set; }
    public string? Note { get; set; }
    public string? AudioUrl { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Vocabulary word taught in a lecture (FR-A40, FR-L33).</summary>
public class LectureVocabulary
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public Lecture Lecture { get; set; } = null!;
    public string Word { get; set; } = string.Empty;
    public string Meaning { get; set; } = string.Empty;
    public string? Pronunciation { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? AudioUrl { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>External link attached to a lecture (FR-A41).</summary>
public class LectureLink
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public Lecture Lecture { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public LinkType LinkType { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Snapshot of a lecture each time its content is saved (FR-A46).</summary>
public class LectureVersion
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? BodyHtml { get; set; }
    /// <summary>Examples, vocabulary and links as JSON, so a restore brings them back too.</summary>
    public string? ExtrasJson { get; set; }
    public string? Note { get; set; }
    public Guid? ChangedBy { get; set; }
    public string? ChangedByEmail { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>Feed post: tip, article/video link, cultural note, word of the day (FR-A50, SRS 7.2).</summary>
public class Post : AuditableEntity<int>
{
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public int? LectureId { get; set; }
    public Lecture? Lecture { get; set; }

    public PostType PostType { get; set; } = PostType.Tip;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? Url { get; set; }
    public string? ImageUrl { get; set; }
    public CefrLevel? Level { get; set; }
    /// <summary>Comma-separated tags.</summary>
    public string? Tags { get; set; }

    /// <summary>Scheduled publish time (FR-A51). Visible to learners when Published and PublishAt has passed.</summary>
    public DateTimeOffset? PublishAt { get; set; }
    public bool IsPinned { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    /// <summary>When learners of this language were told about it (FR-L110 "new post in my language").</summary>
    public DateTimeOffset? NotifiedAt { get; set; }
    public ContentSource Source { get; set; } = ContentSource.Manual;
}

/// <summary>A learner's like / save of a post (FR-L41).</summary>
public class PostInteraction
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public Guid UserId { get; set; }
    public bool Liked { get; set; }
    public bool Saved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Review request for a lecture or post (FR-A70 .. FR-A72, SRS 7.6 ReviewItems).</summary>
public class ReviewItem
{
    public int Id { get; set; }
    public ReviewEntityType EntityType { get; set; }
    public int EntityId { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;
    public Guid? SubmittedBy { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public Guid? ReviewerId { get; set; }
    public string? ReviewerEmail { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Learner error report about content (FR-L37, SRS 7.6 ContentReports).</summary>
public class ContentReport
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public ReportEntityType EntityType { get; set; }
    public int EntityId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}

/// <summary>Bookmark (FR-L35).</summary>
public class LectureBookmark
{
    public Guid UserId { get; set; }
    public int LectureId { get; set; }
    public Lecture Lecture { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Personal note on a lecture (FR-L35).</summary>
public class LectureNote
{
    public Guid UserId { get; set; }
    public int LectureId { get; set; }
    public string NoteText { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>1–5 star rating (FR-L37).</summary>
public class LectureRating
{
    public Guid UserId { get; set; }
    public int LectureId { get; set; }
    public int Stars { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
