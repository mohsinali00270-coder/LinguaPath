using System.ComponentModel.DataAnnotations;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Admin;

// ------------------------------------------------------------------ question bank (FR-A60 .. FR-A66)

public record QuestionFilter(
    int? LanguageId, int? CourseId, int? LectureId, CefrLevel? Level, Skill? Skill, QuestionType? Type, int? Difficulty,
    ContentStatus? Status, string? Search, bool FlaggedOnly = false, int Page = 1, int PageSize = 30);

public record QuestionListItem(
    int Id, string Stem, QuestionType Type, Skill Skill, CefrLevel Level, int Difficulty, string LanguageName, string? LectureTitle,
    ContentStatus Status, ContentSource Source, int TimesAnswered, int? PercentCorrect, int? AverageSeconds, string? Flag);

public class OptionInput
{
    [StringLength(500)] public string? Text { get; set; }
    public bool IsCorrect { get; set; }
    [StringLength(300)] public string? MatchKey { get; set; }
}

public class QuestionInput
{
    public int Id { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    [Display(Name = "Lecture (optional)")] public int? LectureId { get; set; }
    [Display(Name = "Type")] public QuestionType Type { get; set; } = QuestionType.MultipleChoice;
    public Skill Skill { get; set; } = Skill.Vocabulary;
    [Display(Name = "CEFR level")] public CefrLevel Level { get; set; } = CefrLevel.A1;
    [Range(1, 5)] public int Difficulty { get; set; } = 2;
    [Range(1, 20)] public int Points { get; set; } = 1;

    [Required, StringLength(1000), Display(Name = "Question")] public string Stem { get; set; } = string.Empty;
    [StringLength(500), Display(Name = "Sentence (to translate, listen to or say)")] public string? Prompt { get; set; }
    [StringLength(500), Url, Display(Name = "Audio or image URL")] public string? MediaUrl { get; set; }
    [StringLength(1000)] public string? Explanation { get; set; }

    public List<OptionInput> Options { get; set; } = [];
}

public record QuestionEditorModel(
    QuestionInput Input, ContentStatus Status, ContentSource Source, int Version, int TimesAnswered, int? PercentCorrect,
    string? LastReviewComment, ReviewStatus? LastReviewStatus, string? SourceText = null);

public record ImportResult(int Created, int Updated, IReadOnlyList<string> Errors);

// ------------------------------------------------------------------ monthly exams and quiz rules (FR-A63, FR-A64)

public record ExamListItem(
    int Id, int CourseId, string CourseTitle, string LanguageName, int? MonthNo, string Title, int TimeLimitMinutes, int? PassMark,
    ContentStatus Status, int Sections, int Questions, int Attempts);

public class SectionInput
{
    [Required, StringLength(150)] public string Title { get; set; } = "Section";
    public Skill? Skill { get; set; }
    public QuestionType? QuestionType { get; set; }
    [Range(1, 100)] public int QuestionCount { get; set; } = 5;
    public SelectionMode SelectionMode { get; set; } = SelectionMode.Random;
    /// <summary>Fixed sections: chosen question ids.</summary>
    public List<int> QuestionIds { get; set; } = [];
}

public class ExamInput
{
    public int Id { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Choose a course."), Display(Name = "Course")] public int CourseId { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(1000)] public string? Instructions { get; set; }
    [Range(1, 24), Display(Name = "Roadmap month")] public int MonthNo { get; set; } = 1;
    [Range(5, 240), Display(Name = "Time limit (minutes)")] public int TimeLimitMinutes { get; set; } = 45;
    [Range(1, 100), Display(Name = "Pass mark % (empty = setting)")] public int? PassMark { get; set; }
    public List<SectionInput> Sections { get; set; } = [];
}

public record EligibleQuestion(int Id, string Stem, QuestionType Type, Skill Skill, CefrLevel Level, bool InMonth);

public record ExamEditorModel(
    ExamInput Input, ContentStatus Status, string CourseTitle, string LanguageName, int Attempts, int DefaultPassMark,
    IReadOnlyList<EligibleQuestion> Eligible, IReadOnlyList<string> Warnings);

public class DailyQuizRuleInput
{
    [Range(0, 20), Display(Name = "From the last 2 days")] public int FromRecent { get; set; } = 3;
    [Range(0, 20), Display(Name = "From the last 7 days")] public int FromWeek { get; set; } = 3;
    [Range(0, 20), Display(Name = "From the review list")] public int FromReview { get; set; } = 2;
    [Range(1, 10), Display(Name = "Placement questions per level")] public int PlacementPerLevel { get; set; } = 2;
}

// ------------------------------------------------------------------ grading and marks (FR-A80, FR-A81, BR-14)

public record GradingQueueItem(
    int AnswerId, int AttemptId, string LearnerName, string LearnerEmail, string CourseTitle, string AttemptTitle, QuizType QuizType,
    QuestionType QuestionType, string Stem, string? Prompt, string? AnswerText, string? AudioUrl, int MaxScore, DateTimeOffset? SubmittedAt,
    decimal? AiScore = null, double? AiConfidence = null, string? AiFeedback = null);

public record AttemptListItem(
    int Id, string Title, QuizType Type, string CourseTitle, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt, AttemptStatus Status,
    double Score, int MaxScore, int Percentage, bool IsPassed, bool IsPractice, int BackgroundEvents);

public record MarkOverrideItem(decimal OldScore, decimal NewScore, string Reason, string? ChangedBy, DateTimeOffset ChangedAt);

public record AttemptAnswerItem(
    int AnswerId, int Number, string? Section, QuestionType Type, Skill Skill, string Stem, string? Prompt, string YourAnswer,
    string? AudioUrl, string CorrectAnswer, bool? IsCorrect, decimal Score, int MaxScore, bool NeedsReview, string? Feedback,
    string? ReviewedBy, IReadOnlyList<MarkOverrideItem> Overrides);

public record AttemptDetailModel(
    AttemptListItem Attempt, Guid UserId, string LearnerName, string LearnerEmail, IReadOnlyList<AttemptAnswerItem> Answers);
