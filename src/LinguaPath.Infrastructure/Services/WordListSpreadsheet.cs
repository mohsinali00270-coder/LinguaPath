using ClosedXML.Excel;
using LinguaPath.Application.Features.Moderation;

namespace LinguaPath.Infrastructure.Services;

/// <summary>Abusive-word list import/export (FR-M24): Term | Severity (Mild, Abusive, Severe) | Language (ISO code or name, empty = all).</summary>
internal sealed class WordListSpreadsheet : IWordListSpreadsheet
{
    public byte[] Export(IEnumerable<WordSheetRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Words");
        ws.Cell(1, 1).Value = "Term";
        ws.Cell(1, 2).Value = "Severity";
        ws.Cell(1, 3).Value = "Language";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        var r = 2;
        foreach (var row in rows)
        {
            ws.Cell(r, 1).Value = row.Term;
            ws.Cell(r, 2).Value = row.Severity;
            ws.Cell(r, 3).Value = row.Language ?? "";
            r++;
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public IReadOnlyList<WordSheetRow> Read(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheet(1);
        var result = new List<WordSheetRow>();
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > 1))
        {
            var term = row.Cell(1).GetString().Trim();
            if (term.Length == 0) continue;
            var severity = row.Cell(2).GetString().Trim();
            var language = row.Cell(3).GetString().Trim();
            result.Add(new WordSheetRow(row.RowNumber(), term, severity.Length == 0 ? "Abusive" : severity, language.Length == 0 ? null : language));
        }
        return result;
    }
}
