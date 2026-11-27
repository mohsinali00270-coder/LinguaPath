namespace LinguaPath.Shared.Contracts.Content;

// ------------------------------------------------------------------ lecture reader (FR-L32 .. FR-L37)

public record LectureExampleDto(int Id, string TargetText, string? Translation, string? Note, string? AudioUrl);

public record LectureVocabularyDto(int Id, string Word, string Meaning, string? Pronunciation, string? PartOfSpeech, string? AudioUrl);

public record LectureLinkDto(int Id, string Title, string Url, LinkType LinkType);

public record LectureDto(
    int Id,
    int LearnerCourseId,
    int RoadmapItemId,
    string Title,
    string? Topic,
    string? Objectives,
    CefrLevel Level,
    int EstimatedMinutes,
    bool IsCore,
    string? CoverUrl,
    string BodyHtml,
    string ModuleTitle,
    string LanguageName,
    string LanguageIsoCode,
    /// <summary>Locale for read-aloud, e.g. "es-ES".</summary>
    string? SpeechLocale,
    TextDirection Direction,
    IReadOnlyList<LectureExampleDto> Examples,
    IReadOnlyList<LectureVocabularyDto> Vocabulary,
    IReadOnlyList<LectureLinkDto> Links,
    IReadOnlyList<PostDto> RelatedPosts,
    RoadmapItemState State,
    int TimeSpentSec,
    int MinimumSecondsToComplete,
    bool IsBookmarked,
    string? Note,
    int? MyRating,
    double? AverageRating,
    int RatingCount,
    int? NextLectureId,
    int Version,
    /// <summary>Phase 6: used to save tapped words to "My words" and translate them.</summary>
    int LanguageId = 0);

public record LectureProgressRequest(int LearnerCourseId, int Seconds);

public record CompleteLectureRequest(int LearnerCourseId, int Seconds);

public record CompleteLectureResponse(
    int? NextLectureId,
    string? NextLectureTitle,
    int ProgressPercent,
    int CompletedLectures,
    int TotalLectures,
    bool AllLecturesDone,
    /// <summary>Phase 7: points for this lecture (0 when already given or over the daily cap) and the streak after it.</summary>
    int PointsEarned = 0,
    int Streak = 0,
    IReadOnlyList<string>? NewBadges = null);

public record SaveNoteRequest(string? Text);

public record RateLectureRequest(int Stars, string? Comment);

public record ReportContentRequest(string Message);

public record BookmarkDto(int LectureId, string Title, string? Topic, string LanguageName, string CourseTitle, DateTimeOffset CreatedAt);

// ------------------------------------------------------------------ posts (FR-L40 .. FR-L42)

public record PostDto(
    int Id,
    int LanguageId,
    string LanguageName,
    PostType PostType,
    string Title,
    string? Body,
    string? Url,
    string? ImageUrl,
    CefrLevel? Level,
    IReadOnlyList<string> Tags,
    DateTimeOffset PublishedAt,
    bool IsPinned,
    int? LectureId,
    string? LectureTitle,
    int Likes,
    bool LikedByMe,
    bool SavedByMe);

public record PostInteractionResponse(int PostId, int Likes, bool LikedByMe, bool SavedByMe);
