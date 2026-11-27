using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Learning;

public interface ILearnerCourseService
{
    Task<Result<FeasibilityResponse>> PreviewAsync(Guid userId, LearningPlanRequest request, CancellationToken ct = default);
    Task<Result<EnrolResponse>> EnrolAsync(Guid userId, LearningPlanRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<LearnerCourseDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<Result<LearnerCourseDto>> GetAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<TodayDto>> TodayAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<RoadmapDto>> RoadmapAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<EnrolResponse>> UpdatePlanAsync(Guid userId, int id, UpdateLearningPlanRequest request, CancellationToken ct = default);
    Task<Result<LearnerCourseDto>> PauseAsync(Guid userId, int id, PauseCourseRequest request, CancellationToken ct = default);
    Task<Result<LearnerCourseDto>> ResumeAsync(Guid userId, int id, CancellationToken ct = default);

    /// <summary>
    /// Re-plans every active learner of a course after its published content changed (a lecture published, unpublished,
    /// deleted or moved). Completed lectures are kept. Returns how many learners were updated.
    /// </summary>
    Task<int> SyncCourseAsync(int courseId, CancellationToken ct = default);
}

/// <summary>Onboarding, personal roadmap, pacing, pause/resume (FR-L11 .. FR-L16, FR-L20 .. FR-L31, FR-L120, FR-L121).</summary>
internal sealed class LearnerCourseService(IApplicationDbContext db, ISettingsService settings, TimeProvider clock) : ILearnerCourseService
{
    private const string NoCourseCode = "course_not_found";

    // ================================================================== onboarding

    public async Task<Result<FeasibilityResponse>> PreviewAsync(Guid userId, LearningPlanRequest request, CancellationToken ct = default)
    {
        var setup = await PrepareAsync(userId, request, ct);
        if (setup.IsFailure) return setup.Error!;
        var (_, lectures, start, end) = setup.Value;
        var plan = RoadmapPlanner.Plan(lectures, start, end, request.DailyMinutes, request.StudyDays);
        return ToFeasibility(plan, start, end);
    }

    public async Task<Result<EnrolResponse>> EnrolAsync(Guid userId, LearningPlanRequest request, CancellationToken ct = default)
    {
        var setup = await PrepareAsync(userId, request, ct);
        if (setup.IsFailure) return setup.Error!;
        var (course, lectures, start, end) = setup.Value;

        // FR-L11: several courses are allowed, but only one active course per target language.
        var existing = await db.LearnerCourses.AnyAsync(c => c.UserId == userId && c.LanguageId == course.LanguageId &&
            (c.Status == LearnerCourseStatus.Active || c.Status == LearnerCourseStatus.Paused), ct);
        if (existing)
            return Error.Conflict("course_already_active", $"You're already learning {course.Language.Name}. Change that plan from its settings instead.");

        var plan = RoadmapPlanner.Plan(lectures, start, end, request.DailyMinutes, request.StudyDays);
        var now = clock.GetUtcNow();
        var learnerCourse = new LearnerCourse
        {
            UserId = userId,
            CourseId = course.Id,
            LanguageId = course.LanguageId,
            CurrentLevel = request.CurrentLevel,
            TargetLevel = request.TargetLevel,
            Goal = request.Goal,
            ExamName = request.Goal == LearningGoal.ExamPrep ? request.ExamName?.Trim() : null,
            StartDate = start,
            EndDate = end,
            DailyMinutes = request.DailyMinutes,
            StudyDays = request.StudyDays,
            PreferredStudyTime = request.PreferredStudyTime,
            FocusSkills = request.FocusSkills == FocusSkill.None ? FocusSkill.All : request.FocusSkills,
            Status = LearnerCourseStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        var order = lectures.Select((l, i) => (l.LectureId, i)).ToDictionary(x => x.LectureId, x => x.i);
        foreach (var p in plan.Lectures)
            learnerCourse.Items.Add(new LearnerRoadmapItem { LectureId = p.LectureId, PlannedDate = p.Date, SortOrder = order[p.LectureId] });

        await ApplyUnlockRuleAsync(learnerCourse.Items, ct);
        db.LearnerCourses.Add(learnerCourse);
        await db.SaveChangesAsync(ct);

        var dto = await BuildDtoAsync(userId, learnerCourse.Id, ct);
        return new EnrolResponse(dto!, ToFeasibility(plan, start, end));
    }

    // ================================================================== reading

    public async Task<IReadOnlyList<LearnerCourseDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.UserId == userId && c.Status != LearnerCourseStatus.Cancelled)
            .OrderBy(c => c.Status == LearnerCourseStatus.Completed ? 1 : 0)
            .ThenByDescending(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var list = new List<LearnerCourseDto>();
        foreach (var id in ids)
        {
            await AutoResumeAsync(userId, id, ct);
            var dto = await BuildDtoAsync(userId, id, ct);
            if (dto is not null) list.Add(dto);
        }
        return list;
    }

