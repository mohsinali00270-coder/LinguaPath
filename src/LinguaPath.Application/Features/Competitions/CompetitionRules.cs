using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Competitions;

/// <summary>A player's totals for ranking (FR-Q04).</summary>
public sealed record Standing(int PlayerId, int Score, long TotalAnswerMs, bool LeftEarly = false);

/// <summary>Pure competition rules: scoring, ranking, Elo, settings, bots, tournaments and leagues.</summary>
public static class CompetitionRules
{
    public static readonly int[] QuestionCounts = [5, 10, 15];
    public const int MinSeconds = 10;
    public const int MaxSeconds = 20;
    public const int StartRating = 1000;
    public const int EloK = 32;
    public const int LeagueSize = 30;
    public const int LeagueZone = 5;

    /// <summary>Only objective types are used in competitions (FR-Q02).</summary>
    public static readonly QuestionType[] Types = [QuestionType.MultipleChoice, QuestionType.TrueFalse, QuestionType.FillBlank, QuestionType.ListenChoose];

    /// <summary>FR-Q01: 5 / 10 / 15 questions, 10–20 seconds each.</summary>
    public static string? ValidateSettings(int questionCount, int secondsPerQuestion) =>
        !QuestionCounts.Contains(questionCount) ? "Choose 5, 10 or 15 questions."
        : secondsPerQuestion is < MinSeconds or > MaxSeconds ? $"Choose {MinSeconds} to {MaxSeconds} seconds per question."
        : null;

