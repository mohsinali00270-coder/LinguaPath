using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Notifications;

/// <summary>Timings for the reminder worker (config section "Reminders").</summary>
public sealed class ReminderOptions
{
    public const string Section = "Reminders";

    /// <summary>Off in tests; the integration tests call the reminder service directly.</summary>
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>A study reminder can go out within this many minutes after the preferred study time.</summary>
    public int StudyWindowMinutes { get; set; } = 60;
    /// <summary>Used when neither the course nor the profile has a preferred study time.</summary>
    public TimeOnly DefaultStudyTime { get; set; } = new(19, 0);
    public TimeOnly QuizReadyFrom { get; set; } = new(17, 0);
    public TimeOnly StreakAlertFrom { get; set; } = new(20, 0);
    public TimeOnly ExamAlertFrom { get; set; } = new(9, 0);
    public int ExamDaysBefore { get; set; } = 3;
    /// <summary>No reminders are created in these local hours (start inclusive, end exclusive; may wrap midnight).</summary>
    public TimeOnly QuietFrom { get; set; } = new(22, 0);
    public TimeOnly QuietUntil { get; set; } = new(8, 0);
    /// <summary>Posts older than this when the worker first sees them are marked as told without notifying anyone.</summary>
    public int NewPostMaxAgeDays { get; set; } = 7;
}

/// <summary>The learner's six notification switches (FR-L05).</summary>
public readonly record struct NotificationSwitches(
    bool StudyReminders, bool QuizReady, bool ExamReminders, bool StreakAlerts, bool Certificates, bool NewPosts);

/// <summary>Pure decisions behind FR-L110 reminders, kept apart from the database so they can be unit-tested.</summary>
public static class ReminderRules
{
    /// <summary>
    /// Whether a notification goes to the inbox and/or as a push. Reminders the learner switched off are not created at all;
    /// certificates and new posts still land in the inbox, the switch only stops the push. Service messages always go.
    /// </summary>
    public static (bool Inbox, bool Push) Delivery(NotificationType type, NotificationSwitches s) => type switch
    {
        NotificationType.StudyReminder => (s.StudyReminders, s.StudyReminders),
        NotificationType.DailyQuizReady => (s.QuizReady, s.QuizReady),
        NotificationType.ExamSoon => (s.ExamReminders, s.ExamReminders),
        NotificationType.StreakAtRisk => (s.StreakAlerts, s.StreakAlerts),
        NotificationType.Certificate => (true, s.Certificates),
        NotificationType.NewPost => (true, s.NewPosts),
        _ => (true, true)
    };

    /// <summary>StudyDays uses Sunday = 1, Monday = 2 … Saturday = 64, i.e. 1 &lt;&lt; DayOfWeek.</summary>
    public static StudyDays DayFlag(DayOfWeek day) => (StudyDays)(1 << (int)day);

    public static bool IsStudyDay(StudyDays days, DayOfWeek day) => days == StudyDays.None || (days & DayFlag(day)) != 0;

    public static bool InQuietHours(TimeOnly local, TimeOnly from, TimeOnly until) =>
        from == until ? false
        : from < until ? local >= from && local < until
        : local >= from || local < until;

    /// <summary>
    /// FR-L110 "push reminders at preferred study time": on a study day, from the study time until the window closes,
    /// when something is due and nothing was studied today.
    /// </summary>
    public static bool StudyReminderDue(StudyDays days, TimeOnly studyTime, DateTime localNow, int windowMinutes,
        bool hasDueLecture, bool studiedToday)
    {
        if (!hasDueLecture || studiedToday) return false;
        if (!IsStudyDay(days, localNow.DayOfWeek)) return false;
        var now = TimeOnly.FromDateTime(localNow);
        var end = studyTime.AddMinutes(windowMinutes, out var wrapped);
        // A window that runs past midnight stops at midnight; tomorrow is another day.
        return wrapped > 0 ? now >= studyTime : now >= studyTime && now < end;
    }

    /// <summary>The streak survives today only if the learner studies before midnight (BR-09).</summary>
    public static bool StreakAtRisk(int currentStreak, DateOnly? lastActivity, DateOnly today, TimeOnly localTime, TimeOnly alertFrom) =>
        currentStreak > 0 && lastActivity == today.AddDays(-1) && localTime >= alertFrom;

    /// <summary>"Exam in 3 days": the exam is between today and <paramref name="daysBefore"/> days away.</summary>
    public static bool ExamSoon(DateOnly? examDate, DateOnly today, int daysBefore) =>
        examDate is { } d && d.DayNumber - today.DayNumber is var days && days >= 0 && days <= daysBefore;

    /// <summary>
    /// The next monthly exam: the first month (in order) that has a published exam the learner hasn't passed.
    /// Its date is the last planned lecture date of that month.
    /// </summary>
    public static (int Month, DateOnly Date)? NextExam(
        IReadOnlyDictionary<int, DateOnly> lastPlannedByMonth, IEnumerable<int> examMonths, IEnumerable<int> passedMonths)
    {
        var passed = passedMonths.ToHashSet();
        foreach (var month in examMonths.Distinct().OrderBy(m => m))
        {
            if (passed.Contains(month)) continue;
            if (lastPlannedByMonth.TryGetValue(month, out var date)) return (month, date);
        }
        return null;
    }

    public static string DayKey(DateOnly day) => day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The learner's local clock, falling back to UTC for unknown zones.</summary>
    public static DateTime LocalNow(DateTimeOffset utcNow, string? timeZoneId)
    {
        var tz = !string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(utcNow, tz).DateTime;
    }

    /// <summary>UTC bounds of the learner's local day, for "did they study today" queries.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) LocalDayUtc(DateOnly day, string? timeZoneId)
    {
        var tz = !string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        DateTimeOffset At(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // Midnight that doesn't exist (a DST jump) moves forward an hour.
            if (tz.IsInvalidTime(local)) local = local.AddHours(1);
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }
        return (At(day), At(day.AddDays(1)));
    }

    public static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
