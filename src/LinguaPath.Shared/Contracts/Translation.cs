namespace LinguaPath.Shared.Contracts.Translation;

// ------------------------------------------------------------------ online translation (FR-L100 .. FR-L104)

public record TranslateRequest(string Text, string? From, string To);

public record TranslationDto(
    int Id,
    string SourceText,
    string TranslatedText,
    /// <summary>Source language code; the detected one when "From" was empty (FR-L100).</summary>
    string From,
    string To,
    bool Detected,
    bool Cached,
    int RemainingToday,
    bool IsSample,
    DateTimeOffset CreatedAt);

public record SpeechTranslationDto(string Transcript, TranslationDto Translation);

public record TranslationLanguageDto(string Code, string Name, string? SpeechLocale);
