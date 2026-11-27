using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Notifications;

public sealed record ReminderRunResult(int Study, int DailyQuiz, int Exam, int Streak, int Posts)
{
    public int Total => Study + DailyQuiz + Exam + Streak + Posts;
}

/// <summary>One pass of the FR-L110 reminders. The API runs it every few minutes; tests call it with a chosen time.</summary>
public interface IReminderService
{
    Task<ReminderRunResult> RunAsync(DateTimeOffset utcNow, CancellationToken ct = default);
}

/// <summary>Next monthly exam date of a learner course (shown on Today and used for "exam in 3 days").</summary>
public static class ExamSchedule
{
    public static async Task<(int Month, DateOnly Date)?> NextAsync(IApplicationDbContext db, int learnerCourseId, int courseId, CancellationToken ct)
    {
        var examMonths = await db.Quizzes.AsNoTracking()
            .Where(q => q.CourseId == courseId && q.Type == QuizType.Monthly && q.Status == ContentStatus.Published && !q.IsDeleted)
            .Select(q => q.MonthNo ?? 1).ToListAsync(ct);
        if (examMonths.Count == 0) return null;
        var passed = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.LearnerCourseId == learnerCourseId && a.Type == QuizType.Monthly && a.IsPassed && !a.IsPractice)
            .Select(a => a.MonthNo ?? 1).ToListAsync(ct);
        // Grouped here rather than in SQL: MAX over a DateOnly isn't translated by every provider (SQLite in tests).
        var items = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.LearnerCourseId == learnerCourseId)
            .Select(i => new { i.Lecture.Module.MonthNo, i.PlannedDate })
            .ToListAsync(ct);
        var planned = items.GroupBy(i => i.MonthNo).ToDictionary(g => g.Key, g => g.Max(i => i.PlannedDate));
        return ReminderRules.NextExam(planned, examMonths, passed);
    }
}

