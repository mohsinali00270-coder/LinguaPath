using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 7: points maths (FR-S03 .. FR-S06, BR-21, BR-22).</summary>
public class PointMathTests
{
    [Theory]
    [InlineData(0, PointTier.Bronze)]
    [InlineData(999, PointTier.Bronze)]
    [InlineData(1_000, PointTier.Silver)]
    [InlineData(4_999, PointTier.Silver)]
    [InlineData(5_000, PointTier.Gold)]
    [InlineData(15_000, PointTier.Platinum)]
    [InlineData(40_000, PointTier.Diamond)]
    [InlineData(1_000_000, PointTier.Diamond)]
    public void Tiers_follow_the_thresholds(int total, PointTier expected) => Assert.Equal(expected, PointMath.TierFor(total));

    [Fact]
    public void Next_tier_progress()
    {
        var (next, toGo, percent) = PointMath.NextTier(3_000);
        Assert.Equal(PointTier.Gold, next);
        Assert.Equal(2_000, toGo);
        Assert.Equal(50, percent);           // 2,000 of the 4,000 between Silver and Gold
        var top = PointMath.NextTier(50_000);
        Assert.Null(top.Next);
        Assert.Null(top.ToGo);
        Assert.Equal(100, top.ProgressPercent);
    }

    [Fact]
    public void Daily_cap_clamps_and_excludes_exams_certificates_prizes_and_adjustments()
    {
        Assert.Equal(10, PointMath.UnderCap(10, 400, 500));
        Assert.Equal(3, PointMath.UnderCap(10, 497, 500));
        Assert.Equal(0, PointMath.UnderCap(10, 500, 500));
        Assert.Equal(10, PointMath.UnderCap(10, 9_999, 0));   // 0 = no cap
        Assert.True(PointMath.IsCapped(PointAction.LectureCompleted));
        Assert.True(PointMath.IsCapped(PointAction.StreakDay));
        Assert.False(PointMath.IsCapped(PointAction.MonthlyExamPassed));
        Assert.False(PointMath.IsCapped(PointAction.ExamGradeBonus));
        Assert.False(PointMath.IsCapped(PointAction.CertificateEarned));
        Assert.False(PointMath.IsCapped(PointAction.TournamentPrize));
        Assert.False(PointMath.IsCapped(PointAction.AdminAdjustment));
    }

    [Fact]
    public void Study_ahead_bonus_is_5_a_day_up_to_15_and_nothing_on_or_after_the_day()
    {
        var planned = new DateOnly(2026, 11, 20);
        Assert.Equal(0, PointMath.StudyAheadBonus(planned, planned, 5, 15));
        Assert.Equal(0, PointMath.StudyAheadBonus(planned, planned.AddDays(3), 5, 15));
        Assert.Equal(5, PointMath.StudyAheadBonus(planned, planned.AddDays(-1), 5, 15));
        Assert.Equal(15, PointMath.StudyAheadBonus(planned, planned.AddDays(-3), 5, 15));
        Assert.Equal(15, PointMath.StudyAheadBonus(planned, planned.AddDays(-10), 5, 15));
    }

    [Fact]
    public void Weekly_boards_start_on_monday_utc_and_monthly_on_the_first()
    {
        var sunday = new DateTimeOffset(2026, 11, 15, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), PointMath.WeekStart(sunday));
        var monday = new DateTimeOffset(2026, 11, 16, 0, 0, 1, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), PointMath.WeekStart(monday));
        // A local time is converted to UTC first: Monday 03:00 in Karachi is still Sunday in UTC.
        var karachi = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), PointMath.WeekStart(karachi));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), PointMath.MonthStart(sunday));
    }

    [Fact]
    public void Daily_quiz_points_and_reference_keys()
    {
        Assert.Equal(8, PointMath.DailyQuiz(4, 2));
        Assert.Equal(0, PointMath.DailyQuiz(-1, 2));
        Assert.Equal("LectureCompleted:RoadmapItem:7", PointMath.Key(PointAction.LectureCompleted, "RoadmapItem", "7"));
        Assert.Equal("Exam passed", PointMath.Describe(PointAction.AdminAdjustment, "Exam passed"));
        Assert.Equal("Daily streak", PointMath.Describe(PointAction.StreakDay, null));
    }
}

