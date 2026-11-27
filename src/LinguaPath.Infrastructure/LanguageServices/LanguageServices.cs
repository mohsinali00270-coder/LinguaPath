using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LinguaPath.Application.Features.Practice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.LanguageServices;

/// <summary>
/// "LanguageServices" configuration: Azure Translator and Azure Speech (SRS 5.1). Keys come from user-secrets / Key Vault.
/// Without keys, development can use offline stand-ins (OfflineWhenNoKey); production then answers "not configured".
/// </summary>
public sealed class LanguageServicesOptions
{
    public const string Section = "LanguageServices";

    public bool ForceOffline { get; set; }
    public bool OfflineWhenNoKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public AzureTranslatorOptions Translator { get; set; } = new();
    public AzureSpeechOptions Speech { get; set; } = new();
}

public sealed class AzureTranslatorOptions
{
    public string Endpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";
    /// <summary>Resource region, e.g. "westeurope"; empty for a global resource.</summary>
    public string? Region { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class AzureSpeechOptions
{
    /// <summary>Speech resource region, e.g. "westeurope".</summary>
    public string? Region { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Optional custom endpoint; default https://{region}.stt.speech.microsoft.com.</summary>
    public string? Endpoint { get; set; }
}

// ===================================================================== selectors

internal sealed class TranslatorSelector(IOptions<LanguageServicesOptions> options, IHttpClientFactory http, ILogger<TranslatorSelector> logger)
    : ITranslator
{
    public Task<TranslatorResult> TranslateAsync(string text, string? from, string to, CancellationToken ct = default)
    {
        var o = options.Value;
        if (o.ForceOffline) return OfflineTranslator.TranslateAsync(text, from, to);
        if (string.IsNullOrWhiteSpace(o.Translator.ApiKey))
        {
            if (o.OfflineWhenNoKey) return OfflineTranslator.TranslateAsync(text, from, to);
            throw new LanguageServiceException("translator_not_configured", "Translation isn't set up yet. Please try again later.");
        }
        var client = http.CreateClient("language");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 120));
        return new AzureTranslator(client, o.Translator, logger).TranslateAsync(text, from, to, ct);
    }
}

internal sealed class SpeechSelector(IOptions<LanguageServicesOptions> options, IHttpClientFactory http, ILogger<SpeechSelector> logger)
    : ISpeechAssessor
{
    public Task<PronunciationResult> AssessAsync(byte[] wav16kMono, string locale, string referenceText, CancellationToken ct = default) =>
        Pick() is { } azure ? azure.AssessAsync(wav16kMono, locale, referenceText, ct) : Task.FromResult(OfflineSpeech.Assess(wav16kMono, referenceText));

    public Task<SpeechTranscript> RecognizeAsync(byte[] wav16kMono, string locale, CancellationToken ct = default) =>
        Pick() is { } azure ? azure.RecognizeAsync(wav16kMono, locale, ct) : Task.FromResult(OfflineSpeech.Recognize(wav16kMono, locale));

    private AzureSpeech? Pick()
    {
        var o = options.Value;
        if (o.ForceOffline) return null;
        if (string.IsNullOrWhiteSpace(o.Speech.ApiKey) || (string.IsNullOrWhiteSpace(o.Speech.Region) && string.IsNullOrWhiteSpace(o.Speech.Endpoint)))
        {
            if (o.OfflineWhenNoKey) return null;
            throw new LanguageServiceException("speech_not_configured", "Speech checking isn't set up yet. Please try again later.");
        }
        var client = http.CreateClient("language");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 120));
        return new AzureSpeech(client, o.Speech, logger);
    }
}

// ===================================================================== Azure Translator (REST v3)