    public async Task<Result<LearnerCourseDto>> GetAsync(Guid userId, int id, CancellationToken ct = default)
    {
        await AutoResumeAsync(userId, id, ct);
        var dto = await BuildDtoAsync(userId, id, ct);
        if (dto is null) return Error.NotFound("Course") with { Code = NoCourseCode };
        return dto;
    }

    public async Task<Result<TodayDto>> TodayAsync(Guid userId, int id, CancellationToken ct = default)
    {
        await AutoResumeAsync(userId, id, ct);
        var course = await BuildDtoAsync(userId, id, ct);
        if (course is null) return Error.NotFound("Course") with { Code = NoCourseCode };

        var today = await TodayForAsync(userId, ct);
        var lectures = await LoadRoadmapLecturesAsync(id, today, ct);
        var open = lectures.Where(l => l.State != RoadmapItemState.Completed).ToList();

        // Overdue + today's lectures; if nothing is due, the next one on the plan.
        var due = open.Where(l => l.PlannedDate <= today).Take(5).ToList();
        if (due.Count == 0) due = open.Take(1).ToList();

        var others = (await ListAsync(userId, ct))
            .Where(c => c.Status is LearnerCourseStatus.Active or LearnerCourseStatus.Paused)
            .Select(c => new CourseSwitchItem(c.Id, c.LanguageName, c.LanguageIsoCode, c.CourseTitle, c.ProgressPercent))
            .ToList();

        // Phase 7: streak (BR-09) and the shared points total (FR-S01).
        var streak = await db.UserStreaks.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var points = await db.UserPointsSummaries.Where(s => s.UserId == userId).Select(s => (int?)s.TotalPoints).FirstOrDefaultAsync(ct) ?? 0;
        // Phase 10: the next monthly exam the learner hasn't passed (also drives the "exam in 3 days" reminder).
        var lcCourseId = await db.LearnerCourses.Where(c => c.Id == id).Select(c => c.CourseId).FirstAsync(ct);
        var nextExam = await Notifications.ExamSchedule.NextAsync(db, id, lcCourseId, ct);
        return new TodayDto(course, due, open.Count(l => l.IsOverdue), Progress.StreakRules.View(streak, today).Current, points,
            NextExamDate: nextExam?.Date, others);
    }

    public async Task<Result<RoadmapDto>> RoadmapAsync(Guid userId, int id, CancellationToken ct = default)
    {
        await AutoResumeAsync(userId, id, ct);
        var course = await BuildDtoAsync(userId, id, ct);
        if (course is null) return Error.NotFound("Course") with { Code = NoCourseCode };

        var today = await TodayForAsync(userId, ct);
        var lectures = await LoadRoadmapLecturesAsync(id, today, ct);
        var moduleIds = lectures.Select(l => l.ModuleId).Distinct().ToList();
        var modules = await db.CourseModules.AsNoTracking()
            .Where(m => moduleIds.Contains(m.Id))
            .OrderBy(m => m.SortOrder)
            .Select(m => new { m.Id, m.Title, m.Objective, m.Level, m.MonthNo })
            .ToListAsync(ct);

        var result = modules.Select(m =>
        {
            var items = lectures.Where(l => l.ModuleId == m.Id).ToList();
            return new RoadmapModuleDto(m.Id, m.Title, m.Objective, m.Level, m.MonthNo, ModuleState(items),
                items.Count(i => i.State == RoadmapItemState.Completed), items);
        }).ToList();

        return new RoadmapDto(course, result);
    }

