using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Moderation queue, decisions and report (FR-M20 .. FR-M23, FR-M26).</summary>
[Authorize(Policy = Policies.Moderators)]
public sealed class ModerationController(IModerationAdminService moderation) : AdminControllerBase
{
    public async Task<IActionResult> Index(bool all = false, ModerationDetector? detectedBy = null, string? search = null, int page = 1,
        CancellationToken ct = default)
    {
        ViewBag.All = all;
        ViewBag.DetectedBy = detectedBy;
        ViewBag.Search = search;
        ViewBag.Open = await moderation.OpenCountAsync(ct);
        return View(await moderation.QueueAsync(new ModerationQueueFilter(!all, detectedBy, search, page), ct));
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var model = await moderation.EventAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Act(long id, ModeratorAction decision, string? note, int? days, int? strikeId, CancellationToken ct)
    {
        var result = await moderation.ActAsync(id, decision, note, days, strikeId, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
            return RedirectToAction(nameof(Details), new { id });
        }
        Success(decision switch
        {
            ModeratorAction.Dismiss => "Dismissed.",
            ModeratorAction.AddStrike => "Strike added; the strike ladder applied.",
            ModeratorAction.RemoveStrike => "Strike removed.",
            ModeratorAction.Suspend => $"Chat suspended for {days} day(s).",
            ModeratorAction.Block => "Account permanently blocked.",
            ModeratorAction.RemoveMessage => "Message removed for everyone.",
            _ => "Done."
        });
        return decision == ModeratorAction.RemoveStrike ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Index));
    }

    /// <summary>BR-28: only a Super Admin can lift a permanent block, with a reason (audited).</summary>
    [HttpPost]
    [Authorize(Policy = Policies.SuperAdminOnly)]
    public async Task<IActionResult> Unblock(Guid userId, string? reason, CancellationToken ct)
    {
        var result = await moderation.UnblockAsync(userId, reason ?? "", ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Account unblocked. The learner can sign in again; their strikes were cleared.");
        return RedirectToAction("Details", "Learners", new { id = userId });
    }

    public async Task<IActionResult> Report(int days = 30, CancellationToken ct = default) => View(await moderation.ReportAsync(days, ct));
}

/// <summary>Abusive-word list per language with Excel import/export and a test box (FR-M24).</summary>
[Authorize(Policy = Policies.Moderators)]
public sealed class ModerationWordsController(IModerationAdminService moderation, ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(string? search, ModerationSeverity? severity, int? languageId, int page = 1, string? test = null,
        int? testLanguageId = null, CancellationToken ct = default)
    {
        ViewBag.Search = search;
        ViewBag.Severity = severity;
        ViewBag.LanguageId = languageId;
        ViewBag.Test = test;
        var options = (await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }).ToList();
        ViewBag.Languages = new SelectList(options, "Id", "Name", languageId);
        ViewBag.LanguageList = options.Select(o => (o.Id, o.Name)).ToList();
        ViewBag.TestLanguages = new SelectList(options, "Id", "Name", testLanguageId);
        if (!string.IsNullOrWhiteSpace(test)) ViewBag.TestResult = await moderation.TestAsync(test, testLanguageId, ct);
        return View(await moderation.WordsAsync(new WordFilter(search, severity, languageId, page), ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save(int? id, string term, ModerationSeverity severity, int? languageId, bool isActive = true, CancellationToken ct = default)
    {
        var result = await moderation.SaveWordAsync(id, term, severity, languageId, isActive, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(id is null ? $"\"{term}\" added." : "Word updated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await moderation.DeleteWordAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Word deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            Failure("Choose an .xlsx file.");
            return RedirectToAction(nameof(Index));
        }
        await using var stream = file.OpenReadStream();
        var result = await moderation.ImportWordsAsync(stream, ct);
        var message = $"{result.Added} added, {result.Updated} updated.";
        if (result.Errors.Count > 0) Failure(message + " " + string.Join(" ", result.Errors.Take(10)) + (result.Errors.Count > 10 ? $" (+{result.Errors.Count - 10} more)" : ""));
        else Success(message);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Export(CancellationToken ct) =>
        File(await moderation.ExportWordsAsync(ct), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "abusive-words.xlsx");
}

/// <summary>Blocked link/contact patterns and allowed exceptions (FR-M25). Built-in detection always runs.</summary>
[Authorize(Policy = Policies.Moderators)]
public sealed class ModerationLinksController(IModerationAdminService moderation) : AdminControllerBase
{
    public async Task<IActionResult> Index(string? test, CancellationToken ct)
    {
        ViewBag.Test = test;
        if (!string.IsNullOrWhiteSpace(test)) ViewBag.TestResult = await moderation.TestAsync(test, null, ct);
        return View(await moderation.PatternsAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save(int? id, string pattern, string? description, bool isException = false, bool isActive = true, CancellationToken ct = default)
    {
        var result = await moderation.SavePatternAsync(id, pattern, description, isException, isActive, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(isException ? "Exception saved." : "Pattern saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await moderation.DeletePatternAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Deleted.");
        return RedirectToAction(nameof(Index));
    }
}
