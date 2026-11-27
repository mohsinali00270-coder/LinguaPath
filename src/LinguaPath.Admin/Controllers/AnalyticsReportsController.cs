using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Admin.Controllers;

public sealed class AnalyticsReportsViewModel
{
    public required ReportFilter Filter { get; init; }
    public required ReportTable Report { get; init; }
    public required IReadOnlyList<ReportOption> Languages { get; init; }
    public required IReadOnlyList<ReportOption> Courses { get; init; }
}

/// <summary>FR-A100 analytics reports with Excel and PDF export. Super Admin only: the learner report lists names and emails.</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class AnalyticsReportsController(IAnalyticsReportService reports, IReportExporter exporter, IAuditLogger audit, TimeProvider clock)
    : AdminControllerBase
{
    private const int MaxRangeDays = 366;

    public async Task<IActionResult> Index(ReportKind kind = ReportKind.CourseCompletion, DateOnly? from = null, DateOnly? to = null,
        int? languageId = null, int? courseId = null, CancellationToken ct = default)
    {
        var filter = MakeFilter(kind, from, to, languageId, courseId);
        return View(new AnalyticsReportsViewModel
        {
            Filter = filter,
            Report = await reports.RunAsync(filter, ct),
            Languages = await reports.LanguagesAsync(ct),
            Courses = await reports.CoursesAsync(ct)
        });
    }

    /// <summary>Downloads the report as "xlsx" or "pdf". Exports are audited because they can hold personal data.</summary>
    public async Task<IActionResult> Export(string format, ReportKind kind, DateOnly? from = null, DateOnly? to = null,
        int? languageId = null, int? courseId = null, CancellationToken ct = default)
    {
        var filter = MakeFilter(kind, from, to, languageId, courseId);
        var table = await reports.RunAsync(filter, ct);
        var name = $"linguapath-{kind.ToString().ToLowerInvariant()}-{filter.From:yyyyMMdd}-{filter.To:yyyyMMdd}";
        await audit.LogAsync("Export", "AnalyticsReport", kind.ToString(), null,
            new { format, filter.From, filter.To, filter.LanguageId, filter.CourseId, rows = table.Rows.Count }, ct);
        return format?.ToLowerInvariant() switch
        {
            "pdf" => File(exporter.ToPdf(table), "application/pdf", name + ".pdf"),
            _ => File(exporter.ToExcel(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx")
        };
    }

    private ReportFilter MakeFilter(ReportKind kind, DateOnly? from, DateOnly? to, int? languageId, int? courseId)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (start > end) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber > MaxRangeDays) start = end.AddDays(-MaxRangeDays);
        if (!Enum.IsDefined(kind)) kind = ReportKind.CourseCompletion;
        return new ReportFilter(kind, start, end, languageId, courseId);
    }
}