    // ================================================================== changing the plan

    public async Task<Result<EnrolResponse>> UpdatePlanAsync(Guid userId, int id, UpdateLearningPlanRequest request, CancellationToken ct = default)
    {
        var lc = await db.LearnerCourses.Include(c => c.Items).Include(c => c.Course)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (lc is null) return Error.NotFound("Course") with { Code = NoCourseCode };
        if (lc.Status == LearnerCourseStatus.Paused)
            return Error.Validation("Status", "Resume the course before changing its plan.");
        if (lc.Status != LearnerCourseStatus.Active)
            return Error.Validation("Status", "Only an active course can be changed.");
        if (request.TargetLevel < lc.CurrentLevel)
            return Error.Validation(nameof(request.TargetLevel), "Your goal level can't be below your current level.");
        if (request.TargetLevel > lc.Course.EndLevel)
            return Error.Validation(nameof(request.TargetLevel), $"This course goes up to {lc.Course.EndLevel}.");

        var today = await TodayForAsync(userId, ct);
        var start = lc.StartDate > today ? lc.StartDate : today;
        var end = request.DurationMonths is { } months ? RoadmapPlanner.EndDateFor(lc.StartDate, months) : request.EndDate!.Value;
        if (end < start) return Error.Validation(nameof(request.EndDate), "The end date must be today or later.");

        lc.TargetLevel = request.TargetLevel;
        lc.Goal = request.Goal;
        lc.ExamName = request.Goal == LearningGoal.ExamPrep ? request.ExamName?.Trim() : null;
        lc.EndDate = end;
        lc.DailyMinutes = request.DailyMinutes;
        lc.StudyDays = request.StudyDays;
        lc.PreferredStudyTime = request.PreferredStudyTime;
        lc.FocusSkills = request.FocusSkills == FocusSkill.None ? FocusSkill.All : request.FocusSkills;
        lc.UpdatedAt = clock.GetUtcNow();

        var plan = await ReplanAsync(lc, start, ct);
        await db.SaveChangesAsync(ct);

        return new EnrolResponse((await BuildDtoAsync(userId, id, ct))!, ToFeasibility(plan, start, end));
    }

    public async Task<Result<LearnerCourseDto>> PauseAsync(Guid userId, int id, PauseCourseRequest request, CancellationToken ct = default)
    {
        var lc = await db.LearnerCourses.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (lc is null) return Error.NotFound("Course") with { Code = NoCourseCode };
        if (lc.Status != LearnerCourseStatus.Active) return Error.Validation("Status", "Only an active course can be paused.");

        var max = await settings.GetIntAsync(SettingKeys.MaxPauseDays, 30, ct);
        var left = Math.Max(0, max - lc.TotalPausedDays);
        if (left == 0) return Error.Validation(nameof(request.Days), $"You've used all {max} pause days for this course.");
        if (request.Days < 1 || request.Days > left)
            return Error.Validation(nameof(request.Days), $"You can pause for 1 to {left} day(s).");

        var today = await TodayForAsync(userId, ct);
        lc.Status = LearnerCourseStatus.Paused;
        lc.PausedFrom = today;
        lc.PausedUntil = today.AddDays(request.Days - 1);
        lc.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (await BuildDtoAsync(userId, id, ct))!;
    }

    public async Task<Result<LearnerCourseDto>> ResumeAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var lc = await db.LearnerCourses.Include(c => c.Items).Include(c => c.Course)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (lc is null) return Error.NotFound("Course") with { Code = NoCourseCode };
        if (lc.Status != LearnerCourseStatus.Paused) return Error.Validation("Status", "This course is not paused.");

