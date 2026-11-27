using System.Globalization;
using System.Text;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Assessment;

/// <summary>What the learner sent for one question.</summary>
public sealed record AnswerInput(string? Text, IReadOnlyCollection<int> OptionIds, IReadOnlyDictionary<int, string> Pairs, string? AudioUrl)
{
    public static readonly AnswerInput Empty = new(null, [], new Dictionary<int, string>(), null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && OptionIds.Count == 0 && Pairs.Count == 0 && string.IsNullOrWhiteSpace(AudioUrl);
}

/// <param name="IsCorrect">Null while a reviewer still has to grade it.</param>
/// <param name="Fraction">0..1 of the question's points.</param>
public sealed record GradeOutcome(bool? IsCorrect, decimal Fraction, bool NeedsReview, string CorrectAnswer);

/// <summary>
/// Grades objective answers instantly (FR-L63). Writing and speaking go to the grading queue (FR-A81);
/// AI scoring of those arrives in Phase 5.
/// </summary>
public static class AnswerGrader
{
    public static bool IsManuallyGraded(QuestionType type) => type is QuestionType.Writing or QuestionType.Speak;

    public static GradeOutcome Grade(Question q, AnswerInput a)
    {
        var options = q.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).ToList();
        var correctText = CorrectAnswerText(q);

        switch (q.Type)
        {
            case QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose:
            {
                if (a.OptionIds.Count == 0) return new(false, 0, false, correctText);
                var correct = options.Where(o => o.IsCorrect).Select(o => o.Id).ToHashSet();
                var ok = correct.SetEquals(a.OptionIds);
                return new(ok, ok ? 1 : 0, false, correctText);
            }

            case QuestionType.FillBlank or QuestionType.Translate or QuestionType.ReorderWords:
            {
                if (string.IsNullOrWhiteSpace(a.Text)) return new(false, 0, false, correctText);
                var given = Normalize(a.Text);
                var ok = options.Where(o => q.Type == QuestionType.ReorderWords || o.IsCorrect).Any(o => Normalize(o.Text) == given);
                return new(ok, ok ? 1 : 0, false, correctText);
            }

            case QuestionType.MatchPairs:
            {
                if (options.Count == 0) return new(false, 0, false, correctText);
                var matched = options.Count(o => a.Pairs.TryGetValue(o.Id, out var target) && Normalize(target) == Normalize(o.MatchKey ?? ""));
                var fraction = Math.Round((decimal)matched / options.Count, 2);
                return new(matched == options.Count, fraction, false, correctText);
            }

            case QuestionType.Writing:
                return string.IsNullOrWhiteSpace(a.Text)
                    ? new(false, 0, false, correctText)
                    : new(null, 0, true, correctText);

            case QuestionType.Speak:
                return string.IsNullOrWhiteSpace(a.AudioUrl)
                    ? new(false, 0, false, correctText)
                    : new(null, 0, true, correctText);

            default:
                return new(false, 0, false, correctText);
        }
    }

    /// <summary>The answer shown in feedback and the exam review (FR-L52, FR-L72).</summary>
    public static string CorrectAnswerText(Question q)
    {
        var options = q.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).ToList();
        return q.Type switch
        {
            QuestionType.MatchPairs => string.Join("; ", options.Select(o => $"{o.Text} → {o.MatchKey}")),
            QuestionType.ReorderWords => options.FirstOrDefault()?.Text ?? "",
            QuestionType.FillBlank or QuestionType.Translate => options.FirstOrDefault(o => o.IsCorrect)?.Text ?? "",
            QuestionType.Writing => "Graded by a reviewer.",
            QuestionType.Speak => q.Prompt ?? "",
            _ => string.Join(", ", options.Where(o => o.IsCorrect).Select(o => o.Text))
        };
    }

    /// <summary>Case and punctuation don't matter; accents do (they change meaning in many languages).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                // "don't" → "dont", but "hola,amigo" → "hola amigo"
                if (c is '\'' or '’') continue;
                space = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Words of the canonical sentence for a reorder question, shuffled by the caller.</summary>
    public static IReadOnlyList<string> WordTiles(Question q)
    {
        var sentence = q.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).FirstOrDefault()?.Text ?? "";
        return sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Questions with more than one correct option let the learner pick several.</summary>
    public static bool AllowsMultiple(Question q) =>
        q.Type == QuestionType.MultipleChoice && q.Options.Count(o => o.IsCorrect) > 1;
}

/// <summary>Leitner-style review list for wrong answers (FR-L54).</summary>
public static class ReviewScheduler
{
    public static readonly int[] IntervalDays = [1, 2, 4, 7, 15];
    public const int MaxBox = 5;

    /// <summary>Applies an answer to a card. Returns false when the card is learnt and can be removed.</summary>
    public static bool Apply(ReviewCard card, bool correct, DateOnly today, DateTimeOffset now)
    {
        card.UpdatedAt = now;
        if (!correct)
        {
            card.Box = 1;
            card.Lapses++;
            card.DueDate = today.AddDays(IntervalDays[0]);
            return true;
        }
        if (card.Box >= MaxBox) return false;
        card.Box++;
        card.DueDate = today.AddDays(IntervalDays[card.Box - 1]);
        return true;
    }
}

public static class GradeRules
{
    public static int Percent(decimal score, int max) => max <= 0 ? 0 : (int)Math.Round(100m * score / max, MidpointRounding.AwayFromZero);

    /// <summary>BR-05: weighted average of daily quizzes and monthly exams (best attempt per month).</summary>
    public static int? CourseGrade(int? dailyAverage, int? examAverage, int quizWeight, int examWeight)
    {
        if (dailyAverage is null && examAverage is null) return null;
        if (dailyAverage is null) return examAverage;
        if (examAverage is null) return dailyAverage;
        var total = quizWeight + examWeight;
        if (total <= 0) return (dailyAverage + examAverage) / 2;
        return (int)Math.Round((dailyAverage.Value * quizWeight + examAverage.Value * examWeight) / (double)total, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// FR-L11: the learner is placed at the first level (from the lowest) where they score below the pass percent;
    /// passing every level places them at the highest level tested.
    /// </summary>
    public static CefrLevel PlacementLevel(IEnumerable<(CefrLevel Level, bool Correct)> answers, int passPercent, CefrLevel lowest)
    {
        var byLevel = answers.GroupBy(a => a.Level).OrderBy(g => g.Key).ToList();
        if (byLevel.Count == 0) return lowest;
        foreach (var level in byLevel)
        {
            var percent = 100 * level.Count(a => a.Correct) / level.Count();
            if (percent < passPercent) return level.Key;
        }
        return byLevel[^1].Key;
    }

    /// <summary>UTC instant of the learner's next local midnight (end of a daily quiz, FR-L53).</summary>
    public static DateTimeOffset NextMidnightUtc(DateTimeOffset utcNow, string? timeZoneId)
    {
        var tz = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found)) tz = found;
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var nextLocalMidnight = local.Date.AddDays(1);
        var offset = tz.GetUtcOffset(nextLocalMidnight);
        return new DateTimeOffset(nextLocalMidnight, offset).ToUniversalTime();
    }

    public static string Csv(IEnumerable<int> ids) => string.Join(",", ids);

    public static List<int> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();

    public static string PairsToText(IReadOnlyDictionary<int, string> pairs) =>
        string.Join("\n", pairs.Select(p => $"{p.Key}={p.Value.Replace("\n", " ")}"));

    public static Dictionary<int, string> ParsePairs(string? text)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0 && int.TryParse(line[..i], out var id)) result[id] = line[(i + 1)..];
        }
        return result;
    }
}