internal sealed class AzureTranslator(HttpClient http, AzureTranslatorOptions o, ILogger logger)
{
    public async Task<TranslatorResult> TranslateAsync(string text, string? from, string to, CancellationToken ct)
    {
        var url = $"{o.Endpoint.TrimEnd('/')}/translate?api-version=3.0&to={Uri.EscapeDataString(to)}" +
                  (string.IsNullOrWhiteSpace(from) ? "" : $"&from={Uri.EscapeDataString(from)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new[] { new { Text = text } }) };
        request.Headers.Add("Ocp-Apim-Subscription-Key", o.ApiKey);
        if (!string.IsNullOrWhiteSpace(o.Region)) request.Headers.Add("Ocp-Apim-Subscription-Region", o.Region);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Azure Translator returned {Status}: {Body}", (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                throw new LanguageServiceException(response.StatusCode == HttpStatusCode.TooManyRequests ? "translator_busy" : "translator_error",
                    "The translator couldn't answer right now. Please try again in a moment.");
            }
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement[0];
            var translated = first.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
            var detected = first.TryGetProperty("detectedLanguage", out var d) ? d.GetProperty("language").GetString() : null;
            return new TranslatorResult(translated, detected ?? from ?? "", string.IsNullOrWhiteSpace(from), "AzureTranslator", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Azure Translator call failed");
            throw new LanguageServiceException("translator_error", "The translator couldn't answer right now. Please try again in a moment.", ex);
        }
    }
}

// ===================================================================== Azure Speech (short-audio REST, pronunciation assessment)

internal sealed class AzureSpeech(HttpClient http, AzureSpeechOptions o, ILogger logger)
{
    public async Task<PronunciationResult> AssessAsync(byte[] wav, string locale, string referenceText, CancellationToken ct)
    {
        var config = JsonSerializer.Serialize(new
        {
            ReferenceText = referenceText, GradingSystem = "HundredMark", Granularity = "Word", Dimension = "Comprehensive", EnableMiscue = true
        });
        using var doc = await RecognizeRawAsync(wav, locale, Convert.ToBase64String(Encoding.UTF8.GetBytes(config)), ct);
        if (Best(doc.RootElement) is not { } best)
            return new PronunciationResult("", 0, 0, 0, 0, 0, [], false);

        // Older responses put the scores on the NBest entry; newer ones nest them under "PronunciationAssessment".
        var scores = best.TryGetProperty("PronunciationAssessment", out var pa) ? pa : best;
        var words = new List<WordAssessment>();
        if (best.TryGetProperty("Words", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in list.EnumerateArray())
            {
                var ws = w.TryGetProperty("PronunciationAssessment", out var wpa) ? wpa : w;
                words.Add(new WordAssessment(Str(w, "Word"), Num(ws, "AccuracyScore"), Str(ws, "ErrorType", "None")));
            }
        }
        return new PronunciationResult(Str(best, "Display", Str(best, "Lexical")), Num(scores, "AccuracyScore"), Num(scores, "FluencyScore"),
            Num(scores, "CompletenessScore"), Num(scores, "PronScore"), Num(best, "Confidence", 0.8), words, false);
    }

    public async Task<SpeechTranscript> RecognizeAsync(byte[] wav, string locale, CancellationToken ct)
    {
        using var doc = await RecognizeRawAsync(wav, locale, null, ct);
        var best = Best(doc.RootElement);
        var text = best is { } b ? Str(b, "Display") : doc.RootElement.TryGetProperty("DisplayText", out var t) ? t.GetString() ?? "" : "";
        return new SpeechTranscript(text, best is { } c ? Num(c, "Confidence", 0.8) : 0, false);
    }

