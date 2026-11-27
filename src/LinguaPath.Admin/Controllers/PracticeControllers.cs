using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Practice library: reading passages, writing tasks, speaking and dictation sentences (FR-L90 .. FR-L93).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class PracticeController(IPracticeAdminService practice, IReviewService reviews, ILanguageAdminService languages,
    Application.Features.Ai.IAiGenerationService ai) : AdminControllerBase
{
    public const int QuestionRows = 5;

    public async Task<IActionResult> Index(int? languageId, PracticeType? type, ContentStatus? status, string? search, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        ViewBag.LanguageId = languageId;
        ViewBag.Type = type;
        ViewBag.Status = status;
        ViewBag.Search = search;
        return View(await practice.ListAsync(new PracticeItemFilter(languageId, type, status, search), ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? languageId, PracticeType type = PracticeType.Read, CancellationToken ct = default)
    {
        await FormBagAsync(languageId, null, ct);
        return View("Edit", Pad(new PracticeItemInput { LanguageId = languageId ?? 0, Type = type }));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await practice.GetAsync(id, ct);
        if (model is null) return NotFound();
        await FormBagAsync(model.LanguageId, id, ct);
        return View(Pad(model));
    }

    [HttpPost]
    public async Task<IActionResult> Save(PracticeItemInput input, string? then, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await practice.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                if (then == "submit")
                {
                    var submitted = await reviews.SubmitAsync(ReviewEntityType.PracticeItem, result.Value, ct);
                    if (submitted.IsFailure) Failure(submitted.Error!.Message);
                    else Success("Saved and sent for review.");
                }
                else Success("Exercise saved.");
                return RedirectToAction(nameof(Edit), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await FormBagAsync(input.LanguageId, input.Id == 0 ? null : input.Id, ct);
        return View("Edit", Pad(input));
    }

    [HttpPost]
    public async Task<IActionResult> Submit(int id, CancellationToken ct) =>
        Done(await reviews.SubmitAsync(ReviewEntityType.PracticeItem, id, ct), "Sent for review.", id);

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) => Done(await practice.PublishAsync(id, ct), "Published.", id);

    [HttpPost]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct) =>
        Done(await practice.SetStatusAsync(id, ContentStatus.Draft, ct), "Moved back to Draft.", id);

    [HttpPost]
    public async Task<IActionResult> Archive(int id, CancellationToken ct) =>
        Done(await practice.SetStatusAsync(id, ContentStatus.Archived, ct), "Archived.", id);

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await practice.DeleteAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Exercise deleted.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ AI drafts

    [HttpGet]
    public async Task<IActionResult> Generate(int? languageId, PracticeType type = PracticeType.Read, CancellationToken ct = default)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        return View(new Application.Features.Ai.PracticeAiInput { LanguageId = languageId ?? 0, Type = type });
    }

    [HttpPost]
    public async Task<IActionResult> Generate(Application.Features.Ai.PracticeAiInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.RequestPracticeAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Queued. Drafts appear in the library when the job finishes; send them for review to publish.");
                return RedirectToAction("Details", "AiRequests", new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        ViewBag.Languages = await LanguagesAsync(input.LanguageId, ct);
        return View(input);
    }

    // ------------------------------------------------------------------ helpers

    private static PracticeItemInput Pad(PracticeItemInput input)
    {
        while (input.Questions.Count < QuestionRows) input.Questions.Add(new PracticeQuestionInput());
        return input;
    }

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<SelectList> LanguagesAsync(int? selected, CancellationToken ct) =>
        new((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);

    private async Task FormBagAsync(int? languageId, int? id, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        if (id is { } itemId)
        {
            ViewBag.State = await practice.GetStateAsync(itemId, ct);
            ViewBag.History = await reviews.HistoryAsync(ReviewEntityType.PracticeItem, itemId, ct);
            ViewBag.AiProvenance = await ai.ProvenanceAsync(ReviewEntityType.PracticeItem, itemId, ct);
        }
    }
}
