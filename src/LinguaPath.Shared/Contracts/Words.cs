namespace LinguaPath.Shared.Contracts.Words;

// ------------------------------------------------------------------ My words (FR-L94, FR-L103)

public record UserWordDto(
    int Id, int LanguageId, string LanguageName, string Word, string Meaning, string? Example,
    int Repetitions, int IntervalDays, double EaseFactor, DateTimeOffset NextReviewAt, bool IsDue);

public record SaveWordRequest(int LanguageId, string Word, string Meaning, string? Example);

public record ReviewWordRequest(RecallGrade Grade);

public record WordsSummaryDto(int Total, int Due, int LearnedThisWeek);
