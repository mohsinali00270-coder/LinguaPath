namespace LinguaPath.Shared.Contracts.Practice;

// ------------------------------------------------------------------ practice (FR-L90 .. FR-L93)

public record ReadingQuestionDto(int Index, string Question, IReadOnlyList<string> Options);

public record PracticeItemDto(
    int Id,
    PracticeType Type,
    int LanguageId,
    string LanguageName,
    CefrLevel Level,
    string Title,
    /// <summary>Passage (read), task (write) or sentence (speak, listen). Hidden for listen until checked.</summary>
    string? Text,
    string? Translation,
    IReadOnlyList<ReadingQuestionDto> Questions,
    string? SpeechLocale,
    string LanguageIsoCode,
    TextDirection Direction);

public record ReadAnswersRequest(List<int> Answers);

public record ReadQuestionResultDto(int Index, bool Correct, int CorrectIndex, string? Explanation);

public record ReadResultDto(int Score, int Correct, int Total, IReadOnlyList<ReadQuestionResultDto> Results);

public record WriteCheckRequest(int LanguageId, int? ItemId, string? Prompt, string Text);

public record CorrectionDto(string Original, string Corrected, string? Explanation);

public record WriteCheckDto(int Score, string Feedback, IReadOnlyList<CorrectionDto> Corrections, int RemainingToday, bool IsSample);

public record WordScoreDto(string Word, int Accuracy, string ErrorType);

/// <summary>Pronunciation assessment (FR-L92): 0-100 scores and per-word accuracy; mispronounced words are flagged.</summary>
public record SpeakResultDto(
    int Score, int Accuracy, int Fluency, int Completeness, string Transcript, IReadOnlyList<WordScoreDto> Words, bool IsSample);

public record ListenCheckRequest(string Text);

public record DiffWordDto(string Text, string? Expected, DiffKind Kind);

public record ListenResultDto(int Score, string Sentence, IReadOnlyList<DiffWordDto> Words);

public record ChatTurnDto(string Role, string Text);

public record ConversationRequest(int LanguageId, string Topic, List<ChatTurnDto> History, string Message);

public record ConversationReplyDto(string Reply, string? Correction, string? Explanation, int RemainingToday, bool IsSample);

public record PracticeTypeSummaryDto(PracticeType Type, int Done, int? AverageScore, int Available);

public record PracticeSummaryDto(
    int LanguageId, string LanguageName, CefrLevel Level, IReadOnlyList<PracticeTypeSummaryDto> Types, int WordsDue, int TranslationsLeftToday,
    IReadOnlyList<string> Topics);

public record PracticeHistoryDto(int Id, PracticeType Type, string Title, int? Score, DateTimeOffset CreatedAt);
