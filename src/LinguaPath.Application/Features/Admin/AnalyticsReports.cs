using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Admin;

// ================================================================== FR-A100 analytics reports

public enum ReportKind
{
    LearnerProgress = 1,
    CourseCompletion = 2,
    AssessmentPerformance = 3,
    LectureDropOff = 4,
    AiAndTranslationUsage = 5,
    Certificates = 6
}

public enum ReportColumnKind { Text, Number, Percent, Date, Money }

public sealed record ReportColumn(string Header, ReportColumnKind Kind = ReportColumnKind.Text);

/// <summary>
/// One report as a plain table, so the page, the Excel file and the PDF all come from the same data.
/// Values are string, int, decimal, double, DateOnly or null.
/// </summary>
public sealed record ReportTable(
    ReportKind Kind,
    string Title,
    string Subtitle,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<KeyValuePair<string, string>> Totals,
    bool Truncated,
    string? Notice = null);

public sealed record ReportFilter(ReportKind Kind, DateOnly From, DateOnly To, int? LanguageId, int? CourseId)
{
    public DateTimeOffset FromUtc => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public DateTimeOffset ToUtcExclusive => new(To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

public sealed record ReportOption(int Id, string Name, int? LanguageId = null);

public interface IAnalyticsReportService
{
    Task<ReportTable> RunAsync(ReportFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ReportOption>> LanguagesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ReportOption>> CoursesAsync(CancellationToken ct = default);
}

/// <summary>Excel (ClosedXML) and PDF (QuestPDF) files of a report table.</summary>
public interface IReportExporter
{
    byte[] ToExcel(ReportTable table);
    byte[] ToPdf(ReportTable table);
}

/// <summary>Pure helpers behind the reports, unit-tested on their own.</summary>
public static class ReportMath
{
    public static double Percent(int part, int whole) => whole <= 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    public static double Average(IEnumerable<int> values)
    {
        var list = values as IList<int> ?? values.ToList();
        return list.Count == 0 ? 0 : Math.Round(list.Average(), 1);
    }

    /// <summary>
    /// Drop-off per lecture in course order: the share of learners who completed the previous lecture but not this one.
    /// The first lecture is measured against everyone who reached it.
    /// </summary>
    public static IReadOnlyList<double> DropOff(IReadOnlyList<(int Reached, int Completed)> lectures)
    {
        var result = new List<double>(lectures.Count);
        for (var i = 0; i < lectures.Count; i++)
        {
            var baseline = i == 0 ? lectures[i].Reached : lectures[i - 1].Completed;
            var lost = Math.Max(0, baseline - lectures[i].Completed);
            result.Add(Percent(lost, baseline));
        }
        return result;
    }
}

internal sealed class AnalyticsReportService(IApplicationDbContext db) : IAnalyticsReportService
{
    /// <summary>Screens and files stay quick; narrow the filters for more.</summary>
    public const int MaxRows = 5000;

    public async Task<IReadOnlyList<ReportOption>> LanguagesAsync(CancellationToken ct = default) =>
        await db.Languages.AsNoTracking().Where(l => !l.IsDeleted).OrderBy(l => l.Name)
            .Select(l => new ReportOption(l.Id, l.Name, null)).ToListAsync(ct);

    public async Task<IReadOnlyList<ReportOption>> CoursesAsync(CancellationToken ct = default) =>
        await db.Courses.AsNoTracking().Where(c => !c.IsDeleted).OrderBy(c => c.Language.Name).ThenBy(c => c.Title)
            .Select(c => new ReportOption(c.Id, c.Language.Name + " · " + c.Title, c.LanguageId)).ToListAsync(ct);

    public Task<ReportTable> RunAsync(ReportFilter f, CancellationToken ct = default) => f.Kind switch
    {
        ReportKind.LearnerProgress => LearnerProgressAsync(f, ct),
        ReportKind.CourseCompletion => CourseCompletionAsync(f, ct),
        ReportKind.AssessmentPerformance => AssessmentAsync(f, ct),
        ReportKind.LectureDropOff => DropOffAsync(f, ct),
        ReportKind.AiAndTranslationUsage => UsageAsync(f, ct),
        ReportKind.Certificates => CertificatesAsync(f, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(f), f.Kind, "Unknown report")
    };

    private static string Range(ReportFilter f) => $"{f.From:d MMM yyyy} – {f.To:d MMM yyyy}";

    private IQueryable<Domain.Entities.LearnerCourse> LearnerCourses(ReportFilter f)
    {
        var q = db.LearnerCourses.AsNoTracking().Where(c => !c.User.IsDeleted);
        if (f.LanguageId is { } lang) q = q.Where(c => c.LanguageId == lang);
        if (f.CourseId is { } course) q = q.Where(c => c.CourseId == course);
        // Courses that were running at some point in the range.
        var to = f.To;
        var from = f.FromUtc;
        return q.Where(c => c.StartDate <= to && (c.CompletedAt == null || c.CompletedAt >= from));
    }

    // ------------------------------------------------------------------ learner progress

    private async Task<ReportTable> LearnerProgressAsync(ReportFilter f, CancellationToken ct)
    {
        var rows = await LearnerCourses(f).OrderBy(c => c.User.FullName).Take(MaxRows + 1)
            .Select(c => new
            {
                c.Id, c.UserId, c.User.FullName, c.User.Email, Language = c.Course.Language.Name, Course = c.Course.Title,
                c.Status, c.CurrentLevel, c.TargetLevel, c.StartDate, c.EndDate,
                Total = c.Items.Count(), Done = c.Items.Count(i => i.State == RoadmapItemState.Completed)
            })
            .ToListAsync(ct);
        var truncated = rows.Count > MaxRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var ids = rows.Select(r => r.Id).ToList();
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        // Last activity is worked out here: SQLite (tests) can't take MAX of a DateTimeOffset.
        var last = new Dictionary<int, DateTimeOffset>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var done = await db.LearnerRoadmapItems.AsNoTracking()
                .Where(i => chunk.Contains(i.LearnerCourseId) && i.CompletedAt != null)
                .Select(i => new { i.LearnerCourseId, i.CompletedAt }).ToListAsync(ct);
            foreach (var g in done.GroupBy(d => d.LearnerCourseId)) last[g.Key] = g.Max(d => d.CompletedAt!.Value);
        }
        var points = new Dictionary<Guid, int>();
        foreach (var chunk in userIds.Chunk(1000))
            foreach (var p in await db.UserPointsSummaries.AsNoTracking().Where(p => chunk.Contains(p.UserId))
                         .Select(p => new { p.UserId, p.TotalPoints }).ToListAsync(ct))
                points[p.UserId] = p.TotalPoints;

        var table = rows.Select(r => new object?[]
        {
            r.FullName, r.Email, r.Language, r.Course, r.Status.ToString(), $"{r.CurrentLevel} → {r.TargetLevel}", r.StartDate,
            r.Done, r.Total, ReportMath.Percent(r.Done, r.Total),
            last.TryGetValue(r.Id, out var at) ? DateOnly.FromDateTime(at.UtcDateTime) : null,
            points.GetValueOrDefault(r.UserId)
        }).ToList();

        return new ReportTable(ReportKind.LearnerProgress, "Learner progress", Range(f),
            [new("Learner"), new("Email"), new("Language"), new("Course"), new("Status"), new("Level"), new("Started", ReportColumnKind.Date),
             new("Lectures done", ReportColumnKind.Number), new("Lectures planned", ReportColumnKind.Number), new("Progress", ReportColumnKind.Percent),
             new("Last lecture", ReportColumnKind.Date), new("Points", ReportColumnKind.Number)],
            table,
            [new("Learner courses", rows.Count.ToString()),
             new("Average progress", $"{ReportMath.Average(rows.Select(r => (int)ReportMath.Percent(r.Done, r.Total)))}%")],
            truncated);
    }

    // ------------------------------------------------------------------ completion by course

    private async Task<ReportTable> CourseCompletionAsync(ReportFilter f, CancellationToken ct)
    {
        var rows = await LearnerCourses(f)
            .Select(c => new
            {
                c.CourseId, Language = c.Course.Language.Name, Course = c.Course.Title, c.Status,
                Total = c.Items.Count(), Done = c.Items.Count(i => i.State == RoadmapItemState.Completed)
            })
            .ToListAsync(ct);

        var table = rows.GroupBy(r => new { r.CourseId, r.Language, r.Course })
            .OrderBy(g => g.Key.Language).ThenBy(g => g.Key.Course)
            .Select(g =>
            {
                var n = g.Count();
                var completed = g.Count(x => x.Status == LearnerCourseStatus.Completed);
                return new object?[]
                {
                    g.Key.Language, g.Key.Course, n,
                    g.Count(x => x.Status == LearnerCourseStatus.Active), g.Count(x => x.Status == LearnerCourseStatus.Paused),
                    completed, g.Count(x => x.Status == LearnerCourseStatus.Cancelled),
                    ReportMath.Percent(completed, n), ReportMath.Average(g.Select(x => (int)ReportMath.Percent(x.Done, x.Total)))
                };
            }).ToList();

        var all = rows.Count;
        var allCompleted = rows.Count(r => r.Status == LearnerCourseStatus.Completed);
        return new ReportTable(ReportKind.CourseCompletion, "Completion by course", Range(f),
            [new("Language"), new("Course"), new("Enrolled", ReportColumnKind.Number), new("Active", ReportColumnKind.Number),
             new("Paused", ReportColumnKind.Number), new("Completed", ReportColumnKind.Number), new("Cancelled", ReportColumnKind.Number),
             new("Completion rate", ReportColumnKind.Percent), new("Average progress", ReportColumnKind.Percent)],
            table,
            [new("Enrolments", all.ToString()), new("Completed", allCompleted.ToString()),
             new("Completion rate", $"{ReportMath.Percent(allCompleted, all)}%")],
            false);
    }

    // ------------------------------------------------------------------ quiz and exam performance

    private async Task<ReportTable> AssessmentAsync(ReportFilter f, CancellationToken ct)
    {
        var from = f.FromUtc;
        var to = f.ToUtcExclusive;
        var q = db.QuizAttempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.Graded && !a.IsPractice && a.SubmittedAt >= from && a.SubmittedAt < to);
        if (f.CourseId is { } course) q = q.Where(a => a.CourseId == course);
        if (f.LanguageId is { } lang) q = q.Where(a => a.Course.LanguageId == lang);
        var rows = await q.Select(a => new
            {
                a.CourseId, Course = a.Course.Title, Language = a.Course.Language.Name, a.Type, a.MonthNo, a.UserId, a.Percentage, a.IsPassed
            })
            .ToListAsync(ct);

        var table = rows
            .GroupBy(r => new { r.CourseId, r.Language, r.Course, r.Type, Month = r.Type == QuizType.Monthly ? r.MonthNo : null })
            .OrderBy(g => g.Key.Language).ThenBy(g => g.Key.Course).ThenBy(g => g.Key.Type).ThenBy(g => g.Key.Month)
            .Select(g => new object?[]
            {
                g.Key.Language, g.Key.Course,
                g.Key.Type switch { QuizType.Daily => "Daily quizzes", QuizType.Monthly => $"Month {g.Key.Month ?? 1} exam", _ => "Placement test" },
                g.Count(), g.Select(x => x.UserId).Distinct().Count(),
                ReportMath.Average(g.Select(x => x.Percentage)), ReportMath.Percent(g.Count(x => x.IsPassed), g.Count()),
                g.Min(x => x.Percentage), g.Max(x => x.Percentage)
            }).ToList();

        return new ReportTable(ReportKind.AssessmentPerformance, "Quiz and exam performance", Range(f) + " · graded attempts, practice excluded",
            [new("Language"), new("Course"), new("Assessment"), new("Attempts", ReportColumnKind.Number), new("Learners", ReportColumnKind.Number),
             new("Average score", ReportColumnKind.Percent), new("Pass rate", ReportColumnKind.Percent),
             new("Lowest", ReportColumnKind.Percent), new("Highest", ReportColumnKind.Percent)],
            table,
            [new("Attempts", rows.Count.ToString()), new("Average score", $"{ReportMath.Average(rows.Select(r => r.Percentage))}%"),
             new("Pass rate", $"{ReportMath.Percent(rows.Count(r => r.IsPassed), rows.Count)}%")],
            false);
    }

    // ------------------------------------------------------------------ drop-off per lecture

    private async Task<ReportTable> DropOffAsync(ReportFilter f, CancellationToken ct)
    {
        string[] headers = ["#", "Module", "Lecture", "Reached", "Started", "Completed", "Drop-off"];
        var columns = new List<ReportColumn>
        {
            new(headers[0], ReportColumnKind.Number), new(headers[1]), new(headers[2]), new(headers[3], ReportColumnKind.Number),
            new(headers[4], ReportColumnKind.Number), new(headers[5], ReportColumnKind.Number), new(headers[6], ReportColumnKind.Percent)
        };
        if (f.CourseId is null)
            return new ReportTable(ReportKind.LectureDropOff, "Drop-off per lecture", Range(f), columns, [], [], false,
                "Choose a course: drop-off is measured along one course's lecture order.");

        var lcIds = LearnerCourses(f).Select(c => c.Id);
        var items = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => lcIds.Contains(i.LearnerCourseId))
            .Select(i => new
            {
                i.LectureId, i.Lecture.Title, Module = i.Lecture.Module.Title, ModuleOrder = i.Lecture.Module.SortOrder, i.SortOrder, i.State,
                Started = i.StartedAt != null
            })
            .ToListAsync(ct);

        var lectures = items.GroupBy(i => new { i.LectureId, i.Title, i.Module })
            .Select(g => new
            {
                g.Key.LectureId, g.Key.Title, g.Key.Module, Order = g.Min(x => x.SortOrder), ModuleOrder = g.Min(x => x.ModuleOrder),
                Reached = g.Count(x => x.State != RoadmapItemState.Locked),
                Started = g.Count(x => x.Started || x.State is RoadmapItemState.InProgress or RoadmapItemState.Completed),
                Completed = g.Count(x => x.State == RoadmapItemState.Completed)
            })
            .OrderBy(l => l.Order).ThenBy(l => l.ModuleOrder)
            .ToList();
        var drop = ReportMath.DropOff(lectures.Select(l => (l.Reached, l.Completed)).ToList());

        var table = lectures.Select((l, i) => new object?[] { i + 1, l.Module, l.Title, l.Reached, l.Started, l.Completed, drop[i] }).ToList();
        var worst = lectures.Select((l, i) => (l.Title, Drop: drop[i])).OrderByDescending(x => x.Drop).FirstOrDefault();
        return new ReportTable(ReportKind.LectureDropOff, "Drop-off per lecture", Range(f), columns, table,
            lectures.Count == 0 ? [] : [new("Lectures", lectures.Count.ToString()), new("Biggest drop", $"{worst.Title} ({worst.Drop}%)")],
            false);
    }

