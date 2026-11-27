using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Application.Features.Progress;

/// <summary>
/// Turns learning events into points, streak days, badges and certificates. Called after the event is saved, and safe to call
/// again: every award is idempotent, so a regraded attempt or a retried request never pays twice.
/// </summary>
public interface IProgressService
{
    Task<PointsEarnedDto> OnLectureCompletedAsync(Guid userId, int roadmapItemId, CancellationToken ct = default);

    /// <summary>After a quiz or exam is closed or (re)graded: daily quiz and exam points, streak, badges, certificate check.</summary>
    Task<PointsEarnedDto> OnAttemptClosedAsync(int attemptId, CancellationToken ct = default);

    Task<PointsEarnedDto> OnPracticeAsync(Guid userId, int languageId, int practiceAttemptId, CancellationToken ct = default);
}

internal sealed class ProgressService(
    IApplicationDbContext db,
    IPointsService points,
    ICertificateService certificates,
    ISettingsService settings,
    TimeProvider clock,
    ILogger<ProgressService> logger) : IProgressService
{
    public const int PracticeRegularCount = 25;

    public Task<PointsEarnedDto> OnLectureCompletedAsync(Guid userId, int roadmapItemId, CancellationToken ct = default) =>
        SafeAsync(() => LectureAsync(userId, roadmapItemId, ct), "lecture", roadmapItemId);

    public Task<PointsEarnedDto> OnAttemptClosedAsync(int attemptId, CancellationToken ct = default) =>
        SafeAsync(() => AttemptAsync(attemptId, ct), "attempt", attemptId);

    public Task<PointsEarnedDto> OnPracticeAsync(Guid userId, int languageId, int practiceAttemptId, CancellationToken ct = default) =>
        SafeAsync(() => PracticeAsync(userId, languageId, practiceAttemptId, ct), "practice", practiceAttemptId);

    /// <summary>Rewards never break the learner's action: the lecture or quiz is already saved when this runs.</summary>
    private async Task<PointsEarnedDto> SafeAsync(Func<Task<PointsEarnedDto>> work, string what, long id)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Points/streak update failed for {What} {Id}", what, id);
            return Empty;
        }
    }

    private async Task<PointsEarnedDto> LectureAsync(Guid userId, int roadmapItemId, CancellationToken ct)
    {
        var item = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.Id == roadmapItemId && i.LearnerCourse.UserId == userId && i.State == RoadmapItemState.Completed)
            .Select(i => new { i.Id, i.PlannedDate, i.CompletedAt, i.LearnerCourseId, i.LearnerCourse.LanguageId })
            .FirstOrDefaultAsync(ct);
        if (item is null) return Empty;

        var before = await BadgeSetAsync(userId, ct);
        var today = await LocalDateAsync(userId, item.CompletedAt ?? clock.GetUtcNow(), ct);
        var earned = 0;

        // FR-S03: 10 for a lecture, once (FR-S05); BR-21: study-ahead bonus for finishing before the planned day.
        earned += await points.AwardAsync(userId, item.LanguageId, PointAction.LectureCompleted, "RoadmapItem", item.Id.ToString(), ct: ct);
        var perDay = await settings.GetIntAsync(SettingKeys.PointsStudyAheadPerDay, 5, ct);
        var max = await settings.GetIntAsync(SettingKeys.PointsStudyAheadMax, 15, ct);
        var bonus = PointMath.StudyAheadBonus(item.PlannedDate, today, perDay, max);
        if (bonus > 0)
            earned += await points.AwardAsync(userId, item.LanguageId, PointAction.StudyAheadBonus, "RoadmapItem", item.Id.ToString(), bonus, ct);

        (int streakPoints, int streak) = await RegisterStreakAsync(userId, today, ct);
        earned += streakPoints;
        await points.GrantBadgeAsync(userId, BadgeKey.FirstLecture, ct);

        await TryCertificateAsync(item.LearnerCourseId, ct);
        return await ResultAsync(userId, earned, streak, before, ct);
    }

    private async Task<PointsEarnedDto> AttemptAsync(int attemptId, CancellationToken ct)
    {
        var a = await db.QuizAttempts.AsNoTracking().Where(x => x.Id == attemptId)
            .Select(x => new
            {
                x.Id, x.UserId, x.Type, x.Status, x.IsPractice, x.IsPassed, x.Percentage, x.QuizId, x.LearnerCourseId, x.SubmittedAt,
                LanguageId = x.Course.LanguageId,
                Answered = x.Answers.Count(y => y.AnsweredAt != null),
                Correct = x.Answers.Count(y => y.IsCorrect == true)
            }).FirstOrDefaultAsync(ct);
        if (a is null || a.Status is not (AttemptStatus.Graded or AttemptStatus.PendingReview)) return Empty;

        var before = await BadgeSetAsync(a.UserId, ct);
        var earned = 0;
        var streak = 0;

        if (a.Type == QuizType.Daily && a.Answered > 0)
        {
            // BR-09: finishing the day's quiz keeps the streak, practice attempt or not.
            (int streakPoints, streak) = await RegisterStreakAsync(a.UserId, await LocalDateAsync(a.UserId, a.SubmittedAt ?? clock.GetUtcNow(), ct), ct);
            earned += streakPoints;

            if (!a.IsPractice && a.Status == AttemptStatus.Graded)
            {
                var perCorrect = await RulePointsAsync(PointAction.DailyQuiz, ct);
                earned += await points.AwardAsync(a.UserId, a.LanguageId, PointAction.DailyQuiz, "QuizAttempt", a.Id.ToString(),
                    PointMath.DailyQuiz(a.Correct, perCorrect), ct);
                if (a.Percentage >= 100)
                    earned += await points.AwardAsync(a.UserId, a.LanguageId, PointAction.DailyQuizPerfect, "QuizAttempt", a.Id.ToString(), ct: ct);
            }
        }
        else if (a.Type == QuizType.Monthly && a.Status == AttemptStatus.Graded && a.IsPassed)
        {
            // Once per exam, whichever attempt passes first; the grade bonus uses the certificate bands (Merit, Distinction ×2).
            var reference = (a.QuizId ?? -a.Id).ToString();
            earned += await points.AwardAsync(a.UserId, a.LanguageId, PointAction.MonthlyExamPassed, "Quiz", reference, ct: ct);
            var band = CertificateRules.Band(a.Percentage,
                await settings.GetIntAsync(SettingKeys.GradeBandDistinction, 90, ct), await settings.GetIntAsync(SettingKeys.GradeBandMerit, 75, ct));
            if (band != GradeBand.Pass)
            {
                var merit = await RulePointsAsync(PointAction.ExamGradeBonus, ct);
                earned += await points.AwardAsync(a.UserId, a.LanguageId, PointAction.ExamGradeBonus, "Quiz", reference,
                    band == GradeBand.Distinction ? merit * 2 : merit, ct);
            }
            await points.GrantBadgeAsync(a.UserId, BadgeKey.FirstExamPassed, ct);
        }

        if (a.LearnerCourseId is { } lcId && a.Type is QuizType.Monthly or QuizType.Daily) await TryCertificateAsync(lcId, ct);
        return await ResultAsync(a.UserId, earned, streak, before, ct);
    }

    private async Task<PointsEarnedDto> PracticeAsync(Guid userId, int languageId, int practiceAttemptId, CancellationToken ct)
    {
        var before = await BadgeSetAsync(userId, ct);
        var earned = await points.AwardAsync(userId, languageId, PointAction.PracticeSession, "PracticeAttempt", practiceAttemptId.ToString(), ct: ct);
        if (await db.PracticeAttempts.CountAsync(p => p.UserId == userId, ct) >= PracticeRegularCount)
            await points.GrantBadgeAsync(userId, BadgeKey.PracticeRegular, ct);
        return await ResultAsync(userId, earned, 0, before, ct);
    }

    // ------------------------------------------------------------------ streak (BR-09)

    private async Task<(int Points, int Current)> RegisterStreakAsync(Guid userId, DateOnly day, CancellationToken ct)
    {
        var streak = await db.UserStreaks.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (streak is null)
        {
            streak = new UserStreak { UserId = userId };
            db.UserStreaks.Add(streak);
        }
        var change = StreakRules.Register(streak, day, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        if (!change.Counted) return (0, streak.CurrentStreak);

        var earned = await points.AwardAsync(userId, null, PointAction.StreakDay, "Streak", day.ToString("yyyy-MM-dd"), ct: ct);
        var start = streak.StreakStartDate?.ToString("yyyy-MM-dd") ?? "-";
        if (change.Current == 7)
            earned += await points.AwardAsync(userId, null, PointAction.StreakWeek, "Streak", $"{start}:7", ct: ct);
        if (change.Current == 30)
            earned += await points.AwardAsync(userId, null, PointAction.StreakMonth, "Streak", $"{start}:30", ct: ct);
        if (change.Current >= 7) await points.GrantBadgeAsync(userId, BadgeKey.Streak7, ct);
        if (change.Current >= 30) await points.GrantBadgeAsync(userId, BadgeKey.Streak30, ct);
        return (earned, change.Current);
    }

    // ------------------------------------------------------------------ helpers

    private async Task TryCertificateAsync(int learnerCourseId, CancellationToken ct)
    {
        try
        {
            await certificates.CheckAndIssueAsync(learnerCourseId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The learner's lecture or exam is saved either way; the admin "check eligibility" button or the next event retries.
            logger.LogError(ex, "Certificate check failed for learner course {LearnerCourseId}", learnerCourseId);
        }
    }

    private async Task<int> RulePointsAsync(PointAction action, CancellationToken ct) =>
        await db.PointRules.Where(r => r.Action == action && r.IsActive).Select(r => (int?)r.Points).FirstOrDefaultAsync(ct) ?? 0;

    private async Task<DateOnly> LocalDateAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        var tz = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, PointsService.Zone(tz)).DateTime);
    }

    private async Task<HashSet<BadgeKey>> BadgeSetAsync(Guid userId, CancellationToken ct) =>
        (await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).Select(b => b.Badge).ToListAsync(ct)).ToHashSet();

    private async Task<PointsEarnedDto> ResultAsync(Guid userId, int earned, int streak, HashSet<BadgeKey> before, CancellationToken ct)
    {
        var after = await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).ToListAsync(ct);
        var added = after.Where(b => !before.Contains(b.Badge)).Select(b => Badges.ToDto(b.Badge, b.EarnedAt)).ToList();
        return new PointsEarnedDto(earned, added, streak);
    }

    private static readonly PointsEarnedDto Empty = new(0, [], 0);
}
