using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Practice;

namespace LinguaPath.Application.Features.Practice;

/// <summary>SM-2 scheduling for "My words" flashcards (FR-L94).</summary>
public static class Sm2
{
    public const double MinEase = 1.3;

    public static void Review(UserWord word, RecallGrade grade, DateTimeOffset now)
    {
        var q = (int)grade;
        if (q < 3)
        {
            // Forgotten: start again tomorrow, ease drops.
            word.Repetitions = 0;
            word.IntervalDays = 1;
            word.Lapses++;
        }
        else
        {
            word.Repetitions++;
            word.IntervalDays = word.Repetitions switch
            {
                1 => 1,
                2 => 6,
                _ => Math.Max(1, (int)Math.Round(word.IntervalDays * word.EaseFactor))
            };
            // "Easy" also stretches the interval a little, as most SM-2 apps do.
            if (grade == RecallGrade.Easy && word.Repetitions > 1) word.IntervalDays = (int)Math.Ceiling(word.IntervalDays * 1.3);
        }
        word.EaseFactor = Math.Max(MinEase, Math.Round(word.EaseFactor + (0.1 - (5 - q) * (0.08 + (5 - q) * 0.02)), 2));
        word.IntervalDays = Math.Min(word.IntervalDays, 365);
        word.NextReviewAt = now.AddDays(word.IntervalDays);
        word.LastReviewedAt = now;
    }
}

/// <summary>Word-by-word comparison of a dictation with the sentence (FR-L93).</summary>
public static class Dictation
{
    public sealed record Result(int Score, IReadOnlyList<DiffWordDto> Words);

    public static IReadOnlyList<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim()).Where(w => AnswerGrader.Normalize(w).Length > 0).ToList();

    public static Result Compare(string expected, string typed)
    {
        var e = Words(expected);
        var t = Words(typed);
        var en = e.Select(w => AnswerGrader.Normalize(w)).ToArray();
        var tn = t.Select(w => AnswerGrader.Normalize(w)).ToArray();

        // Longest common subsequence on normalised words: case, punctuation and apostrophes don't count; accents do.
        var lcs = new int[en.Length + 1, tn.Length + 1];
        for (var i = en.Length - 1; i >= 0; i--)
            for (var j = tn.Length - 1; j >= 0; j--)
                lcs[i, j] = en[i] == tn[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var raw = new List<DiffWordDto>();
        int a = 0, b = 0;
        while (a < en.Length || b < tn.Length)
        {
            if (a < en.Length && b < tn.Length && en[a] == tn[b])
            {
                raw.Add(new DiffWordDto(t[b], e[a], DiffKind.Correct));
                a++; b++;
            }
            else if (b < tn.Length && (a == en.Length || lcs[a, b + 1] >= lcs[a + 1, b]))
            {
                raw.Add(new DiffWordDto(t[b], null, DiffKind.Extra));
                b++;
            }
            else
            {
                raw.Add(new DiffWordDto("", e[a], DiffKind.Missing));
                a++;
            }
        }

        // Between two correct words, typed words that don't match and expected words that are missing are misspellings:
        // pair them in order ("typed → expected"); whatever is left over is extra or missing.
        var words = new List<DiffWordDto>();
        var extras = new List<DiffWordDto>();
        var missing = new List<DiffWordDto>();
        void Flush()
        {
            var pairs = Math.Min(extras.Count, missing.Count);
            for (var i = 0; i < pairs; i++) words.Add(new DiffWordDto(extras[i].Text, missing[i].Expected, DiffKind.Wrong));
            words.AddRange(extras.Skip(pairs));
            words.AddRange(missing.Skip(pairs));
            extras.Clear();
            missing.Clear();
        }
        foreach (var w in raw)
        {
            if (w.Kind == DiffKind.Extra) extras.Add(w);
            else if (w.Kind == DiffKind.Missing) missing.Add(w);
            else
            {
                Flush();
                words.Add(w);
            }
        }
        Flush();

        var correct = words.Count(w => w.Kind == DiffKind.Correct);
        var denominator = Math.Max(e.Count, correct + words.Count(w => w.Kind is DiffKind.Extra or DiffKind.Wrong));
        var score = denominator == 0 ? 0 : (int)Math.Round(100.0 * correct / denominator);
        return new Result(score, words);
    }

}

/// <summary>Reading comprehension questions stored as JSON on the practice item (FR-L90).</summary>
public static class ReadingQuestions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed class Stored
    {
        public string Question { get; set; } = "";
        public List<string> Options { get; set; } = [];
        public int Correct { get; set; }
        public string? Explanation { get; set; }
    }

    public static List<Stored> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Stored>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Serialize(IEnumerable<Stored> questions)
    {
        var list = questions.Where(q => !string.IsNullOrWhiteSpace(q.Question) && q.Options.Count >= 2).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, Json);
    }

    /// <summary>Without the answers, for the learner.</summary>
    public static IReadOnlyList<ReadingQuestionDto> ForLearner(string? json) =>
        Parse(json).Select((q, i) => new ReadingQuestionDto(i, q.Question, q.Options)).ToList();

    public static ReadResultDto Grade(string? json, IReadOnlyList<int> answers)
    {
        var questions = Parse(json);
        var results = questions.Select((q, i) =>
        {
            var given = i < answers.Count ? answers[i] : -1;
            return new ReadQuestionResultDto(i, given == q.Correct, q.Correct, q.Explanation);
        }).ToList();
        var correct = results.Count(r => r.Correct);
        var score = questions.Count == 0 ? 100 : (int)Math.Round(100.0 * correct / questions.Count);
        return new ReadResultDto(score, correct, questions.Count, results);
    }
}

public static class PracticeRules
{
    /// <summary>Start of the learner's day in UTC, for daily limits (BR-15).</summary>
    public static DateTimeOffset DayStartUtc(DateTimeOffset utcNow, string? timeZoneId)
    {
        var tz = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found)) tz = found;
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var midnight = local.Date;
        return new DateTimeOffset(midnight, tz.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Whitespace-normalised text; case is kept because it can change a translation (names, German nouns).</summary>
    public static string CleanText(string text) => Spaces.Replace(text.Normalize(NormalizationForm.FormC), " ").Trim();

    public static string CacheKey(string from, string to, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{from.ToLowerInvariant()}|{to.ToLowerInvariant()}|{CleanText(text)}"));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Language code part of a code or locale: "es-MX" → "es", "zh-Hans" → "zh-Hans" (Translator needs the script).</summary>
    public static string LanguageCode(string code)
    {
        code = code.Trim();
        if (code.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) || code.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
            return code[..7];
        var dash = code.IndexOfAny(['-', '_']);
        return (dash > 0 ? code[..dash] : code).ToLowerInvariant();
    }

    /// <summary>Overall pronunciation score for a learner (FR-L92) and an exam mark (speaking pre-grading).</summary>
    public static int SpeakScore(PronunciationResult r) =>
        (int)Math.Round(Math.Clamp(r.Overall > 0 ? r.Overall : (r.Accuracy * 0.6 + r.Fluency * 0.2 + r.Completeness * 0.2), 0, 100));

    /// <summary>
    /// How sure the pronunciation mark is (0-1): sample scores never count, and low completeness means the learner
    /// said something else or stopped early, so a reviewer should listen.
    /// </summary>
    public static double SpeakConfidence(PronunciationResult r)
    {
        if (r.IsSample) return 0;
        if (r.Completeness < 60 || string.IsNullOrWhiteSpace(r.Transcript)) return Math.Min(r.Confidence, 0.5);
        return Math.Clamp(r.Confidence, 0, 1);
    }
}
