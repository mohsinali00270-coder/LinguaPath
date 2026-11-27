using LinguaPath.Application.Features.Competitions;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 9: scoring, ranking, Elo, settings, bots, tournaments and leagues (FR-Q01 .. FR-Q11, FR-S08, BR-29).</summary>
public class CompetitionRulesTests
{
    [Theory]
    [InlineData(5, 15, true)]
    [InlineData(10, 10, true)]
    [InlineData(15, 20, true)]
    [InlineData(7, 15, false)]
    [InlineData(10, 9, false)]
    [InlineData(10, 21, false)]
    public void Settings(int count, int seconds, bool ok) => Assert.Equal(ok, CompetitionRules.ValidateSettings(count, seconds) is null);

    [Theory]
    [InlineData(true, 0, 150)]       // instant
    [InlineData(true, 5000, 125)]    // half the time left on 10 s
    [InlineData(true, 10000, 100)]   // at the buzzer
    [InlineData(true, 12000, 100)]   // late but within the grace: no negative bonus
    [InlineData(false, 100, 0)]
    public void Score_rewards_correct_and_fast(bool correct, int ms, int expected) => Assert.Equal(expected, CompetitionRules.Score(correct, ms, 10));

    [Fact]
    public void Faster_correct_answer_scores_higher() =>
        Assert.True(CompetitionRules.Score(true, 2000, 15) > CompetitionRules.Score(true, 9000, 15));

    [Fact]
    public void Ranking_breaks_ties_by_time_and_puts_leavers_last()
    {
        var ranks = CompetitionRules.Rank([
            new Standing(1, 900, 40_000),
            new Standing(2, 900, 35_000),
            new Standing(3, 1200, 60_000, LeftEarly: true),
            new Standing(4, 500, 20_000)
        ]);
        Assert.Equal(1, ranks[2]);
        Assert.Equal(2, ranks[1]);
        Assert.Equal(3, ranks[4]);
        Assert.Equal(4, ranks[3]);
    }

    [Fact]
    public void Exact_ties_share_a_rank_and_draw()
    {
        var ranks = CompetitionRules.Rank([new Standing(1, 700, 30_000), new Standing(2, 700, 30_000)]);
        Assert.Equal(1, ranks[1]);
        Assert.Equal(1, ranks[2]);
        Assert.Equal(CompetitionResult.Draw, CompetitionRules.Result(1, 2, false));
        Assert.Equal(CompetitionResult.Win, CompetitionRules.Result(1, 1, false));
        Assert.Equal(CompetitionResult.Loss, CompetitionRules.Result(1, 1, leftEarly: true));
        Assert.Equal(CompetitionResult.Loss, CompetitionRules.Result(2, 1, false));
    }

    [Fact]
    public void Elo()
    {
        var (a, b) = CompetitionRules.Elo(1000, 1000, CompetitionResult.Win);
        Assert.Equal(1016, a);
        Assert.Equal(984, b);
        var (c, d) = CompetitionRules.Elo(1000, 1000, CompetitionResult.Draw);
        Assert.Equal(1000, c);
        Assert.Equal(1000, d);
        // Beating a much stronger player is worth more than beating an equal one.
        var (upset, _) = CompetitionRules.Elo(1000, 1400, CompetitionResult.Win);
        Assert.True(upset - 1000 > 16);
        // Points are conserved.
        var (e, f) = CompetitionRules.Elo(1210, 1035, CompetitionResult.Loss);
        Assert.Equal(1210 + 1035, e + f);
    }

    [Theory]
    [InlineData(CefrLevel.A2, CefrLevel.A1, true)]
    [InlineData(CefrLevel.A2, CefrLevel.B1, true)]
    [InlineData(CefrLevel.A1, CefrLevel.B1, false)]
    public void Matchmaking_levels(CefrLevel a, CefrLevel b, bool ok) => Assert.Equal(ok, CompetitionRules.LevelsMatch(a, b));

    [Fact]
    public void Streaks_and_win_rate()
    {
        var (current, best) = CompetitionRules.Streak(2, 2, CompetitionResult.Win);
        Assert.Equal(3, current);
        Assert.Equal(3, best);
        (current, best) = CompetitionRules.Streak(current, best, CompetitionResult.Draw);
        Assert.Equal(0, current);
        Assert.Equal(3, best);
        Assert.Equal(67, CompetitionRules.WinRate(3, 2));
        Assert.Equal(0, CompetitionRules.WinRate(0, 0));
    }