    private async Task<JsonDocument> RecognizeRawAsync(byte[] wav, string locale, string? assessmentHeader, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(o.Endpoint) ? $"https://{o.Region}.stt.speech.microsoft.com" : o.Endpoint!.TrimEnd('/');
        var url = $"{host}/speech/recognition/conversation/cognitiveservices/v1?language={Uri.EscapeDataString(locale)}&format=detailed";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(wav) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav; codecs=audio/pcm; samplerate=16000");
        request.Headers.Add("Ocp-Apim-Subscription-Key", o.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (assessmentHeader is not null) request.Headers.Add("Pronunciation-Assessment", assessmentHeader);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Azure Speech returned {Status}: {Body}", (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                throw new LanguageServiceException("speech_error", "Speech checking isn't available right now. Please try again in a moment.");
            }
            var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("RecognitionStatus", out var s) ? s.GetString() : "Success";
            if (status is "NoMatch" or "InitialSilenceTimeout" or "BabbleTimeout")
            {
                doc.Dispose();
                throw new LanguageServiceException("speech_no_match", "We couldn't hear any words. Try again a little closer to the microphone.");
            }
            return doc;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Azure Speech call failed");
            throw new LanguageServiceException("speech_error", "Speech checking isn't available right now. Please try again in a moment.", ex);
        }
    }

    private static JsonElement? Best(JsonElement root) =>
        root.TryGetProperty("NBest", out var n) && n.ValueKind == JsonValueKind.Array && n.GetArrayLength() > 0 ? n[0] : null;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}

// ===================================================================== offline stand-ins (development, tests)

/// <summary>Word-for-word sample translation with a tiny glossary. Every result is flagged IsSample.</summary>
internal static class OfflineTranslator
{
    private static readonly Dictionary<string, Dictionary<string, string>> Glossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hello"] = "hola", ["good"] = "bueno", ["morning"] = "mañana", ["thank"] = "gracias", ["thanks"] = "gracias", ["you"] = "tú",
            ["please"] = "por favor", ["yes"] = "sí", ["no"] = "no", ["water"] = "agua", ["friend"] = "amigo", ["house"] = "casa",
            ["i"] = "yo", ["am"] = "soy", ["is"] = "es", ["the"] = "el", ["a"] = "un", ["and"] = "y", ["cat"] = "gato", ["dog"] = "perro",
            ["book"] = "libro", ["eat"] = "comer", ["drink"] = "beber", ["today"] = "hoy", ["tomorrow"] = "mañana", ["goodbye"] = "adiós"
        },
        ["fr"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hello"] = "bonjour", ["good"] = "bon", ["thank"] = "merci", ["thanks"] = "merci", ["you"] = "tu", ["please"] = "s'il vous plaît",
            ["yes"] = "oui", ["no"] = "non", ["water"] = "eau", ["friend"] = "ami", ["house"] = "maison", ["i"] = "je", ["am"] = "suis",
            ["is"] = "est", ["the"] = "le", ["and"] = "et", ["cat"] = "chat", ["dog"] = "chien", ["book"] = "livre", ["today"] = "aujourd'hui"
        },
        ["de"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hello"] = "hallo", ["good"] = "gut", ["morning"] = "Morgen", ["thank"] = "danke", ["thanks"] = "danke", ["you"] = "du",
            ["please"] = "bitte", ["yes"] = "ja", ["no"] = "nein", ["water"] = "Wasser", ["friend"] = "Freund", ["house"] = "Haus",
            ["i"] = "ich", ["am"] = "bin", ["is"] = "ist", ["the"] = "der", ["and"] = "und", ["cat"] = "Katze", ["dog"] = "Hund", ["book"] = "Buch"
        }
    };

    public static Task<TranslatorResult> TranslateAsync(string text, string? from, string to)
    {
        var source = string.IsNullOrWhiteSpace(from) ? Detect(text) : from;
        var target = to.ToLowerInvariant();
        Func<string, string?> lookup = source == "en" && Glossary.TryGetValue(target, out var forward)
            ? w => forward.TryGetValue(w, out var t) ? t : null
            : target == "en" && Glossary.TryGetValue(source, out var back)
                ? w => back.FirstOrDefault(p => p.Value.Equals(w, StringComparison.OrdinalIgnoreCase)).Key
                : _ => null;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            var core = w.Trim(',', '.', '!', '?', ';', ':', '¿', '¡');
            var t = lookup(core);
            return t is null ? w : w.Replace(core, t);
        });
        return Task.FromResult(new TranslatorResult($"{string.Join(' ', words)} [{target}, sample]", source, string.IsNullOrWhiteSpace(from),
            "Offline", true));
    }

    /// <summary>Rough guess by script and a few very common words.</summary>
    public static string Detect(string text)
    {
        foreach (var c in text)
        {
            var block = (int)c;
            if (block is >= 0x0600 and <= 0x06FF) return "ar";
            if (block is >= 0x0400 and <= 0x04FF) return "ru";
            if (block is >= 0x3040 and <= 0x30FF) return "ja";
            if (block is >= 0xAC00 and <= 0xD7AF) return "ko";
            if (block is >= 0x4E00 and <= 0x9FFF) return "zh-Hans";
            if (block is >= 0x0900 and <= 0x097F) return "hi";
        }
        var words = text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', '¿', '¡'], StringSplitOptions.RemoveEmptyEntries);
        int Score(params string[] markers) => words.Count(markers.Contains);
        var scores = new Dictionary<string, int>
        {
            ["es"] = Score("el", "la", "que", "de", "y", "es", "hola", "gracias", "los", "por", "una", "estoy"),
            ["fr"] = Score("le", "les", "et", "est", "je", "bonjour", "merci", "une", "des", "pas", "vous"),
            ["de"] = Score("der", "die", "das", "und", "ist", "ich", "nicht", "danke", "ein", "eine", "hallo"),
            ["en"] = Score("the", "and", "is", "i", "you", "hello", "a", "to", "of", "it", "thanks")
        };
        var best = scores.OrderByDescending(s => s.Value).First();
        return best.Value == 0 ? "en" : best.Key;
    }
}

