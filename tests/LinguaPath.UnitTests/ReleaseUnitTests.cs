using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Notifications;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 10 pure rules: reminder timing, notification switches, BR-32 ages and report maths.</summary>
public sealed class ReleaseUnitTests
{
    private static readonly NotificationSwitches AllOn = new(true, true, true, true, true, true);
    private static readonly NotificationSwitches AllOff = new(false, false, false, false, false, false);

    // ------------------------------------------------------------------ switches

    [Theory]
    [InlineData(NotificationType.StudyReminder)]
    [InlineData(NotificationType.DailyQuizReady)]
    [InlineData(NotificationType.ExamSoon)]
    [InlineData(NotificationType.StreakAtRisk)]
    public void Reminders_switched_off_are_not_created_at_all(NotificationType type)
    {
        Assert.Equal((true, true), ReminderRules.Delivery(type, AllOn));
        Assert.Equal((false, false), ReminderRules.Delivery(type, AllOff));
    }

    [Theory]
    [InlineData(NotificationType.Certificate)]
    [InlineData(NotificationType.NewPost)]
    public void Certificates_and_posts_still_reach_the_inbox_when_push_is_off(NotificationType type) =>
        Assert.Equal((true, false), ReminderRules.Delivery(type, AllOff));

    [Fact]
    public void Service_messages_always_go() => Assert.Equal((true, true), ReminderRules.Delivery(NotificationType.System, AllOff));

    // ------------------------------------------------------------------ study days and quiet hours

    [Fact]
    public void Day_flags_match_the_study_days_enum()
    {
        Assert.Equal(StudyDays.Sunday, ReminderRules.DayFlag(DayOfWeek.Sunday));
        Assert.Equal(StudyDays.Saturday, ReminderRules.DayFlag(DayOfWeek.Saturday));
        Assert.True(ReminderRules.IsStudyDay(StudyDays.Weekdays, DayOfWeek.Wednesday));
        Assert.False(ReminderRules.IsStudyDay(StudyDays.Weekdays, DayOfWeek.Sunday));
        Assert.True(ReminderRules.IsStudyDay(StudyDays.None, DayOfWeek.Sunday)); // no days picked = every day
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(21, 59, false)]
    public void Quiet_hours_wrap_past_midnight(int h, int m, bool quiet) =>
        Assert.Equal(quiet, ReminderRules.InQuietHours(new TimeOnly(h, m), new TimeOnly(22, 0), new TimeOnly(8, 0)));

    [Fact]
    public void Quiet_hours_within_a_day_and_disabled()
    {
        Assert.True(ReminderRules.InQuietHours(new TimeOnly(13, 0), new TimeOnly(12, 0), new TimeOnly(14, 0)));
        Assert.False(ReminderRules.InQuietHours(new TimeOnly(15, 0), new TimeOnly(12, 0), new TimeOnly(14, 0)));
        Assert.False(ReminderRules.InQuietHours(new TimeOnly(3, 0), new TimeOnly(0, 0), new TimeOnly(0, 0)));
    }

    // ------------------------------------------------------------------ study reminder

    private static readonly DateTime Wednesday1910 = new(2026, 11, 18, 19, 10, 0);

