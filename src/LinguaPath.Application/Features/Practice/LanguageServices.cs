namespace LinguaPath.Application.Features.Practice;

// Azure Translator and Azure Speech sit behind these interfaces (SRS 5.1), with offline stand-ins for development and tests,
// the same way AI providers do. Keys live in configuration (user-secrets / Key Vault), never in the database.

public sealed record TranslatorResult(string Text, string From, bool Detected, string Provider, bool IsSample);

public interface ITranslator
{
    /// <param name="from">Null or empty: detect the source language (FR-L100).</param>
    Task<TranslatorResult> TranslateAsync(string text, string? from, string to, CancellationToken ct = default);
}

public sealed record WordAssessment(string Word, double Accuracy, string ErrorType);

/// <summary>Pronunciation assessment, all scores 0-100 (FR-L92).</summary>
public sealed record PronunciationResult(
    string Transcript, double Accuracy, double Fluency, double Completeness, double Overall, double Confidence,
    IReadOnlyList<WordAssessment> Words, bool IsSample);

public sealed record SpeechTranscript(string Text, double Confidence, bool IsSample);

/// <summary>Speech to text and pronunciation scoring. Audio is 16 kHz mono 16-bit PCM WAV (see <see cref="WavAudio"/>).</summary>
public interface ISpeechAssessor
{
    Task<PronunciationResult> AssessAsync(byte[] wav16kMono, string locale, string referenceText, CancellationToken ct = default);
    Task<SpeechTranscript> RecognizeAsync(byte[] wav16kMono, string locale, CancellationToken ct = default);
}

/// <summary>Raised when a language service is down or not configured; mapped to a 409 with a clear message.</summary>
public sealed class LanguageServiceException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