/// <summary>
/// Sample pronunciation scores derived from the recording (length and loudness), so the screens can be tried without Azure.
/// Every result is flagged IsSample and never counts as an exam mark.
/// </summary>
internal static class OfflineSpeech
{
    public static PronunciationResult Assess(byte[] wav, string referenceText)
    {
        var (seconds, rms) = Measure(wav);
        var words = referenceText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', '.', '!', '?', '¿', '¡')).ToList();
        // About 2.5 words a second is a natural pace for a learner.
        var expected = Math.Max(1.0, words.Count / 2.5);
        var pace = Math.Clamp(100 - Math.Abs(seconds - expected) / expected * 60, 20, 100);
        var voice = rms < 0.005 ? 20 : 95;
        var accuracy = Math.Round(Math.Min(pace, voice) * 0.9 + 8, 0);
        var scored = words.Select((w, i) =>
        {
            var a = Math.Clamp(accuracy + ((w.Length * 7 + i * 13) % 21) - 10, 0, 100);
            return new WordAssessment(w, a, a < 60 ? "Mispronunciation" : "None");
        }).ToList();
        var completeness = voice < 50 ? 30 : 100;
        return new PronunciationResult(voice < 50 ? "" : referenceText, accuracy, Math.Round(pace), completeness,
            Math.Round(accuracy * 0.6 + pace * 0.2 + completeness * 0.2), 0, scored, true);
    }

    public static SpeechTranscript Recognize(byte[] wav, string locale)
    {
        var (seconds, rms) = Measure(wav);
        var sample = rms < 0.005 ? "" : locale.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "hola, ¿cómo estás?"
            : locale.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "bonjour, comment ça va ?"
            : locale.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "hallo, wie geht es dir?"
            : "hello, how are you?";
        return new SpeechTranscript(seconds < 0.3 ? "" : sample, 0, true);
    }

    private static (double Seconds, double Rms) Measure(byte[] wav)
    {
        if (WavAudio.Read(wav) is not { } w || w.Mono.Length == 0) return (0, 0);
        double sum = 0;
        foreach (var s in w.Mono) sum += s * s;
        return (w.Info.Duration.TotalSeconds, Math.Sqrt(sum / w.Mono.Length));
    }
}
