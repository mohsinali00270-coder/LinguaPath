using LinguaPath.Application.Features.Learning;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Pacing rules (BR-10), feasibility check (FR-L15) and optional-lecture dropping (FR-A32).</summary>
public class RoadmapPlannerTests
{
    // Monday 2 March 2026
    private static readonly DateOnly Monday = new(2026, 3, 2);

    private static List<PlannerLecture> Lectures(params (int Minutes, bool Core)[] items) =>
        items.Select((x, i) => new PlannerLecture(i + 1, (i / 3) + 1, x.Minutes, x.Core)).ToList();

    [Fact]
    public void Everything_fits_and_days_are_filled_up_to_the_daily_minutes()
    {
        var lectures = Lectures((10, true), (10, true), (15, true), (20, true));
        var plan = RoadmapPlanner.Plan(lectures, Monday, Monday.AddDays(30), dailyMinutes: 20, StudyDays.Weekdays);

        Assert.Equal(FeasibilityStatus.Ok, plan.Status);
        Assert.Equal(4, plan.Lectures.Count);
        // 10+10 on Monday, 15 Tuesday, 20 Wednesday
        Assert.Equal(new[] { Monday, Monday, Monday.AddDays(1), Monday.AddDays(2) }, plan.Lectures.Select(l => l.Date));
    }

    [Fact]
    public void Only_study_days_are_used()
    {
        var lectures = Lectures((30, true), (30, true), (30, true), (30, true));
        var plan = RoadmapPlanner.Plan(lectures, Monday, Monday.AddDays(60), 30, StudyDays.Monday | StudyDays.Thursday);

        Assert.All(plan.Lectures, l => Assert.Contains(l.Date.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Thursday }));
        Assert.Equal(Monday.AddDays(10), plan.EstimatedFinish); // Mon, Thu, Mon, Thu
    }

    [Fact]
    public void A_lecture_longer_than_the_daily_time_gets_a_day_of_its_own()
    {
        var plan = RoadmapPlanner.Plan(Lectures((45, true), (10, true)), Monday, Monday.AddDays(10), 20, StudyDays.Everyday);
        Assert.Equal(Monday, plan.Lectures[0].Date);
        Assert.Equal(Monday.AddDays(1), plan.Lectures[1].Date);
    }

    [Fact]
    public void Optional_lectures_are_dropped_first_when_time_is_short()
    {
        // 3 study days of 20 minutes; core needs 3 days, one optional does not fit.
        var lectures = Lectures((20, true), (20, false), (20, true), (20, true));
        var plan = RoadmapPlanner.Plan(lectures, Monday, Monday.AddDays(2), 20, StudyDays.Everyday);

        Assert.Equal(FeasibilityStatus.OptionalDropped, plan.Status);
        Assert.Equal(1, plan.SkippedOptional);
        Assert.DoesNotContain(plan.Lectures, l => l.LectureId == 2);
        Assert.True(plan.EstimatedFinish <= Monday.AddDays(2));
    }

    [Fact]
    public void Optional_lectures_that_still_fit_are_kept()
    {
        // 3 days of 30 minutes. Core (3 × 20) needs 3 days; the 10-minute optional shares day 1, the 30-minute one would need a 4th day.
        var lectures = Lectures((20, true), (10, false), (20, true), (20, true), (30, false));
        var plan = RoadmapPlanner.Plan(lectures, Monday, Monday.AddDays(2), 30, StudyDays.Everyday);

        Assert.Equal(FeasibilityStatus.OptionalDropped, plan.Status);
        Assert.Contains(plan.Lectures, l => l.LectureId == 2);      // the short optional one fits
        Assert.DoesNotContain(plan.Lectures, l => l.LectureId == 5); // the long one does not
    }

    [Fact]
    public void Not_feasible_suggests_more_minutes_and_a_later_end_date()
    {
        var lectures = Enumerable.Range(1, 20).Select(i => new PlannerLecture(i, 1, 20, true)).ToList();
        var plan = RoadmapPlanner.Plan(lectures, Monday, Monday.AddDays(6), 20, StudyDays.Everyday);

        Assert.Equal(FeasibilityStatus.NotFeasible, plan.Status);
        Assert.Equal(20, plan.Lectures.Count); // every core lecture is still planned
        Assert.True(plan.SuggestedEndDate > Monday.AddDays(6));
        Assert.Equal(60, plan.SuggestedDailyMinutes); // 3 lectures a day × 7 days = 21 ≥ 20
        Assert.Contains("needs more time", RoadmapPlanner.Describe(plan, Monday.AddDays(6)));
    }

    [Fact]
    public void Planner_rejects_no_study_days()
    {
        Assert.Throws<ArgumentException>(() =>
            RoadmapPlanner.Plan(Lectures((10, true)), Monday, Monday.AddDays(10), 20, StudyDays.None));
    }

    [Theory]
    [InlineData(1, "2026-04-01")]
    [InlineData(3, "2026-06-01")]
    [InlineData(12, "2027-03-01")]
    public void End_date_is_the_day_before_the_same_date_n_months_later(int months, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), RoadmapPlanner.EndDateFor(Monday, months));
    }

    [Fact]
    public void Counts_study_days_inclusive()
    {
        Assert.Equal(5, RoadmapPlanner.CountStudyDays(Monday, Monday.AddDays(6), StudyDays.Weekdays));
        Assert.Equal(7, RoadmapPlanner.CountStudyDays(Monday, Monday.AddDays(6), StudyDays.Everyday));
        Assert.Equal(5, StudyDays.Weekdays.CountPerWeek());
    }

    [Fact]
    public void Learner_today_follows_their_time_zone()
    {
        // 23:30 UTC on 1 March is already 2 March in Karachi (UTC+5).
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 3, 2), LearnerClock.Today(clock, "Asia/Karachi"));
        Assert.Equal(new DateOnly(2026, 3, 1), LearnerClock.Today(clock, "UTC"));
        Assert.Equal(new DateOnly(2026, 3, 1), LearnerClock.Today(clock, "Not/AZone"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
