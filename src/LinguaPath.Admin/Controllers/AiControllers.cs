using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>AI generation requests: new languages (with Super Admin approval), roadmaps, lectures, questions, posts (SRS 5).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class AiRequestsController(
    IAiGenerationService ai,
    IAiContentService aiContent,
    ILanguageAdminService languages,
    IQuestionAdminService questions) : AdminControllerBase
{
    public async Task<IActionResult> Index(AiRequestStatus? status, AiRequestType? type, CancellationToken ct)
    {
        ViewBag.Status = status;
        ViewBag.Type = type;
        ViewBag.Budget = await aiContent.BudgetAsync(ct);
        return View(await ai.ListAsync(status, type, ct));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var model = await ai.GetAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    // ------------------------------------------------------------------ new language (FR-A21 .. FR-A24)

    [HttpGet]
    public async Task<IActionResult> NewLanguage(int languageId, CancellationToken ct)
    {
        await LanguagesBagAsync(languageId, ct);
        ViewBag.Estimate = await ai.EstimateAsync(40, true, ct);
        return View(new RoadmapAiInput { LanguageId = languageId, GenerateContent = true });
    }

    [HttpPost]
    public async Task<IActionResult> NewLanguage(RoadmapAiInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.RequestNewLanguageAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Request created. A Super Admin has to approve the cost before anything is generated.");
                return RedirectToAction(nameof(Details), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await LanguagesBagAsync(input.LanguageId, ct);
        ViewBag.Estimate = await ai.EstimateAsync(input.TotalHours, true, ct);
        return View(input);
    }

    /// <summary>Live estimate while the form is filled in (FR-A22).</summary>
    public async Task<IActionResult> Estimate(int hours, bool content, CancellationToken ct)
    {
        var e = await ai.EstimateAsync(Math.Clamp(hours, 1, 500), content, ct);
        return Json(new { modules = e.Modules, lectures = e.Lectures, questions = e.Questions, cost = e.Cost.ToString("0.00") });
    }

    [HttpPost]
    [Authorize(Policy = Policies.SuperAdminOnly)]
    public async Task<IActionResult> Decide(int id, bool approve, string? notes, CancellationToken ct)
    {
        var result = await ai.DecideAsync(id, approve, notes, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(approve ? "Approved. The roadmap is being generated." : "Rejected.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ roadmap (FR-A31)

    [HttpGet]
    public async Task<IActionResult> Roadmap(int? languageId, CancellationToken ct)
    {
        await LanguagesBagAsync(languageId, ct);
        return View(new RoadmapAiInput { LanguageId = languageId ?? 0 });
    }

    [HttpPost]
    public async Task<IActionResult> Roadmap(RoadmapAiInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.RequestRoadmapAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Queued. The roadmap appears as a Draft course when it is ready.");
                return RedirectToAction(nameof(Details), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await LanguagesBagAsync(input.LanguageId, ct);
        return View(input);
    }

    /// <summary>FR-A23: roadmap reviewed in the builder; write the lectures and questions now.</summary>
    [HttpPost]
    public async Task<IActionResult> ApproveRoadmap(int id, int? courseId, CancellationToken ct)
    {
        var result = await ai.ApproveRoadmapAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Roadmap approved. AI is now writing the lectures and questions, module by module.");
        return courseId is { } c ? RedirectToAction("Builder", "Courses", new { id = c }) : RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ lectures (FR-A42, FR-A43, FR-A44)

    [HttpPost]
    public async Task<IActionResult> Lecture(int lectureId, string? notes, CancellationToken ct)
    {
        var result = await ai.RequestLectureAsync(lectureId, notes, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("AI is writing this lecture. Refresh in a minute; the result is saved as a new Draft version.");
        return RedirectToAction("Edit", "Lectures", new { id = lectureId });
    }

    [HttpPost]
    public async Task<IActionResult> AutoFill(int courseId, string? notes, CancellationToken ct)
    {
        var result = await ai.RequestAutoFillAsync(courseId, notes, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Queued: AI writes every lecture that has no text yet. Progress is under AI generation.");
        return RedirectToAction("Builder", "Courses", new { id = courseId });
    }

    /// <summary>FR-A43: expand / simplify / add examples to the editor text (returned, not saved).</summary>
    [HttpPost]
    public async Task<IActionResult> Assist(int lectureId, AssistAction action, string html, CancellationToken ct)
    {
        var result = await ai.AssistAsync(lectureId, action, html, ct);
        return result.IsSuccess ? Json(new { html = result.Value }) : Json(new { error = result.Error!.Message });
    }

    // ------------------------------------------------------------------ questions (FR-A62)

    [HttpGet]
    public async Task<IActionResult> Questions(int? lectureId, int? languageId, CancellationToken ct)
    {
        await LectureBagAsync(languageId, lectureId, ct);
        return View(new QuestionsAiInput { LectureId = lectureId ?? 0 });
    }

    [HttpPost]
    public async Task<IActionResult> Questions(QuestionsAiInput input, int? languageId, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.RequestQuestionsAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Queued. The questions appear in the question bank as AI Drafts.");
                return RedirectToAction(nameof(Details), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await LectureBagAsync(languageId, input.LectureId, ct);
        return View(input);
    }

    // ------------------------------------------------------------------ posts (FR-A52)

    [HttpGet]
    public async Task<IActionResult> Posts(int? languageId, CancellationToken ct)
    {
        await LanguagesBagAsync(languageId, ct);
        var next = DateTime.UtcNow.AddMonths(1);
        return View(new PostsAiInput { LanguageId = languageId ?? 0, Month = new DateTime(next.Year, next.Month, 1) });
    }

    [HttpPost]
    public async Task<IActionResult> Posts(PostsAiInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.RequestPostsAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Queued. The posts appear under Posts as AI Drafts, scheduled one a day.");
                return RedirectToAction(nameof(Details), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await LanguagesBagAsync(input.LanguageId, ct);
        return View(input);
    }

    // ------------------------------------------------------------------ job control

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct) =>
        Done(await ai.CancelAsync(id, ct), "Cancelled. Anything already drafted stays in Draft.", id);

    [HttpPost]
    public async Task<IActionResult> Retry(int id, CancellationToken ct) =>
        Done(await ai.RetryAsync(id, ct), "Queued again. Finished parts are skipped.", id);

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LanguagesBagAsync(int? selected, CancellationToken ct) =>
        ViewBag.Languages = new SelectList((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);

    private async Task LectureBagAsync(int? languageId, int? lectureId, CancellationToken ct)
    {
        await LanguagesBagAsync(languageId, ct);
        ViewBag.LanguageId = languageId;
        ViewBag.Lectures = languageId is > 0
            ? new SelectList((await questions.LectureOptionsAsync(languageId.Value, ct)).Select(l => new { l.Id, l.Label }), "Id", "Label", lectureId)
            : null;
    }
}

/// <summary>AI cost and prompt templates (SRS 5.3, 5.4, FR-A110). Super Admin only.</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class AiSettingsController(IAiGenerationService ai) : AdminControllerBase
{
    public async Task<IActionResult> Index(int days = 30, CancellationToken ct = default)
    {
        ViewBag.Days = days;
        ViewBag.Templates = await ai.TemplatesAsync(ct);
        return View(await ai.UsageAsync(days, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Prompt(string key, int? versionId, CancellationToken ct)
    {
        try
        {
            return View(await ai.TemplateEditorAsync(key, versionId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Prompt(PromptTemplateInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await ai.SaveTemplateAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Saved as a new version and made active.");
                return RedirectToAction(nameof(Prompt), new { key = input.Key });
            }
            AddErrors(result.Error!, nameof(PromptEditorModel.Input));
        }
        var model = await ai.TemplateEditorAsync(input.Key, null, ct);
        return View(model with { Input = input });
    }

    [HttpPost]
    public async Task<IActionResult> Activate(int id, string key, CancellationToken ct)
    {
        var result = await ai.ActivateTemplateAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("That version is now active.");
        return RedirectToAction(nameof(Prompt), new { key });
    }
}