    [Fact]
    public void Group_bonus_top_three() =>
        Assert.Equal(new[] { 30, 20, 10, 0 }, new[] { 1, 2, 3, 4 }.Select(r => CompetitionRules.GroupBonus(r, 30)).ToArray());

    [Fact]
    public void Bot_answers_are_repeatable_and_within_time()
    {
        var first = CompetitionRules.BotAnswer(42, 3, CefrLevel.B1, 15);
        Assert.Equal(first, CompetitionRules.BotAnswer(42, 3, CefrLevel.B1, 15));
        var all = Enumerable.Range(0, 200).Select(i => CompetitionRules.BotAnswer(7, i, CefrLevel.A1, 10)).ToList();
        Assert.All(all, a => Assert.InRange(a.AnswerMs, 0, 10_000));
        var accuracy = all.Count(a => a.Correct) / 200.0;
        Assert.InRange(accuracy, 0.45, 0.75);
    }

    [Theory]
    [InlineData(8, 1, 3, 4)]
    [InlineData(5, 1, 3, 3)]
    [InlineData(3, 2, 3, 2)]
    [InlineData(2, 1, 3, 0)]   // two players: this is the final
    [InlineData(8, 3, 3, 0)]   // last round
    public void Tournament_advancing(int players, int round, int rounds, int expected) =>
        Assert.Equal(expected, CompetitionRules.Advancing(players, round, rounds));

    [Fact]
    public void Prizes_skip_disqualified_players()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var prizes = CompetitionRules.Prizes([(a, 1, true), (b, 2, false), (c, 3, false), (d, 4, false)], 300, 200, 100);
        Assert.Equal(new[] { (b, 1, 300), (c, 2, 200), (d, 3, 100) }, prizes.ToArray());
        Assert.Empty(CompetitionRules.Prizes([(a, 1, false)], 0, 0, 0));
    }

    [Fact]
    public void Weeks_start_on_monday_utc()
    {
        Assert.Equal(new DateOnly(2026, 11, 16), CompetitionRules.WeekOf(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 16), CompetitionRules.WeekOf(new DateTimeOffset(2026, 11, 22, 23, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 16), CompetitionRules.WeekOf(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(1, 30, LeagueTier.Silver, 120, LeagueOutcome.Promoted)]
    [InlineData(5, 30, LeagueTier.Silver, 10, LeagueOutcome.Promoted)]
    [InlineData(6, 30, LeagueTier.Silver, 10, LeagueOutcome.Stayed)]
    [InlineData(26, 30, LeagueTier.Silver, 0, LeagueOutcome.Relegated)]
    [InlineData(26, 30, LeagueTier.Bronze, 0, LeagueOutcome.Stayed)]   // nowhere to go down
    [InlineData(1, 30, LeagueTier.Diamond, 500, LeagueOutcome.Stayed)] // nowhere to go up
    [InlineData(8, 8, LeagueTier.Gold, 0, LeagueOutcome.Stayed)]       // small league: no relegation
    [InlineData(1, 8, LeagueTier.Gold, 0, LeagueOutcome.Stayed)]       // no points, no promotion
    public void League_zones(int rank, int members, LeagueTier tier, int points, LeagueOutcome expected) =>
        Assert.Equal(expected, CompetitionRules.Zone(rank, members, tier, points));

    [Fact]
    public void League_ranks_share_places_on_equal_points()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var ranks = CompetitionRules.LeagueRanks([(a, 50, t0), (b, 80, t0), (c, 50, t0.AddMinutes(1))]);
        Assert.Equal((b, 80, 1), ranks[0]);
        Assert.Equal(2, ranks[1].Rank);
        Assert.Equal(2, ranks[2].Rank);
        Assert.Equal(LeagueTier.Gold, CompetitionRules.NextTier(LeagueTier.Silver, LeagueOutcome.Promoted));
        Assert.Equal(LeagueTier.Bronze, CompetitionRules.NextTier(LeagueTier.Bronze, LeagueOutcome.Relegated));
    }

    [Fact]
    public void Question_pick_is_seeded_and_unique()
    {
        var pool = Enumerable.Range(1, 50).ToList();
        var a = CompetitionRules.Pick(pool, 10, 123);
        Assert.Equal(a, CompetitionRules.Pick(pool, 10, 123));
        Assert.Equal(10, a.Distinct().Count());
        Assert.NotEqual(a, CompetitionRules.Pick(pool, 10, 124));
    }
}