    /// <summary>FR-Q04: 100 for a correct answer plus up to 50 for speed, linear in the time left. Wrong or late = 0.</summary>
    public static int Score(bool correct, int answerMs, int secondsPerQuestion)
    {
        if (!correct) return 0;
        var total = secondsPerQuestion * 1000;
        var left = Math.Clamp(total - answerMs, 0, total);
        return 100 + (int)Math.Round(50.0 * left / total, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Ranks by score, ties broken by total answer time (FR-Q04). Players who left early rank after everyone who stayed.
    /// Exactly equal players share a rank.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Rank(IEnumerable<Standing> standings)
    {
        var ordered = standings.OrderBy(s => s.LeftEarly).ThenByDescending(s => s.Score).ThenBy(s => s.TotalAnswerMs).ToList();
        var ranks = new Dictionary<int, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var prev = i > 0 ? ordered[i - 1] : null;
            ranks[s.PlayerId] = prev is not null && prev.LeftEarly == s.LeftEarly && prev.Score == s.Score && prev.TotalAnswerMs == s.TotalAnswerMs
                ? ranks[prev.PlayerId]
                : i + 1;
        }
        return ranks;
    }

    /// <summary>1 v 1 and group results: rank 1 wins (shared rank 1 = draw), everyone else loses. Leaving early is always a loss (FR-Q06).</summary>
    public static CompetitionResult Result(int rank, int playersAtRankOne, bool leftEarly) =>
        leftEarly ? CompetitionResult.Loss
        : rank == 1 ? (playersAtRankOne > 1 ? CompetitionResult.Draw : CompetitionResult.Win)
        : CompetitionResult.Loss;

    /// <summary>Elo (FR-Q08): expected score from the rating gap, K = 32.</summary>
    public static (int A, int B) Elo(int ratingA, int ratingB, CompetitionResult resultA, int k = EloK)
    {
        var expectedA = 1.0 / (1 + Math.Pow(10, (ratingB - ratingA) / 400.0));
        var actualA = resultA switch { CompetitionResult.Win => 1.0, CompetitionResult.Draw => 0.5, _ => 0.0 };
        var delta = (int)Math.Round(k * (actualA - expectedA), MidpointRounding.AwayFromZero);
        return (ratingA + delta, ratingB - delta);
    }

    /// <summary>Quick match (SRS 11.4): same language, ± 1 CEFR level.</summary>
    public static bool LevelsMatch(CefrLevel a, CefrLevel b) => Math.Abs((int)a - (int)b) <= 1;

    /// <summary>Streaks for the stats screen.</summary>
    public static (int Current, int Best) Streak(int current, int best, CompetitionResult result)
    {
        var next = result == CompetitionResult.Win ? current + 1 : 0;
        return (next, Math.Max(best, next));
    }

    public static int WinRate(int played, int won) => played == 0 ? 0 : (int)Math.Round(100.0 * won / played, MidpointRounding.AwayFromZero);

    /// <summary>FR-Q10: group battle top 3 get 100% / 66% / 33% of the bonus.</summary>
    public static int GroupBonus(int rank, int firstPrize) => rank switch
    {
        1 => firstPrize,
        2 => (int)Math.Round(firstPrize * 2 / 3.0, MidpointRounding.AwayFromZero),
        3 => (int)Math.Round(firstPrize / 3.0, MidpointRounding.AwayFromZero),
        _ => 0
    };

    /// <summary>The bot's answer to a question: deterministic per competition and question, roughly 60% right (A1) to 75% (C2).</summary>
    public static (bool Correct, int AnswerMs) BotAnswer(int competitionId, int index, CefrLevel level, int secondsPerQuestion)
    {
        var rng = new Random(HashCode.Combine(competitionId, index, 7919));
        var accuracy = 0.57 + 0.03 * (int)level;
        var correct = rng.NextDouble() < accuracy;
        var ms = (int)(secondsPerQuestion * 1000 * (0.3 + 0.5 * rng.NextDouble()));
        return (correct, ms);
    }

    public static readonly string[] BotNames = ["Lingo Bot", "Quiz Bot", "Practice Bot"];

    /// <summary>Tournament rounds: the top half go through (at least 2); the last round, or 2 or fewer players, is the final.</summary>
    public static int Advancing(int players, int round, int rounds) =>
        round >= rounds || players <= 2 ? 0 : Math.Max(2, (players + 1) / 2);

    /// <summary>Who takes the 3 prizes: tournament places in order, disqualified players skipped.</summary>
    public static IReadOnlyList<(Guid UserId, int Place, int Points)> Prizes(IEnumerable<(Guid UserId, int Place, bool Disqualified)> standings,
        int first, int second, int third)
    {
        int[] prizes = [first, second, third];
        return standings.Where(s => !s.Disqualified).OrderBy(s => s.Place).Take(3)
            .Select((s, i) => (s.UserId, i + 1, prizes[i])).Where(p => p.Item3 > 0).ToList();
    }

    // ------------------------------------------------------------------ leagues (FR-S08)

    /// <summary>Monday 00:00 UTC of the week that contains <paramref name="now"/>.</summary>
    public static DateOnly WeekOf(DateTimeOffset now)
    {
        var d = DateOnly.FromDateTime(now.UtcDateTime);
        var offset = ((int)d.DayOfWeek + 6) % 7;
        return d.AddDays(-offset);
    }

    public static DateTimeOffset WeekStartUtc(DateOnly week) => new(week.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>Top 5 go up, bottom 5 go down; nobody goes down from the bottom tier or up from the top; small leagues only promote.</summary>
    public static LeagueOutcome Zone(int rank, int members, LeagueTier tier, int points)
    {
        if (points > 0 && rank <= LeagueZone && tier < LeagueTier.Diamond) return LeagueOutcome.Promoted;
        if (members > 2 * LeagueZone && rank > members - LeagueZone && tier > LeagueTier.Bronze) return LeagueOutcome.Relegated;
        return LeagueOutcome.Stayed;
    }

    public static LeagueTier NextTier(LeagueTier tier, LeagueOutcome outcome) => outcome switch
    {
        LeagueOutcome.Promoted when tier < LeagueTier.Diamond => tier + 1,
        LeagueOutcome.Relegated when tier > LeagueTier.Bronze => tier - 1,
        _ => tier
    };

    /// <summary>Dense league ranks by weekly points (equal points share a rank, so nobody is relegated on a tie).</summary>
    public static IReadOnlyList<(Guid UserId, int Points, int Rank)> LeagueRanks(IEnumerable<(Guid UserId, int Points, DateTimeOffset JoinedAt)> members)
    {
        var ordered = members.OrderByDescending(m => m.Points).ThenBy(m => m.JoinedAt).ToList();
        var result = new List<(Guid, int, int)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var rank = i > 0 && ordered[i - 1].Points == ordered[i].Points ? result[i - 1].Item3 : i + 1;
            result.Add((ordered[i].UserId, ordered[i].Points, rank));
        }
        return result;
    }

    /// <summary>Questions shuffled with a seed so tests and replays are repeatable.</summary>
    public static IReadOnlyList<int> Pick(IReadOnlyList<int> candidates, int count, int seed)
    {
        var rng = new Random(seed);
        return candidates.Distinct().OrderBy(_ => rng.Next()).Take(count).ToList();
    }
}
