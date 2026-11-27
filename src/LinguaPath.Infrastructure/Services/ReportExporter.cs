using System.Globalization;
using ClosedXML.Excel;
using LinguaPath.Application.Features.Admin;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LinguaPath.Infrastructure.Services;

/// <summary>FR-A100: every analytics report exports to Excel and PDF from the same table.</summary>
internal sealed class ReportExporter : IReportExporter
{
    static ReportExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    public byte[] ToExcel(ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName(t.Title));
        ws.Cell(1, 1).Value = t.Title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = t.Subtitle;
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        const int header = 4;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var cell = ws.Cell(header, c + 1);
            cell.Value = t.Columns[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");
        }

        var r = header + 1;
        foreach (var row in t.Rows)
        {
            for (var c = 0; c < t.Columns.Count && c < row.Length; c++)
                Write(ws.Cell(r, c + 1), row[c], t.Columns[c].Kind);
            r++;
        }

        if (t.Rows.Count > 0)
        {
            ws.Range(header, 1, r - 1, t.Columns.Count).SetAutoFilter();
            ws.SheetView.FreezeRows(header);
        }

        r++;
        foreach (var (label, value) in t.Totals)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value;
            r++;
        }
        if (t.Truncated) ws.Cell(r + 1, 1).Value = $"Only the first {AnalyticsReportServiceLimits.MaxRows} rows are included. Narrow the filters for the rest.";
        if (t.Notice is not null) ws.Cell(r + 1, 1).Value = t.Notice;

        try
        {
            ws.Columns().AdjustToContents(header, Math.Min(r, header + 500), 8, 60);
        }
        catch (Exception)
        {
            // Measuring text needs a font; on a bare server keep a readable fixed width instead.
            ws.Columns().Width = 18;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Write(IXLCell cell, object? value, ReportColumnKind kind)
    {
        switch (value)
        {
            case null: return;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                return;
            case int i: cell.Value = i; break;
            case long l: cell.Value = (double)l; break;
            case decimal m: cell.Value = (double)m; break;
            case double x: cell.Value = x; break;
            default: cell.Value = value.ToString(); return;
        }
        cell.Style.NumberFormat.Format = kind switch
        {
            ReportColumnKind.Percent => "0.0\"%\"",
            ReportColumnKind.Money => "0.0000",
            _ => "#,##0"
        };
    }

    public byte[] ToPdf(ReportTable t)
    {
        var landscape = t.Columns.Count > 6;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(s => s.FontSize(8.5f).FontColor("#1F2937"));

            page.Header().Column(col =>
            {
                col.Item().Text(t.Title).FontSize(16).Bold().FontColor("#4338CA");
                col.Item().Text(t.Subtitle).FontColor(Colors.Grey.Darken1);
                if (t.Totals.Count > 0)
                    col.Item().PaddingTop(6).Text(string.Join("   ·   ", t.Totals.Select(kv => $"{kv.Key}: {kv.Value}"))).SemiBold();
                col.Item().PaddingBottom(8);
            });

            page.Content().Column(col =>
            {
                if (t.Notice is not null) col.Item().PaddingBottom(6).Text(t.Notice).Italic();
                if (t.Rows.Count == 0)
                {
                    col.Item().PaddingTop(20).AlignCenter().Text("No data for these filters.").FontColor(Colors.Grey.Medium);
                    return;
                }
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        foreach (var column in t.Columns)
                            if (column.Kind == ReportColumnKind.Text) c.RelativeColumn(2); else c.RelativeColumn(1);
                    });
                    table.Header(h =>
                    {
                        foreach (var column in t.Columns)
                            h.Cell().Background("#EEF2FF").Padding(3).Text(column.Header).SemiBold();
                    });
                    var shade = false;
                    foreach (var row in t.Rows)
                    {
                        for (var c = 0; c < t.Columns.Count; c++)
                        {
                            var kind = t.Columns[c].Kind;
                            var cell = table.Cell().Background(shade ? "#F9FAFB" : "#FFFFFF").Padding(3);
                            var text = Format(c < row.Length ? row[c] : null, kind);
                            if (kind == ReportColumnKind.Text) cell.Text(text); else cell.AlignRight().Text(text);
                        }
                        shade = !shade;
                    }
                });
                if (t.Truncated)
                    col.Item().PaddingTop(6).Text($"Only the first {AnalyticsReportServiceLimits.MaxRows} rows are shown. Narrow the filters for the rest.").Italic();
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"LinguaPath · generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").FontColor(Colors.Grey.Medium);
                row.ConstantItem(80).AlignRight().Text(x =>
                {
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    internal static string Format(object? value, ReportColumnKind kind) => value switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double x when kind == ReportColumnKind.Percent => x.ToString("0.0", CultureInfo.InvariantCulture) + "%",
        int i when kind == ReportColumnKind.Percent => i.ToString(CultureInfo.InvariantCulture) + "%",
        decimal m when kind == ReportColumnKind.Money => m.ToString("0.0000", CultureInfo.InvariantCulture),
        int i => i.ToString("#,##0", CultureInfo.InvariantCulture),
        long l => l.ToString("#,##0", CultureInfo.InvariantCulture),
        double x => x.ToString("0.#", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string SheetName(string title)
    {
        var clean = new string(title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean;
    }
}

/// <summary>Row limit shared with the report service (which is internal to the application layer).</summary>
internal static class AnalyticsReportServiceLimits
{
    public const int MaxRows = 5000;
}
