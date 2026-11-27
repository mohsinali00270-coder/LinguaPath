using LinguaPath.Admin.Infrastructure;
using LinguaPath.Admin.Models;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Question bank (FR-A60, FR-A61, FR-A65, FR-A66).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class QuestionsController(
    IQuestionAdminService questions,
    IReviewService reviews,
    ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index([FromQuery] QuestionFilterForm filter, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(filter.LanguageId, ct);
        ViewBag.Lectures = filter.LanguageId is > 0
            ? new SelectList((await questions.LectureOptionsAsync(filter.LanguageId.Value, ct)).Select(l => new { l.Id, l.Label }), "Id", "Label", filter.LectureId)
            : new SelectList(Array.Empty<object>());
        return View(new QuestionIndexViewModel { Filter = filter, Results = await questions.ListAsync(filter.ToFilter(), ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? languageId, int? lectureId, QuestionType type = QuestionType.MultipleChoice, CancellationToken ct = default)
    {
        var input = new QuestionInput { LanguageId = languageId ?? 0, LectureId = lectureId, Type = type };
        await FormBagAsync(input.LanguageId, null, ct);
        return View("Edit", new QuestionEditorModel(input, ContentStatus.Draft, ContentSource.Manual, 0, 0, null, null, null));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, [FromServices] Application.Features.Ai.IAiGenerationService ai, CancellationToken ct)
    {
        var model = await questions.GetEditorAsync(id, ct);
        if (model is null) return NotFound();
        await FormBagAsync(model.Input.LanguageId, id, ct);
        ViewBag.AiProvenance = await ai.ProvenanceAsync(ReviewEntityType.Question, id, ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(QuestionInput input, string? then, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await questions.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                if (then == "submit")
                {
                    var submitted = await reviews.SubmitAsync(ReviewEntityType.Question, result.Value, ct);
                    if (submitted.IsFailure) Failure(submitted.Error!.Message);
                    else Success("Saved and sent for review.");
                }
                else if (then == "new")
                {
                    Success("Question saved. Add the next one.");
                    return RedirectToAction(nameof(Create), new { languageId = input.LanguageId, lectureId = input.LectureId, type = input.Type });
                }
                else Success("Question saved.");
                return RedirectToAction(nameof(Edit), new { id = result.Value });
            }
            AddErrors(result.Error!, nameof(QuestionEditorModel.Input));
        }

        var existing = input.Id == 0 ? null : await questions.GetEditorAsync(input.Id, ct);
        await FormBagAsync(input.LanguageId, input.Id == 0 ? null : input.Id, ct);
        return View("Edit", (existing ?? new QuestionEditorModel(input, ContentStatus.Draft, ContentSource.Manual, 0, 0, null, null, null)) with { Input = input });
    }

    [HttpPost]
    public async Task<IActionResult> Submit(int id, CancellationToken ct) =>
        Done(await reviews.SubmitAsync(ReviewEntityType.Question, id, ct), "Sent for review.", id);

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) =>
        Done(await questions.PublishAsync(id, ct), "Published. It can now appear in quizzes and exams.", id);

    [HttpPost]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct) =>
        Done(await questions.SetStatusAsync(id, ContentStatus.Draft, ct), "Moved back to Draft.", id);

    [HttpPost]
    public async Task<IActionResult> Archive(int id, CancellationToken ct) =>
        Done(await questions.SetStatusAsync(id, ContentStatus.Archived, ct), "Archived.", id);

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await questions.DeleteAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Question deleted. Past attempts still show it.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Lectures of a language, for the editor's lecture picker.</summary>
    public async Task<IActionResult> Lectures(int languageId, CancellationToken ct) =>
        Json((await questions.LectureOptionsAsync(languageId, ct)).Select(l => new { id = l.Id, label = l.Label }));

    /// <summary>FR-A65: download the current filter as the Excel template.</summary>
    public async Task<IActionResult> Export([FromQuery] QuestionFilterForm filter, CancellationToken ct) =>
        File(await questions.ExportAsync(filter.ToFilter() with { Page = 1, PageSize = 5000 }, ct),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"questions-{DateTime.UtcNow:yyyyMMdd}.xlsx");

    [HttpPost]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Import(int languageId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            Failure("Choose an .xlsx file made from the template (use Export to get one).");
            return RedirectToAction(nameof(Index), new { languageId });
        }
        await using var stream = file.OpenReadStream();
        var result = await questions.ImportAsync(languageId, stream, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
        }
        else
        {
            var r = result.Value;
            Success($"Import finished: {r.Created} added, {r.Updated} updated" + (r.Errors.Count > 0 ? $", {r.Errors.Count} row(s) skipped." : "."));
            if (r.Errors.Count > 0) TempData["ImportErrors"] = string.Join("\n", r.Errors.Take(50));
        }
        return RedirectToAction(nameof(Index), new { languageId, status = ContentStatus.Draft });
    }

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<SelectList> LanguagesAsync(int? selected, CancellationToken ct) =>
        new((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);

    private async Task FormBagAsync(int languageId, int? questionId, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        ViewBag.Lectures = languageId > 0
            ? new SelectList((await questions.LectureOptionsAsync(languageId, ct)).Select(l => new { l.Id, l.Label }), "Id", "Label")
            : new SelectList(Array.Empty<object>());
        ViewBag.History = questionId is { } id ? await reviews.HistoryAsync(ReviewEntityType.Question, id, ct) : [];
    }
}

/// <summary>Monthly exam builder and the daily quiz rule (FR-A63, FR-A64).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class QuizzesController(IQuizAdminService quizzes, ICourseAdminService courses) : AdminControllerBase
{
    public async Task<IActionResult> Index(int? courseId, CancellationToken ct)
    {
        ViewBag.Courses = await CoursesAsync(courseId, ct);
        ViewBag.CourseId = courseId;
        ViewBag.Rule = await quizzes.GetDailyRuleAsync(ct);
        return View(await quizzes.ListAsync(courseId, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(int courseId, CancellationToken ct)
    {
        var model = await quizzes.NewAsync(courseId, ct);
        if (model is null)
        {
            Failure("Choose a course first.");
            return RedirectToAction(nameof(Index));
        }
        return View("Edit", model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await quizzes.GetEditorAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(ExamInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await quizzes.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Exam saved.");
                return RedirectToAction(nameof(Edit), new { id = result.Value });
            }
            AddErrors(result.Error!, nameof(ExamEditorModel.Input));
        }
        var model = await quizzes.RebuildAsync(input, ct);
        return model is null ? NotFound() : View("Edit", model);
    }

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) =>
        Done(await quizzes.PublishAsync(id, ct), "Published. Learners see it in their exams list.", id);

    [HttpPost]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct) =>
        Done(await quizzes.UnpublishAsync(id, ct), "Unpublished.", id);

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await quizzes.DeleteAsync(id, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
            return RedirectToAction(nameof(Edit), new { id });
        }
        Success("Exam deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Policy = Policies.SuperAdminOnly)]
    public async Task<IActionResult> SaveRule(DailyQuizRuleInput rule, CancellationToken ct)
    {
        var result = ModelState.IsValid ? await quizzes.SaveDailyRuleAsync(rule, ct) : null;
        if (result is null) Failure("Each count must be between 0 and 20.");
        else if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Daily quiz rule saved.");
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<SelectList> CoursesAsync(int? selected, CancellationToken ct) =>
        new((await courses.ListAsync(null, ct)).Select(c => new { c.Id, Name = $"{c.LanguageName} · {c.Title}" }), "Id", "Name", selected);
}

/// <summary>Grading queue, attempt details and mark overrides (FR-A80, FR-A81, BR-14).</summary>
[Authorize(Policy = Policies.AdminPanel)]
public sealed class GradingController(IGradingService grading) : AdminControllerBase
{
    [Authorize(Policy = Policies.Reviewers)]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await grading.QueueAsync(ct));

    [HttpPost]
    [Authorize(Policy = Policies.Reviewers)]
    public async Task<IActionResult> Grade(int answerId, decimal score, string? feedback, string? returnTo, CancellationToken ct)
    {
        var result = await grading.GradeAsync(answerId, score, feedback, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Graded. The learner's mark is updated.");
        return Url.IsLocalUrl(returnTo) ? Redirect(returnTo) : RedirectToAction(nameof(Index));
    }

    /// <summary>Every answer of an attempt (FR-A80).</summary>
    public async Task<IActionResult> Attempt(int id, CancellationToken ct)
    {
        var model = await grading.AttemptDetailAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [Authorize(Policy = Policies.Reviewers)]
    public async Task<IActionResult> Override(int answerId, int attemptId, decimal score, string reason, CancellationToken ct)
    {
        var result = await grading.OverrideAsync(answerId, score, reason, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Mark changed and recorded in the audit log.");
        return RedirectToAction(nameof(Attempt), new { id = attemptId });
    }
}