    [Fact]
    public void Study_reminder_opens_at_the_study_time_for_the_window()
    {
        var at = new TimeOnly(19, 0);
        Assert.True(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, Wednesday1910, 60, true, false));
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, Wednesday1910.AddMinutes(-20), 60, true, false));
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, Wednesday1910.AddMinutes(55), 60, true, false));
    }

    [Fact]
    public void Study_reminder_skips_rest_days_done_days_and_nothing_due()
    {
        var at = new TimeOnly(19, 0);
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Monday | StudyDays.Friday, at, Wednesday1910, 60, true, false));
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, Wednesday1910, 60, true, studiedToday: true));
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, Wednesday1910, 60, hasDueLecture: false, false));
    }

    [Fact]
    public void A_late_window_stops_at_midnight()
    {
        var at = new TimeOnly(23, 30);
        Assert.True(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, new DateTime(2026, 11, 18, 23, 45, 0), 60, true, false));
        Assert.False(ReminderRules.StudyReminderDue(StudyDays.Everyday, at, new DateTime(2026, 11, 19, 0, 15, 0), 60, true, false));
    }

    // ------------------------------------------------------------------ streak and exam

    [Fact]
    public void Streak_is_at_risk_in_the_evening_after_yesterday_only()
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.True(ReminderRules.StreakAtRisk(4, today.AddDays(-1), today, new TimeOnly(20, 30), new TimeOnly(20, 0)));
        Assert.False(ReminderRules.StreakAtRisk(4, today.AddDays(-1), today, new TimeOnly(19, 30), new TimeOnly(20, 0)));
        Assert.False(ReminderRules.StreakAtRisk(4, today, today, new TimeOnly(20, 30), new TimeOnly(20, 0)));          // studied today
        Assert.False(ReminderRules.StreakAtRisk(4, today.AddDays(-2), today, new TimeOnly(20, 30), new TimeOnly(20, 0))); // already broken
        Assert.False(ReminderRules.StreakAtRisk(0, today.AddDays(-1), today, new TimeOnly(20, 30), new TimeOnly(20, 0)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void Exam_soon_is_within_three_days(int daysAway, bool soon)
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.Equal(soon, ReminderRules.ExamSoon(today.AddDays(daysAway), today, 3));
    }

    [Fact]
    public void Next_exam_is_the_first_unpassed_month_with_an_exam()
    {
        var planned = new Dictionary<int, DateOnly>
        {
            [1] = new(2026, 12, 1), [2] = new(2027, 1, 1), [3] = new(2027, 2, 1)
        };
        Assert.Equal((1, new DateOnly(2026, 12, 1)), ReminderRules.NextExam(planned, [3, 1, 2], []));
        Assert.Equal((3, new DateOnly(2027, 2, 1)), ReminderRules.NextExam(planned, [1, 3], [1]));
        Assert.Null(ReminderRules.NextExam(planned, [1, 2], [1, 2]));
        Assert.Null(ReminderRules.NextExam(planned, [], []));
        Assert.Null(ReminderRules.NextExam(new Dictionary<int, DateOnly>(), [1], [])); // month without lectures
    }

    // ------------------------------------------------------------------ time zones

    [Fact]
    public void Local_day_bounds_follow_the_learners_zone()
    {
        var (start, end) = ReminderRules.LocalDayUtc(new DateOnly(2026, 11, 18), "Asia/Karachi"); // UTC+5
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 19, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromDays(1), end - start);

        var utc = ReminderRules.LocalDayUtc(new DateOnly(2026, 11, 18), "Not/AZone");
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), utc.Start);
    }

    [Fact]
    public void Local_now_converts_and_falls_back_to_utc()
    {
        var utcNow = new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(19, ReminderRules.LocalNow(utcNow, "Asia/Karachi").Hour);
        Assert.Equal(14, ReminderRules.LocalNow(utcNow, null).Hour);
    }

    [Fact]
    public void Trim_keeps_short_text_and_cuts_long_text_with_an_ellipsis()
    {
        Assert.Equal("Hola", ReminderRules.Trim("Hola", 10));
        var cut = ReminderRules.Trim(new string('a', 50), 10);
        Assert.Equal(10, cut.Length);
        Assert.EndsWith("…", cut);
    }

    // ------------------------------------------------------------------ BR-32

    [Fact]
    public void Ages_count_whole_years_and_birthdays()
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.Equal(13, AgeRules.Age(new DateOnly(2013, 11, 18), today));
        Assert.Equal(12, AgeRules.Age(new DateOnly(2013, 11, 19), today));
        Assert.True(AgeRules.IsTooYoung(new DateOnly(2013, 11, 19), today));
        Assert.False(AgeRules.IsTooYoung(new DateOnly(2013, 11, 18), today));
        Assert.True(AgeRules.IsMinor(new DateOnly(2009, 1, 1), today));
        Assert.False(AgeRules.IsMinor(new DateOnly(2008, 11, 18), today));
        Assert.False(AgeRules.IsMinor(null, today)); // not given = treated as an adult
        Assert.False(AgeRules.IsTooYoung(null, today));
    }

    [Fact]
    public void Leap_day_birthdays_and_impossible_dates()
    {
        // Born 29 Feb: the birthday counts on 28 Feb in common years (DateOnly.AddYears).
        Assert.Equal(13, AgeRules.Age(new DateOnly(2012, 2, 29), new DateOnly(2025, 2, 28)));
        var today = new DateOnly(2026, 11, 18);
        Assert.False(AgeRules.IsPlausible(today.AddDays(1), today));
        Assert.False(AgeRules.IsPlausible(today.AddYears(-121), today));
        Assert.True(AgeRules.IsPlausible(new DateOnly(1990, 5, 1), today));
    }

    // ------------------------------------------------------------------ FR-A100 report maths

    [Fact]
    public void Percent_and_average_round_to_one_decimal_and_handle_zero()
    {
        Assert.Equal(33.3, ReportMath.Percent(1, 3));
        Assert.Equal(0, ReportMath.Percent(5, 0));
        Assert.Equal(66.7, ReportMath.Average([50, 75, 75]));
        Assert.Equal(0, ReportMath.Average([]));
    }

    [Fact]
    public void Drop_off_compares_each_lecture_with_the_one_before()
    {
        var drop = ReportMath.DropOff([(100, 80), (80, 60), (60, 60), (60, 0)]);
        Assert.Equal([20.0, 25.0, 0.0, 100.0], drop);
        Assert.Empty(ReportMath.DropOff([]));
        Assert.Equal([0.0], ReportMath.DropOff([(0, 0)]));
    }
}