/// <summary>Phase 7: streak (BR-09) and badges (FR-L111).</summary>
public class StreakRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateOnly Day = new(2026, 11, 10);

    [Fact]
    public void Consecutive_days_grow_the_streak_same_day_counts_once_and_a_gap_restarts()
    {
        var s = new UserStreak();
        Assert.True(StreakRules.Register(s, Day, Now).Counted);
        Assert.False(StreakRules.Register(s, Day, Now).Counted);
        StreakRules.Register(s, Day.AddDays(1), Now);
        StreakRules.Register(s, Day.AddDays(2), Now);
        Assert.Equal(3, s.CurrentStreak);
        Assert.Equal(Day, s.StreakStartDate);

        var restart = StreakRules.Register(s, Day.AddDays(5), Now);
        Assert.True(restart.Counted);
        Assert.False(restart.Continued);
        Assert.Equal(1, s.CurrentStreak);
        Assert.Equal(3, s.LongestStreak);
        Assert.Equal(Day.AddDays(5), s.StreakStartDate);

        // A late event for an earlier day (e.g. a regraded quiz) changes nothing.
        Assert.False(StreakRules.Register(s, Day.AddDays(4), Now).Counted);
        Assert.Equal(1, s.CurrentStreak);
    }

    [Fact]
    public void View_keeps_the_streak_until_a_whole_day_is_missed()
    {
        var s = new UserStreak { CurrentStreak = 5, LongestStreak = 9, LastActivityDate = Day };
        var today = StreakRules.View(s, Day);
        Assert.Equal(5, today.Current);
        Assert.True(today.ActiveToday);
        Assert.False(today.AtRisk);

        var tomorrow = StreakRules.View(s, Day.AddDays(1));
        Assert.Equal(5, tomorrow.Current);
        Assert.True(tomorrow.AtRisk);

        var broken = StreakRules.View(s, Day.AddDays(2));
        Assert.Equal(0, broken.Current);
        Assert.Equal(9, broken.Longest);
        Assert.Equal(0, StreakRules.View(null, Day).Current);
    }

    [Fact]
    public void Tier_badges_accumulate()
    {
        Assert.Empty(Badges.ForTier(PointTier.Bronze));
        Assert.Equal(new[] { BadgeKey.TierSilver, BadgeKey.TierGold }, Badges.ForTier(PointTier.Gold));
        Assert.Equal(4, Badges.ForTier(PointTier.Diamond).Count());
        Assert.All(Enum.GetValues<BadgeKey>(), k => Assert.Equal(k, Badges.Get(k).Key));
    }
}

/// <summary>Phase 7: certificate rules (BR-06 .. BR-08, numbering).</summary>
public class CertificateRulesTests
{
    [Fact]
    public void Eligibility_needs_every_part_of_br06()
    {
        var ok = new CertificateRules.Facts(10, 10, 3, 3, 72, 60, true);
        Assert.True(CertificateRules.Eligible(ok));
        Assert.False(CertificateRules.Eligible(ok with { CoreDone = 9 }));
        Assert.False(CertificateRules.Eligible(ok with { ExamsPassed = 2 }));
        Assert.False(CertificateRules.Eligible(ok with { CourseGrade = 59 }));
        Assert.False(CertificateRules.Eligible(ok with { CourseGrade = null }));
        Assert.False(CertificateRules.Eligible(ok with { GoodStanding = false }));
        Assert.False(CertificateRules.Eligible(ok with { CoreDone = 0, CoreTotal = 0 }));   // an empty course earns nothing
        Assert.True(CertificateRules.Eligible(ok with { ExamsPassed = 0, ExamsTotal = 0 })); // no exams set up: lectures and grade decide
    }

    [Theory]
    [InlineData(95, GradeBand.Distinction)]
    [InlineData(90, GradeBand.Distinction)]
    [InlineData(89, GradeBand.Merit)]
    [InlineData(75, GradeBand.Merit)]
    [InlineData(74, GradeBand.Pass)]
    [InlineData(60, GradeBand.Pass)]
    public void Grade_bands(int grade, GradeBand band) => Assert.Equal(band, CertificateRules.Band(grade, 90, 75));

    [Fact]
    public void Numbers_follow_lp_lang_year_sequence()
    {
        Assert.Equal("LP-ES-2026-000123", CertificateRules.Number("LP", "es", 2026, 123));
        Assert.Equal("LP-ZHH-2026-000001", CertificateRules.Number("LP", "zh-Hans", 2026, 1));
        var prefix = CertificateRules.NumberPrefix("LP", "es", 2026);
        Assert.Equal(123, CertificateRules.SequenceOf("LP-ES-2026-000123", prefix));
        Assert.Equal(0, CertificateRules.SequenceOf("LP-FR-2026-000123", prefix));
        Assert.Equal(0, CertificateRules.SequenceOf("LP-ES-2026-12a", prefix));
    }

    [Fact]
    public void Verification_codes_are_ten_unambiguous_characters()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => CertificateRules.NewVerificationCode()).ToList();
        Assert.All(codes, c => Assert.Equal(10, c.Length));
        Assert.All(codes, c => Assert.DoesNotContain(c, ch => "01ILO".Contains(ch)));
        Assert.True(codes.Distinct().Count() > 195);
    }

    [Theory]
    [InlineData("2026-01-01", "2026-01-01", "1 day")]
    [InlineData("2026-01-01", "2026-01-10", "10 days")]
    [InlineData("2026-01-01", "2026-02-10", "5 weeks")]
    [InlineData("2026-01-05", "2026-07-05", "6 months")]
    [InlineData("2026-01-05", "2026-07-04", "5 months")]
    [InlineData("2025-01-05", "2026-03-05", "1 year 2 months")]
    [InlineData("2024-01-05", "2026-01-05", "2 years")]
    public void Duration_text(string start, string end, string expected) =>
        Assert.Equal(expected, CertificateRules.Duration(DateOnly.Parse(start), DateOnly.Parse(end)));

    [Fact]
    public void Placeholders_are_filled_case_insensitively()
    {
        var text = CertificateRules.Fill("{Name} reached {level} in {Language}.",
            new Dictionary<string, string> { ["Name"] = "Ana", ["Level"] = "B1", ["Language"] = "Spanish" });
        Assert.Equal("Ana reached B1 in Spanish.", text);
    }
}
