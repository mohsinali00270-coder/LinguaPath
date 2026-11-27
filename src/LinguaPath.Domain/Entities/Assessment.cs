using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>A question in the bank (SRS 7.4, FR-A60, FR-A61). The meaning of the options depends on <see cref="Type"/>.</summary>
public class Question : AuditableEntity<int>
{
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int? ModuleId { get; set; }
    public CourseModule? Module { get; set; }
    public int? LectureId { get; set; }
    public Lecture? Lecture { get; set; }

    public QuestionType Type { get; set; } = QuestionType.MultipleChoice;
    public Skill Skill { get; set; } = Skill.Vocabulary;
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    /// <summary>1 (easy) .. 5 (hard).</summary>
    public int Difficulty { get; set; } = 2;
    public string Stem { get; set; } = string.Empty;
    /// <summary>Sentence to translate, to listen to, or to say aloud (SourceSentence in the SRS).</summary>
    public string? Prompt { get; set; }
    public string? MediaUrl { get; set; }
    public string? Explanation { get; set; }
    /// <summary>AI questions: the lecture sentence the question is based on (SRS 5.3 grounding).</summary>
    public string? SourceText { get; set; }
    public int Points { get; set; } = 1;

    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public ContentSource Source { get; set; } = ContentSource.Manual;
    public int Version { get; set; } = 1;

    // Analytics counters (FR-A66), updated when answers are graded.
    public int TimesAnswered { get; set; }
    public int TimesCorrect { get; set; }
    public long TotalTimeSec { get; set; }

    public ICollection<QuestionOption> Options { get; set; } = new List<QuestionOption>();
}

public class QuestionOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public string Text { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
    /// <summary>Right-hand item for matching questions.</summary>
    public string? MatchKey { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A monthly exam or other configured quiz for a course (FR-A64). Daily quizzes are built from rules, not stored here.</summary>
public class Quiz : AuditableEntity<int>
{
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public QuizType Type { get; set; } = QuizType.Monthly;
    public string Title { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public int? MonthNo { get; set; }
    public int TimeLimitMinutes { get; set; } = 45;
    /// <summary>Pass mark in %; null uses the Assessment.MonthlyExamPassMark setting.</summary>
    public int? PassMark { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    public ICollection<QuizSection> Sections { get; set; } = new List<QuizSection>();
}

public class QuizSection
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    /// <summary>Random sections draw from published questions of this skill (any skill when null).</summary>
    public Skill? Skill { get; set; }
    public QuestionType? QuestionType { get; set; }
    public int QuestionCount { get; set; } = 5;
    public SelectionMode SelectionMode { get; set; } = SelectionMode.Random;
    public int SortOrder { get; set; }

    public ICollection<QuizQuestion> Questions { get; set; } = new List<QuizQuestion>();
}

/// <summary>A fixed question in a section.</summary>
public class QuizQuestion
{
    public int SectionId { get; set; }
    public QuizSection Section { get; set; } = null!;
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public int SortOrder { get; set; }
}

/// <summary>One go at a daily quiz, monthly exam or placement test (SRS 7.4).</summary>
public class QuizAttempt
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int? LearnerCourseId { get; set; }
    public LearnerCourse? LearnerCourse { get; set; }
    public int? QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    public QuizType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? MonthNo { get; set; }
    /// <summary>Daily quiz: the learner's local date it belongs to.</summary>
    public DateOnly? ForDate { get; set; }
    public int AttemptNo { get; set; } = 1;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>Exams: start + time limit. Daily quiz: the learner's next midnight.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public decimal Score { get; set; }
    public int MaxScore { get; set; }
    public int Percentage { get; set; }
    public int PassMark { get; set; }
    public bool IsPassed { get; set; }
    /// <summary>Late daily quizzes are practice and don't count toward marks (FR-L53).</summary>
    public bool IsPractice { get; set; }
    public AttemptStatus Status { get; set; } = AttemptStatus.InProgress;
    /// <summary>App went to the background during an exam (FR-L65).</summary>
    public int BackgroundEvents { get; set; }
    public CefrLevel? SuggestedLevel { get; set; }

    public ICollection<AttemptAnswer> Answers { get; set; } = new List<AttemptAnswer>();
}

public class AttemptAnswer
{
    public int Id { get; set; }
    public int AttemptId { get; set; }
    public QuizAttempt Attempt { get; set; } = null!;
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public int SortOrder { get; set; }
    public string? SectionTitle { get; set; }
    /// <summary>Shuffled option ids for this attempt, comma-separated (FR-L65).</summary>
    public string? OptionOrder { get; set; }

    public string? AnswerText { get; set; }
    /// <summary>Comma-separated option ids.</summary>
    public string? SelectedOptionIds { get; set; }
    /// <summary>Matching answers as "optionId=target" lines.</summary>
    public string? MatchAnswer { get; set; }
    public string? AudioUrl { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public int TimeSpentSec { get; set; }

    public bool? IsCorrect { get; set; }
    public decimal Score { get; set; }
    public int MaxScore { get; set; }
    /// <summary>Writing and speaking answers wait for a reviewer (FR-A81).</summary>
    public bool NeedsReview { get; set; }
    public string? Feedback { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // AI pre-grading of writing (FR-L63): a suggestion for the reviewer, or the grade itself when confident.
    public decimal? AiScore { get; set; }
    public string? AiFeedback { get; set; }
    public double? AiConfidence { get; set; }
}

/// <summary>Audited change of a mark (FR-A80, BR-14).</summary>
public class MarkOverride
{
    public int Id { get; set; }
    public int AttemptAnswerId { get; set; }
    public AttemptAnswer AttemptAnswer { get; set; } = null!;
    public decimal OldScore { get; set; }
    public decimal NewScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ChangedBy { get; set; }
    public string? ChangedByEmail { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>A question the learner got wrong, scheduled for review with simple spaced repetition (FR-L54).</summary>
public class ReviewCard
{
    public int Id { get; set; }
    public int LearnerCourseId { get; set; }
    public LearnerCourse LearnerCourse { get; set; } = null!;
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    /// <summary>Leitner box 1..5; higher boxes come back less often.</summary>
    public int Box { get; set; } = 1;
    public DateOnly DueDate { get; set; }
    public int Lapses { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