        var today = await TodayForAsync(userId, ct);
        var pausedDays = Math.Clamp(today.DayNumber - lc.PausedFrom!.Value.DayNumber, 0,
            lc.PausedUntil!.Value.DayNumber - lc.PausedFrom.Value.DayNumber + 1);
        EndPause(lc, pausedDays);
        await ReplanAsync(lc, today, ct);
        await db.SaveChangesAsync(ct);
        return (await BuildDtoAsync(userId, id, ct))!;
    }

    public async Task<int> SyncCourseAsync(int courseId, CancellationToken ct = default)
    {
        var learners = await db.LearnerCourses.Include(c => c.Items).Include(c => c.Course)
            .Where(c => c.CourseId == courseId && c.Status == LearnerCourseStatus.Active)
            .ToListAsync(ct);
        foreach (var lc in learners)
        {
            var today = await TodayForAsync(lc.UserId, ct);
            await ReplanAsync(lc, lc.StartDate > today ? lc.StartDate : today, ct);
        }
        await db.SaveChangesAsync(ct);
        return learners.Count;
    }

    // ================================================================== helpers

    /// <summary>
    /// Plans the lectures not yet completed from <paramref name="start"/> to the course end date with the learner's
    /// current settings: keeps completed items, updates dates of existing ones, adds new lectures, removes unpublished ones.
    /// </summary>
    private async Task<PlanResult> ReplanAsync(LearnerCourse lc, DateOnly start, CancellationToken ct)
    {
        var end = lc.EndDate < start ? start : lc.EndDate;
        var all = await LoadPlannerLecturesAsync(lc.CourseId, lc.CurrentLevel, lc.TargetLevel, ct);
        var completed = lc.Items.Where(i => i.State == RoadmapItemState.Completed).Select(i => i.LectureId).ToHashSet();
        var remaining = all.Where(l => !completed.Contains(l.LectureId)).ToList();
        var plan = RoadmapPlanner.Plan(remaining, start, end, lc.DailyMinutes, lc.StudyDays);

        var order = all.Select((l, i) => (l.LectureId, i)).ToDictionary(x => x.LectureId, x => x.i);
        var planned = plan.Lectures.ToDictionary(p => p.LectureId);
        foreach (var item in lc.Items.Where(i => i.State != RoadmapItemState.Completed).ToList())
        {
            if (planned.Remove(item.LectureId, out var p))
            {
                item.PlannedDate = p.Date;
                item.SortOrder = order[item.LectureId];
            }
            else
            {
                db.LearnerRoadmapItems.Remove(item);
                lc.Items.Remove(item);
            }
        }
        foreach (var p in planned.Values)
            lc.Items.Add(new LearnerRoadmapItem { LectureId = p.LectureId, PlannedDate = p.Date, SortOrder = order[p.LectureId] });
        foreach (var item in lc.Items.Where(i => i.State == RoadmapItemState.Completed && order.ContainsKey(i.LectureId)))
            item.SortOrder = order[item.LectureId];

        await ApplyUnlockRuleAsync(lc.Items, ct);
        return plan;
    }

    private sealed record Setup(Course Course, List<PlannerLecture> Lectures, DateOnly Start, DateOnly End);

    private async Task<Result<Setup>> PrepareAsync(Guid userId, LearningPlanRequest r, CancellationToken ct)
    {
        var course = await db.Courses.Include(c => c.Language)
            .FirstOrDefaultAsync(c => c.Id == r.CourseId && c.Status == ContentStatus.Published, ct);
        if (course is null || course.Language.Status != LanguageStatus.Published || course.Language.IsDeleted)
            return Error.Validation(nameof(r.CourseId), "This course is not available.");
        if (r.TargetLevel > course.EndLevel)
            return Error.Validation(nameof(r.TargetLevel), $"This course goes up to {course.EndLevel}. Pick {course.EndLevel} or lower.");

        var today = await TodayForAsync(userId, ct);
        var start = r.StartDate ?? today;
        if (start < today || start > today.AddDays(30))
            return Error.Validation(nameof(r.StartDate), "Start today or within the next 30 days.");

        var end = r.DurationMonths is { } months ? RoadmapPlanner.EndDateFor(start, months) : r.EndDate!.Value;
        if (end <= start) return Error.Validation(nameof(r.EndDate), "The end date must be after the start date.");
        if (end > start.AddYears(2)) return Error.Validation(nameof(r.EndDate), "Plans can be at most 2 years long.");

        var lectures = await LoadPlannerLecturesAsync(course.Id, r.CurrentLevel, r.TargetLevel, ct);
        if (lectures.Count == 0)
            return Error.Validation(nameof(r.TargetLevel), "This course has no lectures between those levels yet. Try another level.");

        return new Setup(course, lectures, start, end);
    }

