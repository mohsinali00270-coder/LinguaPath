using System.Globalization;
using ClosedXML.Excel;
using LinguaPath.Application.Common.Interfaces;

namespace LinguaPath.Infrastructure.Services;

/// <summary>
/// Excel template for the question bank (FR-A65). One row per question:
/// Id (empty = new) | Lecture (id or title) | Type | Skill | Level | Difficulty | Points | Question | Sentence | Media URL | Explanation | Option 1..8.
/// Options: prefix "*" for a correct choice; "left => right" for matching; accepted answers as plain text for fill-in, translate and reorder.
/// </summary>
internal sealed class QuestionSpreadsheet : IQuestionSpreadsheet
{
    private const int MaxOptions = 8;
    private static readonly string[] Headers =
        ["Id", "Lecture", "Type", "Skill", "Level", "Difficulty", "Points", "Question", "Sentence", "Media URL", "Explanation"];

    public byte[] Export(IEnumerable<QuestionSheetRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Questions");
        for (var c = 0; c < Headers.Length; c++) ws.Cell(1, c + 1).Value = Headers[c];
        for (var i = 1; i <= MaxOptions; i++) ws.Cell(1, Headers.Length + i).Value = $"Option {i}";
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var r = 2;
        foreach (var row in rows)
        {
            ws.Cell(r, 1).Value = row.Id?.ToString(CultureInfo.InvariantCulture) ?? "";
            ws.Cell(r, 2).Value = row.Lecture ?? "";
            ws.Cell(r, 3).Value = row.Type;
            ws.Cell(r, 4).Value = row.Skill;
            ws.Cell(r, 5).Value = row.Level;
            ws.Cell(r, 6).Value = row.Difficulty;
            ws.Cell(r, 7).Value = row.Points;
            ws.Cell(r, 8).Value = row.Stem;
            ws.Cell(r, 9).Value = row.Prompt ?? "";
            ws.Cell(r, 10).Value = row.MediaUrl ?? "";
            ws.Cell(r, 11).Value = row.Explanation ?? "";
            for (var i = 0; i < Math.Min(MaxOptions, row.Options.Count); i++) ws.Cell(r, Headers.Length + 1 + i).Value = row.Options[i];
            r++;
        }
        ws.Columns().AdjustToContents(8, 60);

        var help = wb.Worksheets.Add("How to fill in");
        var lines = new[]
        {
            "Leave Id empty to add a question; keep the Id to update that question. Imported questions start as Draft and need review.",
            "Lecture: the lecture id or its exact title (optional).",
            "Type: MultipleChoice, TrueFalse, FillBlank, MatchPairs, ReorderWords, Translate, ListenChoose, Speak, Writing.",
            "Skill: Grammar, Vocabulary, Reading, Listening, Writing, Speaking.   Level: A1..C2.   Difficulty: 1..5.   Points: 1..20.",
            "Question: the text shown. For FillBlank mark the gap with ___ (three underscores).",
            "Sentence: the sentence to translate (Translate), to hear (ListenChoose) or to say (Speak).",
            "Options: MultipleChoice / TrueFalse / ListenChoose: one choice per cell, put * before each correct one (e.g. *hello).",
            "Options: FillBlank / Translate: every accepted answer.   ReorderWords: the correct sentence (more cells = other accepted orders).",
            "Options: MatchPairs: one pair per cell as  left => right  (e.g. perro => dog).   Speak / Writing: no options."
        };
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Column(1).Width = 140;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public IReadOnlyList<QuestionSheetRow> Read(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheet(1);
        var result = new List<QuestionSheetRow>();
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > 1))
        {
            string Cell(int c) => row.Cell(c).GetString().Trim();
            var number = row.RowNumber();
            var stem = Cell(8);
            if (string.IsNullOrWhiteSpace(stem) && string.IsNullOrWhiteSpace(Cell(3))) continue;

            int? id = null;
            if (Cell(1) is { Length: > 0 } idText)
            {
                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    result.Add(Bad(number, $"Id \"{idText}\" is not a number."));
                    continue;
                }
                id = parsed;
            }
            if (string.IsNullOrWhiteSpace(stem))
            {
                result.Add(Bad(number, "The Question column is empty."));
                continue;
            }

            var options = new List<string>();
            for (var i = 0; i < MaxOptions; i++)
                if (Cell(Headers.Length + 1 + i) is { Length: > 0 } o) options.Add(o);

            result.Add(new QuestionSheetRow(number, id, Empty(Cell(2)), Cell(3), Cell(4), Cell(5),
                Int(Cell(6), 2), Int(Cell(7), 1), stem, Empty(Cell(9)), Empty(Cell(10)), Empty(Cell(11)), options));
        }
        return result;
    }

    private static QuestionSheetRow Bad(int row, string error) => new(row, null, null, "", "", "", 0, 0, "", null, null, null, [], error);
    private static string? Empty(string s) => s.Length == 0 ? null : s;
    private static int Int(string s, int fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : fallback;
}
