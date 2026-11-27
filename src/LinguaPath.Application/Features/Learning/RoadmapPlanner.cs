using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Learning;

/// <summary>A lecture as the planner sees it, already in roadmap order.</summary>
public sealed record PlannerLecture(int LectureId, int ModuleId, int Minutes, bool IsCore);

public sealed record PlannedLecture(int LectureId, int ModuleId, DateOnly Date);

public sealed record PlanResult(
    FeasibilityStatus Status,
    IReadOnlyList<PlannedLecture> Lectures,
    int TotalLectures,
    int SkippedOptional,
    int PlannedMinutes,
    int AvailableMinutes,
    int StudyDaysAvailable,
    DateOnly EstimatedFinish,
    DateOnly? SuggestedEndDate,
    int? SuggestedDailyMinutes,
    decimal LecturesPerWeek);

/// <summary>
/// Paces a roadmap over the learner's study days (BR-10, FR-L15, FR-L16, FR-A32).
/// Lectures stay in order; each study day is filled up to the daily minutes (a day always gets at least one lecture).
/// If everything does not fit before the end date, optional lectures are dropped first; if even the core lectures
/// do not fit, the plan runs past the end date and the result suggests a later end date or more minutes per day.
/// </summary>
public static class RoadmapPlanner
{
    public static readonly int[] DailyMinuteOptions = [10, 15, 20, 30, 45, 60, 90, 120];
    private const int MaxHorizonDays = 3 * 366;

    public static PlanResult Plan(IReadOnlyList<PlannerLecture> lectures, DateOnly start, DateOnly end, int dailyMinutes, StudyDays days)
    {
        if (days == StudyDays.None) throw new ArgumentException("Pick at least one study day.", nameof(days));
        if (dailyMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(dailyMinutes));
        if (end < start) throw new ArgumentException("End date is before the start date.", nameof(end));

        var studyDays = CountStudyDays(start, end, days);
        var available = studyDays * dailyMinutes;
        var all = lectures.Sum(l => l.Minutes);
        var core = lectures.Where(l => l.IsCore).Sum(l => l.Minutes);

        List<PlannerLecture> chosen;
        FeasibilityStatus status;
        if (DaysNeeded(lectures, dailyMinutes) <= studyDays)
        {
            chosen = lectures.ToList();
            status = FeasibilityStatus.Ok;
        }
        else
        {
            var coreOnly = lectures.Where(l => l.IsCore).ToList();
            if (DaysNeeded(coreOnly, dailyMinutes) <= studyDays)
            {
                // Add optional lectures back, in order, while the plan still fits.
                chosen = coreOnly;
                foreach (var optional in lectures.Where(l => !l.IsCore))
                {
                    var attempt = lectures.Where(l => l.IsCore || chosen.Contains(l) || l == optional).ToList();
                    if (DaysNeeded(attempt, dailyMinutes) <= studyDays) chosen = attempt;
                }
                status = chosen.Count == lectures.Count ? FeasibilityStatus.Ok : FeasibilityStatus.OptionalDropped;
            }
            else
            {
                chosen = coreOnly;
                status = FeasibilityStatus.NotFeasible;
            }
        }

        var planned = Schedule(chosen, start, dailyMinutes, days);
        var finish = planned.Count > 0 ? planned[^1].Date : start;

        DateOnly? suggestedEnd = null;
        int? suggestedDaily = null;
        if (status == FeasibilityStatus.NotFeasible)
        {
            suggestedEnd = finish;
            suggestedDaily = DailyMinuteOptions.Cast<int?>()
                .FirstOrDefault(m => m > dailyMinutes && DaysNeeded(chosen, m!.Value) <= studyDays);
        }

        var weeks = Math.Max(1m, ((finish.DayNumber - start.DayNumber) + 1) / 7m);
        return new PlanResult(status, planned, lectures.Count, lectures.Count - chosen.Count,
            chosen.Sum(l => l.Minutes), available, studyDays, finish, suggestedEnd, suggestedDaily,
            Math.Round(planned.Count / weeks, 1));
    }

    /// <summary>Study days from start to end, both included.</summary>
    public static int CountStudyDays(DateOnly start, DateOnly end, StudyDays days)
    {
        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
            if (days.Includes(d.DayOfWeek)) count++;
        return count;
    }

    /// <summary>End date for a duration in whole months: the day before the same date N months later.</summary>
    public static DateOnly EndDateFor(DateOnly start, int months) => start.AddMonths(months).AddDays(-1);

    private static int DaysNeeded(IReadOnlyCollection<PlannerLecture> lectures, int dailyMinutes)
    {
        var daysUsed = 0;
        var used = 0;
        foreach (var l in lectures)
        {
            if (daysUsed == 0 || (used > 0 && used + l.Minutes > dailyMinutes))
            {
                daysUsed++;
                used = 0;
            }
            used += l.Minutes;
        }
        return daysUsed;
    }

    private static List<PlannedLecture> Schedule(IReadOnlyList<PlannerLecture> lectures, DateOnly start, int dailyMinutes, StudyDays days)
    {
        var result = new List<PlannedLecture>(lectures.Count);
        var day = NextStudyDay(start, days);
        var used = 0;
        foreach (var l in lectures)
        {
            if (used > 0 && used + l.Minutes > dailyMinutes)
            {
                day = NextStudyDay(day.AddDays(1), days);
                used = 0;
            }
            result.Add(new PlannedLecture(l.LectureId, l.ModuleId, day));
            used += l.Minutes;
        }
        return result;
    }

    private static DateOnly NextStudyDay(DateOnly from, StudyDays days)
    {
        for (var i = 0; i < MaxHorizonDays; i++)
        {
            var d = from.AddDays(i);
            if (days.Includes(d.DayOfWeek)) return d;
        }
        return from;
    }

    /// <summary>A plain sentence for the feasibility screen.</summary>
    public static string Describe(PlanResult plan, DateOnly end) => plan.Status switch
    {
        FeasibilityStatus.Ok =>
            $"Great plan! You'll finish all {plan.Lectures.Count} lectures by {plan.EstimatedFinish:d MMM yyyy}.",
        FeasibilityStatus.OptionalDropped =>
            $"Your plan fits all core lectures by {end:d MMM yyyy}. {plan.SkippedOptional} optional lecture(s) were left out to fit your time.",
        _ => plan.SuggestedDailyMinutes is { } m
            ? $"This goal needs more time than you have. Study {m} minutes a day, or finish on {plan.SuggestedEndDate:d MMM yyyy} instead."
            : $"This goal needs more time than you have. At this pace you'd finish on {plan.SuggestedEndDate:d MMM yyyy}."
    };
}