    /// <summary>The course roadmap trimmed to the learner's levels (FR-L16), in module + lecture order.</summary>
    private async Task<List<PlannerLecture>> LoadPlannerLecturesAsync(int courseId, CefrLevel from, CefrLevel to, CancellationToken ct) =>
        await db.Lectures.AsNoTracking()
            .Where(l => l.Module.CourseId == courseId && !l.Module.IsDeleted &&
                        l.Status == ContentStatus.Published &&
                        l.Module.Level >= from && l.Module.Level <= to)
            .OrderBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder)
            .Select(l => new PlannerLecture(l.Id, l.ModuleId, l.EstimatedMinutes, l.IsCore))
            .ToListAsync(ct);

    private async Task<List<RoadmapLectureDto>> LoadRoadmapLecturesAsync(int learnerCourseId, DateOnly today, CancellationToken ct)
    {
        var rows = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.LearnerCourseId == learnerCourseId)
            .OrderBy(i => i.SortOrder)
            .Select(i => new
            {
                i.Id, i.LectureId, i.Lecture.ModuleId, i.Lecture.Title, i.Lecture.Topic, i.Lecture.EstimatedMinutes,
                i.Lecture.IsCore, i.State, i.PlannedDate, i.CompletedAt
            })
            .ToListAsync(ct);

        return rows.Select(r => new RoadmapLectureDto(r.Id, r.LectureId, r.ModuleId, r.Title, r.Topic, r.EstimatedMinutes, r.IsCore,
            r.State, r.PlannedDate, r.CompletedAt, r.State != RoadmapItemState.Completed && r.PlannedDate < today)).ToList();
    }

    private async Task ApplyUnlockRuleAsync(IEnumerable<LearnerRoadmapItem> items, CancellationToken ct) =>
        RoadmapRules.ApplyUnlock(items, await settings.GetBoolAsync(SettingKeys.LecturesUnlockSequentially, true, ct));

    private static RoadmapItemState ModuleState(IReadOnlyCollection<RoadmapLectureDto> items) =>
        items.Count > 0 && items.All(i => i.State == RoadmapItemState.Completed) ? RoadmapItemState.Completed
        : items.Any(i => i.State is RoadmapItemState.Completed or RoadmapItemState.InProgress) ? RoadmapItemState.InProgress
        : items.Any(i => i.State == RoadmapItemState.Available) ? RoadmapItemState.Available
        : RoadmapItemState.Locked;

    /// <summary>A pause that has run out ends by itself, shifting the plan by the paused days (BR-11).</summary>
    private async Task AutoResumeAsync(Guid userId, int id, CancellationToken ct)
    {
        var lc = await db.LearnerCourses.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && c.Status == LearnerCourseStatus.Paused, ct);
        if (lc?.PausedUntil is null || lc.PausedFrom is null) return;

        var today = await TodayForAsync(userId, ct);
        if (today <= lc.PausedUntil.Value) return;

        EndPause(lc, lc.PausedUntil.Value.DayNumber - lc.PausedFrom.Value.DayNumber + 1);
        await db.SaveChangesAsync(ct);
    }

    private void EndPause(LearnerCourse lc, int pausedDays)
    {
        if (pausedDays > 0)
        {
            lc.EndDate = lc.EndDate.AddDays(pausedDays);
            foreach (var item in lc.Items.Where(i => i.State != RoadmapItemState.Completed))
                item.PlannedDate = item.PlannedDate.AddDays(pausedDays);
        }
        lc.TotalPausedDays += pausedDays;
        lc.Status = LearnerCourseStatus.Active;
        lc.PausedFrom = null;
        lc.PausedUntil = null;
        lc.UpdatedAt = clock.GetUtcNow();
    }

    private async Task<LearnerCourseDto?> BuildDtoAsync(Guid userId, int id, CancellationToken ct)
    {
        var row = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.Id == id && c.UserId == userId)
            .Select(c => new
            {
                Lc = c,
                c.Course.Title,
                LanguageName = c.Course.Language.Name,
                c.Course.Language.NativeName,
                c.Course.Language.IsoCode,
                c.Course.Language.Direction,
                Total = c.Items.Count(i => !i.Lecture.IsDeleted),
                Done = c.Items.Count(i => !i.Lecture.IsDeleted && i.State == RoadmapItemState.Completed)
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var lc = row.Lc;
        var today = await TodayForAsync(userId, ct);
        var maxPause = await settings.GetIntAsync(SettingKeys.MaxPauseDays, 30, ct);
        var pausedNow = lc.Status == LearnerCourseStatus.Paused && lc.PausedFrom is { } f && lc.PausedUntil is { } u
            ? u.DayNumber - f.DayNumber + 1 : 0;

        return new LearnerCourseDto(lc.Id, lc.LanguageId, row.LanguageName, row.NativeName, row.IsoCode, row.Direction,
            lc.CourseId, row.Title, lc.CurrentLevel, lc.TargetLevel, lc.Goal, lc.ExamName, lc.StartDate, lc.EndDate,
            lc.DailyMinutes, lc.StudyDays, lc.PreferredStudyTime, lc.FocusSkills, lc.Status, lc.PausedUntil,
            RemainingPauseDays: Math.Max(0, maxPause - lc.TotalPausedDays - pausedNow),
            CompletedLectures: row.Done,
            TotalLectures: row.Total,
            ProgressPercent: row.Total == 0 ? 0 : (int)Math.Round(100.0 * row.Done / row.Total),
            DaysLeft: Math.Max(0, lc.EndDate.DayNumber - today.DayNumber));
    }

    private async Task<DateOnly> TodayForAsync(Guid userId, CancellationToken ct)
    {
        var tz = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        return LearnerClock.Today(clock, tz);
    }

    private static FeasibilityResponse ToFeasibility(PlanResult plan, DateOnly start, DateOnly end) => new(
        plan.Status, RoadmapPlanner.Describe(plan, end), start, end, plan.EstimatedFinish,
        plan.TotalLectures, plan.Lectures.Count, plan.SkippedOptional, plan.PlannedMinutes, plan.AvailableMinutes,
        plan.StudyDaysAvailable, plan.LecturesPerWeek, plan.SuggestedEndDate, plan.SuggestedDailyMinutes);
}

/// <summary>"Today" in the learner's own time zone (daily quizzes, streaks and plans follow it).</summary>
public static class LearnerClock
{
    public static DateOnly Today(TimeProvider clock, string? timeZoneId)
    {
        var utc = clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz))
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);
        return DateOnly.FromDateTime(utc.UtcDateTime);
    }
}

/// <summary>FR-L31: lectures unlock in order (or all at once when the setting is off).</summary>
internal static class RoadmapRules
{
    public static void ApplyUnlock(IEnumerable<LearnerRoadmapItem> items, bool sequential)
    {
        var firstOpen = true;
        foreach (var item in items.OrderBy(i => i.SortOrder))
        {
            if (item.State is RoadmapItemState.Completed) continue;
            if (item.State is RoadmapItemState.InProgress)
            {
                firstOpen = false;
                continue;
            }
            item.State = !sequential || firstOpen ? RoadmapItemState.Available : RoadmapItemState.Locked;
            firstOpen = false;
        }
    }
}