internal sealed class ReminderService(
    IApplicationDbContext db,
    INotificationService notifications,
    IAssessmentService assessment,
    IOptions<ReminderOptions> options,
    ILogger<ReminderService> logger) : IReminderService
{
    private readonly ReminderOptions _o = options.Value;

    private sealed record CourseRow(
        int Id, Guid UserId, int CourseId, string CourseTitle, string Language, StudyDays StudyDays,
        TimeOnly? CourseStudyTime, TimeOnly? ProfileStudyTime, string TimeZone, NotificationSwitches Switches);

    public async Task<ReminderRunResult> RunAsync(DateTimeOffset utcNow, CancellationToken ct = default)
    {
        var courses = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.Status == LearnerCourseStatus.Active && !c.User.IsDeleted && c.User.IsActive && !c.User.IsBlocked)
            .Select(c => new CourseRow(c.Id, c.UserId, c.CourseId, c.Course.Title, c.Course.Language.Name, c.StudyDays,
                c.PreferredStudyTime, c.User.PreferredStudyTime, c.User.TimeZone,
                new NotificationSwitches(c.User.NotifyStudyReminders, c.User.NotifyQuizReady, c.User.NotifyExamReminders,
                    c.User.NotifyStreakAlerts, c.User.NotifyCertificates, c.User.NotifyNewPosts)))
            .ToListAsync(ct);

        // Keys written in the last two days: most reminders are skipped without touching the database again.
        var since = utcNow.AddDays(-2);
        var sent = (await db.Notifications.AsNoTracking()
                .Where(n => n.DedupKey != null && n.CreatedAt >= since)
                .Select(n => new { n.UserId, n.DedupKey }).ToListAsync(ct))
            .Select(n => (n.UserId, n.DedupKey!)).ToHashSet();

        int study = 0, quiz = 0, exam = 0, streak = 0;
        foreach (var c in courses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var local = ReminderRules.LocalNow(utcNow, c.TimeZone);
                var localTime = TimeOnly.FromDateTime(local);
                if (ReminderRules.InQuietHours(localTime, _o.QuietFrom, _o.QuietUntil)) continue;
                var today = DateOnly.FromDateTime(local);
                var dayKey = ReminderRules.DayKey(today);

                if (c.Switches.StudyReminders && !sent.Contains((c.UserId, $"study:{c.Id}:{dayKey}")))
                    study += await StudyAsync(c, local, today, dayKey, ct);

                if (c.Switches.QuizReady && localTime >= _o.QuizReadyFrom && !sent.Contains((c.UserId, $"quiz:{c.Id}:{dayKey}")))
                    quiz += await QuizAsync(c, dayKey, ct);

                if (c.Switches.ExamReminders && localTime >= _o.ExamAlertFrom)
                    exam += await ExamAsync(c, today, sent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One learner's bad data shouldn't stop everybody else's reminders.
                logger.LogWarning(ex, "Reminders failed for learner course {Id}", c.Id);
            }
        }

        streak = await StreaksAsync(courses, utcNow, sent, ct);
        var posts = await NewPostsAsync(utcNow, ct);
        var result = new ReminderRunResult(study, quiz, exam, streak, posts);
        if (result.Total > 0) logger.LogInformation("Reminders sent: {@Result}", result);
        return result;
    }

    private async Task<int> StudyAsync(CourseRow c, DateTime local, DateOnly today, string dayKey, CancellationToken ct)
    {
        var studyTime = c.CourseStudyTime ?? c.ProfileStudyTime ?? _o.DefaultStudyTime;
        // Cheap checks first: most runs are outside the learner's window.
        if (!ReminderRules.StudyReminderDue(c.StudyDays, studyTime, local, _o.StudyWindowMinutes, hasDueLecture: true, studiedToday: false))
            return 0;

        var hasDue = await db.LearnerRoadmapItems.AnyAsync(i => i.LearnerCourseId == c.Id && i.State != RoadmapItemState.Completed
                                                                && i.PlannedDate <= today, ct);
        var (start, end) = ReminderRules.LocalDayUtc(today, c.TimeZone);
        var studied = await db.LearnerRoadmapItems.AnyAsync(i => i.LearnerCourseId == c.Id && i.CompletedAt >= start && i.CompletedAt < end, ct);
        if (!ReminderRules.StudyReminderDue(c.StudyDays, studyTime, local, _o.StudyWindowMinutes, hasDue, studied)) return 0;

        return await notifications.NotifyAsync([c.UserId], NotificationType.StudyReminder, $"Time for {c.Language} 📚",
            $"Your next lecture in {c.CourseTitle} is waiting.",
            new Dictionary<string, string> { ["type"] = "today", ["learnerCourseId"] = c.Id.ToString() },
            $"study:{c.Id}:{dayKey}", ct);
    }

    private async Task<int> QuizAsync(CourseRow c, string dayKey, CancellationToken ct)
    {
        var daily = await assessment.GetDailyQuizAsync(c.UserId, c.Id, ct);
        if (daily.IsFailure || daily.Value!.State != DailyQuizState.Ready) return 0;
        return await notifications.NotifyAsync([c.UserId], NotificationType.DailyQuizReady, "Your daily quiz is ready ✅",
            $"{daily.Value.QuestionCount} quick questions from your recent {c.Language} lectures.",
            new Dictionary<string, string> { ["type"] = "dailyQuiz", ["learnerCourseId"] = c.Id.ToString() },
            $"quiz:{c.Id}:{dayKey}", ct);
    }

    private async Task<int> ExamAsync(CourseRow c, DateOnly today, HashSet<(Guid, string)> sent, CancellationToken ct)
    {
        var next = await ExamSchedule.NextAsync(db, c.Id, c.CourseId, ct);
        if (next is not { } e || !ReminderRules.ExamSoon(e.Date, today, _o.ExamDaysBefore)) return 0;
        // One reminder per exam (and again if the date moves after a pause).
        var key = $"exam:{c.Id}:{e.Month}:{ReminderRules.DayKey(e.Date)}";
        if (sent.Contains((c.UserId, key))) return 0;
        var days = e.Date.DayNumber - today.DayNumber;
        var when = days switch { 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };
        return await notifications.NotifyAsync([c.UserId], NotificationType.ExamSoon, $"Month {e.Month} exam {when} 📝",
            $"Your {c.Language} monthly exam is planned for {e.Date:d MMM}. A quick revision now helps.",
            new Dictionary<string, string> { ["type"] = "exams", ["learnerCourseId"] = c.Id.ToString(), ["month"] = e.Month.ToString() },
            key, ct);
    }

    private async Task<int> StreaksAsync(List<CourseRow> courses, DateTimeOffset utcNow, HashSet<(Guid, string)> sent, CancellationToken ct)
    {
        var learners = courses.Where(c => c.Switches.StreakAlerts).GroupBy(c => c.UserId).Select(g => g.First()).ToList();
        if (learners.Count == 0) return 0;
        var count = 0;
        foreach (var chunk in learners.Chunk(500))
        {
            var ids = chunk.Select(c => c.UserId).ToList();
            var streaks = await db.UserStreaks.AsNoTracking().Where(s => ids.Contains(s.UserId) && s.CurrentStreak > 0)
                .ToDictionaryAsync(s => s.UserId, ct);
            foreach (var c in chunk)
            {
                if (!streaks.TryGetValue(c.UserId, out var s)) continue;
                var local = ReminderRules.LocalNow(utcNow, c.TimeZone);
                var localTime = TimeOnly.FromDateTime(local);
                if (ReminderRules.InQuietHours(localTime, _o.QuietFrom, _o.QuietUntil)) continue;
                var today = DateOnly.FromDateTime(local);
                var key = $"streak:{ReminderRules.DayKey(today)}";
                if (sent.Contains((c.UserId, key))) continue;
                if (!ReminderRules.StreakAtRisk(s.CurrentStreak, s.LastActivityDate, today, localTime, _o.StreakAlertFrom)) continue;
                count += await notifications.NotifyAsync([c.UserId], NotificationType.StreakAtRisk, $"Keep your {s.CurrentStreak}-day streak 🔥",
                    "One short lecture before midnight keeps it going.",
                    new Dictionary<string, string> { ["type"] = "today", ["learnerCourseId"] = c.Id.ToString() }, key, ct);
            }
        }
        return count;
    }

    private async Task<int> NewPostsAsync(DateTimeOffset utcNow, CancellationToken ct)
    {
        var cutoff = utcNow.AddDays(-_o.NewPostMaxAgeDays);
        var count = 0;
        // Batches of 20 until every due post is marked, so a backlog (first run after an upgrade) is cleared in one pass.
        for (var batch = 0; batch < 50; batch++)
        {
            var due = await db.Posts
                .Where(p => p.Status == ContentStatus.Published && !p.IsDeleted && p.NotifiedAt == null && (p.PublishAt == null || p.PublishAt <= utcNow))
                .OrderBy(p => p.Id)
                .Select(p => new { Post = p, Language = p.Language.Name })
                .Take(20).ToListAsync(ct);
            if (due.Count == 0) break;

            foreach (var d in due)
            {
                var p = d.Post;
                p.NotifiedAt = utcNow;
                // Old posts are marked without telling anyone.
                if ((p.PublishAt ?? p.CreatedAt) < cutoff) continue;

                var learners = await db.LearnerCourses.AsNoTracking()
                    .Where(c => c.LanguageId == p.LanguageId && c.Status == LearnerCourseStatus.Active && !c.User.IsDeleted && c.User.IsActive)
                    .Select(c => c.UserId).Distinct().ToListAsync(ct);
                await notifications.NotifyAsync(learners, NotificationType.NewPost, $"New in {d.Language}: {p.Title}",
                    string.IsNullOrWhiteSpace(p.Body) ? "Tap to read it." : ReminderRules.Trim(StripTags(p.Body), 160),
                    new Dictionary<string, string> { ["type"] = "post", ["postId"] = p.Id.ToString() },
                    $"post:{p.Id}", ct);
                count++;
            }
            await db.SaveChangesAsync(ct);
        }
        return count;
    }

    private static string StripTags(string html) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "))
            .Replace("  ", " ").Trim();
}
