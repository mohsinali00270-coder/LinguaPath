namespace LinguaPath.Shared.Contracts.Assessment;

// ------------------------------------------------------------------ taking a quiz or exam (FR-L50 .. FR-L65, FR-L11)

public record QuestionOptionDto(int Id, string Text);

public record MatchPairDto(int OptionId, string Target);

/// <summary>What the learner has answered so far (exams auto-save; resume after a crash, FR-L62).</summary>
public record SavedAnswerDto(string? Text, IReadOnlyList<int> OptionIds, IReadOnlyList<MatchPairDto> Pairs, string? AudioUrl);

public record QuizQuestionDto(
    int QuestionId,
    int Number,
    string? Section,
    QuestionType Type,
    Skill Skill,
    string Stem,
    /// <summary>Sentence to translate, to listen to, or to say aloud.</summary>
    string? Prompt,
    string? MediaUrl,
    int Points,
    /// <summary>Choices (shuffled), or the left items of a matching question.</summary>
    IReadOnlyList<QuestionOptionDto> Options,
    /// <summary>Right-hand items of a matching question (shuffled).</summary>
    IReadOnlyList<string> MatchTargets,
    /// <summary>Shuffled words for a reorder question.</summary>
    IReadOnlyList<string> WordTiles,
    bool AllowsMultiple,
    SavedAnswerDto? Answer,
    /// <summary>Daily quiz only: the result once this question was checked.</summary>
    AnswerFeedbackDto? Feedback);

public record AttemptDto(
    int Id,
    QuizType Type,
    string Title,
    AttemptStatus Status,
    bool IsPractice,
    /// <summary>Daily quiz and placement show right/wrong after each question; exams don't (FR-L52).</summary>
    bool InstantFeedback,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExpiresAt,
    int? SecondsLeft,
    IReadOnlyList<QuizQuestionDto> Questions,
    string? SpeechLocale,
    string LanguageIsoCode,
    TextDirection Direction);

public record SaveAnswerRequest(int QuestionId, string? Text, List<int>? OptionIds, List<MatchPairDto>? Pairs, int? TimeSpentSec);

public record AnswerFeedbackDto(
    int QuestionId,
    bool? IsCorrect,
    double Score,
    int MaxScore,
    string? CorrectAnswer,
    string? Explanation,
    bool NeedsReview);

public record AttemptEventRequest(string Kind);

public record SkillScoreDto(Skill Skill, int Percentage, int Questions);

public record AttemptResultDto(
    int AttemptId,
    QuizType Type,
    string Title,
    AttemptStatus Status,
    double Score,
    int MaxScore,
    int Percentage,
    bool IsPassed,
    int PassMark,
    bool IsPractice,
    int Correct,
    int Total,
    int PendingReview,
    IReadOnlyList<SkillScoreDto> Skills,
    /// <summary>Placement test only (FR-L11).</summary>
    CefrLevel? SuggestedLevel,
    DateTimeOffset? SubmittedAt);

public record ReviewedAnswerDto(
    int Number,
    int QuestionId,
    QuestionType Type,
    Skill Skill,
    string Stem,
    string? Prompt,
    string YourAnswer,
    string CorrectAnswer,
    string? Explanation,
    bool? IsCorrect,
    double Score,
    int MaxScore,
    string? Feedback,
    bool NeedsReview);

/// <summary>Exam review after the attempt closes (FR-L72).</summary>
public record AttemptReviewDto(AttemptResultDto Result, IReadOnlyList<ReviewedAnswerDto> Answers);

// ------------------------------------------------------------------ daily quiz and exams lists

public record DailyQuizDto(
    int LearnerCourseId,
    DateOnly Date,
    DailyQuizState State,
    int? AttemptId,
    int QuestionCount,
    AttemptResultDto? Result,
    string Message,
    int ReviewItemsDue);

public record ExamSummaryDto(
    int QuizId,
    int MonthNo,
    string Title,
    int TimeLimitMinutes,
    int PassMark,
    int QuestionCount,
    ExamAvailability Availability,
    string StatusText,
    int LecturesDone,
    int LecturesTotal,
    DateOnly? ScheduledDate,
    int AttemptsUsed,
    int MaxAttempts,
    DateTimeOffset? NextRetakeAt,
    int? BestPercentage,
    int? ActiveAttemptId,
    int? LastAttemptId);

// ------------------------------------------------------------------ marks (FR-L70 .. FR-L73)

public record MarkRowDto(
    int AttemptId,
    QuizType Type,
    string Title,
    DateTimeOffset Date,
    double Score,
    int MaxScore,
    int Percentage,
    bool IsPassed,
    AttemptStatus Status,
    bool IsPractice,
    /// <summary>False for practice attempts and for exam attempts that aren't the best one.</summary>
    bool Counts);

public record TrendPointDto(DateOnly Date, int Percentage, QuizType Type);

public record MarksDto(
    int LearnerCourseId,
    string CourseTitle,
    int? CourseGrade,
    int? DailyAverage,
    int? ExamAverage,
    int QuizWeight,
    int ExamWeight,
    IReadOnlyList<MarkRowDto> Rows,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<SkillScoreDto> Skills);