    // ------------------------------------------------------------------ translation and AI usage

    private async Task<ReportTable> UsageAsync(ReportFilter f, CancellationToken ct)
    {
        var from = f.FromUtc;
        var to = f.ToUtcExclusive;
        var ai = await db.AiUsageLogs.AsNoTracking().Where(l => l.CreatedAt >= from && l.CreatedAt < to)
            .Select(l => new { l.CreatedAt, l.Feature, l.Provider, l.Status, l.InputTokens, l.OutputTokens, l.Cost, l.UserId })
            .ToListAsync(ct);
        var tr = await db.TranslationEntries.AsNoTracking().Where(t => t.CreatedAt >= from && t.CreatedAt < to)
            .Select(t => new { t.CreatedAt, t.Cached, t.IsSample, t.UserId })
            .ToListAsync(ct);

        var rows = new List<object?[]>();
        foreach (var g in ai.GroupBy(a => new { Day = DateOnly.FromDateTime(a.CreatedAt.UtcDateTime), a.Feature, a.Provider })
                     .OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Feature))
            rows.Add([g.Key.Day, $"AI · {g.Key.Feature}", g.Key.Provider, g.Count(), g.Count(x => x.Status != AiCallStatus.Success),
                g.Select(x => x.UserId).Where(u => u != null).Distinct().Count(), g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.Cost)]);
        foreach (var g in tr.GroupBy(t => DateOnly.FromDateTime(t.CreatedAt.UtcDateTime)).OrderBy(g => g.Key))
            rows.Add([g.Key, "Translation", g.Any(x => x.IsSample) && g.All(x => x.IsSample) ? "Offline sample" : "Translator",
                g.Count(), 0, g.Select(x => x.UserId).Distinct().Count(), null, null, null]);
        rows = rows.OrderBy(r => (DateOnly)r[0]!).ToList();

        return new ReportTable(ReportKind.AiAndTranslationUsage, "Translation and AI usage", Range(f) + " · days in UTC",
            [new("Day", ReportColumnKind.Date), new("Feature"), new("Provider"), new("Calls", ReportColumnKind.Number),
             new("Failed", ReportColumnKind.Number), new("Learners", ReportColumnKind.Number), new("Input tokens", ReportColumnKind.Number),
             new("Output tokens", ReportColumnKind.Number), new("Cost (USD)", ReportColumnKind.Money)],
            rows,
            [new("AI calls", ai.Count.ToString()), new("AI cost", ai.Sum(a => a.Cost).ToString("0.00") + " USD"),
             new("Translations", tr.Count.ToString()),
             new("Served from cache", $"{ReportMath.Percent(tr.Count(t => t.Cached), tr.Count)}%")],
            false);
    }

    // ------------------------------------------------------------------ certificates

    private async Task<ReportTable> CertificatesAsync(ReportFilter f, CancellationToken ct)
    {
        var q = db.Certificates.AsNoTracking().Where(c => c.CompletedOn >= f.From && c.CompletedOn <= f.To);
        if (f.LanguageId is { } lang) q = q.Where(c => c.LanguageId == lang);
        if (f.CourseId is { } course) q = q.Where(c => c.LearnerCourse.CourseId == course);
        var rows = await q.OrderBy(c => c.CompletedOn).ThenBy(c => c.CertificateNo).Take(MaxRows + 1)
            .Select(c => new
            {
                c.CertificateNo, c.LearnerName, Language = c.Language.Name, c.CourseTitle, c.LevelAchieved, c.FinalGrade, c.GradeBand,
                c.StartedOn, c.CompletedOn, c.IsRevoked
            })
            .ToListAsync(ct);
        var truncated = rows.Count > MaxRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        return new ReportTable(ReportKind.Certificates, "Certificates", Range(f) + " · by completion date",
            [new("Number"), new("Learner"), new("Language"), new("Course"), new("Level"), new("Grade", ReportColumnKind.Percent),
             new("Band"), new("Started", ReportColumnKind.Date), new("Completed", ReportColumnKind.Date), new("Status")],
            rows.Select(c => new object?[]
            {
                c.CertificateNo, c.LearnerName, c.Language, c.CourseTitle, c.LevelAchieved.ToString(), c.FinalGrade, c.GradeBand.ToString(),
                c.StartedOn, c.CompletedOn, c.IsRevoked ? "Revoked" : "Valid"
            }).ToList(),
            [new("Issued", rows.Count(r => !r.IsRevoked).ToString()), new("Revoked", rows.Count(r => r.IsRevoked).ToString()),
             new("Average grade", $"{ReportMath.Average(rows.Where(r => !r.IsRevoked).Select(r => r.FinalGrade))}%")],
            truncated);
    }
}
