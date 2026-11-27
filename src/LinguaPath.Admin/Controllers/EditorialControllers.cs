using LinguaPath.Admin.Infrastructure;
using LinguaPath.Admin.Models;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Lecture content editor, preview, versions and publishing (FR-A40 .. FR-A47).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class LecturesController(
    ILectureContentAdminService lectures,
    IReviewService reviews,
    ICourseAdminService courses) : AdminControllerBase
{
    public async Task<IActionResult> Index(int? courseId, ContentStatus? status, string? search, int page = 1, CancellationToken ct = default)
    {
        ViewBag.Courses = new SelectList((await courses.ListAsync(null, ct)).Select(c => new { c.Id, Name = $"{c.LanguageName} · {c.Title}" }),
            "Id", "Name", courseId);
        return View(new LectureIndexViewModel
        {
            CourseId = courseId,
            Status = status,
            Search = search,
            Results = await lectures.ListAsync(new LectureFilter(courseId, status, search, page), ct)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, [FromServices] Application.Features.Ai.IAiGenerationService ai, CancellationToken ct)
    {
        var model = await lectures.GetEditorAsync(id, ct);
        if (model is null) return NotFound();
        ViewBag.History = await reviews.HistoryAsync(ReviewEntityType.Lecture, id, ct);
        ViewBag.AiActive = await ai.ActiveForLectureAsync(id, ct);
        ViewBag.AiProvenance = await ai.ProvenanceAsync(ReviewEntityType.Lecture, id, ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(LectureContentInput input, string? then, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await lectures.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                if (then == "submit")
                {
                    var submitted = await reviews.SubmitAsync(ReviewEntityType.Lecture, input.Id, ct);
                    if (submitted.IsFailure) Failure(submitted.Error!.Message);
                    else Success("Saved and sent for review.");
                }
                else
                {
                    Success("Lecture saved.");
                }
                return RedirectToAction(nameof(Edit), new { id = input.Id });
            }
            AddErrors(result.Error!, nameof(LectureEditorModel.Input));
        }

        var model = await lectures.GetEditorAsync(input.Id, ct);
        if (model is null) return NotFound();
        ViewBag.History = await reviews.HistoryAsync(ReviewEntityType.Lecture, input.Id, ct);
        return View(model with { Input = input });
    }

    /// <summary>Phone-sized preview using the same HTML template as the app (FR-A47).</summary>
    public async Task<IActionResult> Preview(int id, CancellationToken ct)
    {
        var model = await lectures.GetEditorAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    /// <summary>The lecture body as the app renders it; loaded in the preview iframe.</summary>
    public async Task<IActionResult> Frame(int id, int? versionId, CancellationToken ct)
    {
        if (versionId is { } vid)
        {
            var version = await lectures.GetVersionAsync(vid, ct);
            if (version is null) return NotFound();
            return Content(LectureHtml.Build(version.BodyHtml, version.Direction), "text/html");
        }
        var model = await lectures.GetEditorAsync(id, ct);
        if (model is null) return NotFound();
        return Content(LectureHtml.Build(model.Input.BodyHtml, model.Direction, languageCode: model.LanguageIsoCode), "text/html");
    }

    [HttpPost]
    public async Task<IActionResult> Submit(int id, CancellationToken ct) =>
        Done(await reviews.SubmitAsync(ReviewEntityType.Lecture, id, ct), "Sent for review.", id);

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) =>
        Done(await lectures.PublishAsync(id, ct), "Published. Enrolled learners' roadmaps were updated.", id);

    [HttpPost]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct) =>
        Done(await lectures.UnpublishAsync(id, ct), "Unpublished and moved back to Draft.", id);

    [HttpPost]
    public async Task<IActionResult> Archive(int id, CancellationToken ct) =>
        Done(await lectures.ArchiveAsync(id, ct), "Archived.", id);

    public async Task<IActionResult> Versions(int id, CancellationToken ct)
    {
        var model = await lectures.GetEditorAsync(id, ct);
        if (model is null) return NotFound();
        ViewBag.Lecture = model;
        return View(await lectures.VersionsAsync(id, ct));
    }

    public async Task<IActionResult> Version(int id, CancellationToken ct)
    {
        var model = await lectures.GetVersionAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Restore(int id, int lectureId, CancellationToken ct) =>
        Done(await lectures.RestoreAsync(id, ct), "Version restored as a new version.", lectureId);

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Edit), new { id });
    }
}

/// <summary>Posts (FR-A50 .. FR-A52).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class PostsController(IPostAdminService posts, IReviewService reviews, ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(int? languageId, ContentStatus? status, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        ViewBag.LanguageId = languageId;
        ViewBag.Status = status;
        return View(await posts.ListAsync(languageId, status, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? languageId, CancellationToken ct)
    {
        await FormBagAsync(languageId, null, ct);
        return View("Edit", new PostInput { LanguageId = languageId ?? 0 });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await posts.GetAsync(id, ct);
        if (model is null) return NotFound();
        await FormBagAsync(model.LanguageId, id, ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(PostInput input, string? then, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await posts.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                if (then == "submit")
                {
                    var submitted = await reviews.SubmitAsync(ReviewEntityType.Post, result.Value, ct);
                    if (submitted.IsFailure) Failure(submitted.Error!.Message);
                    else Success("Saved and sent for review.");
                }
                else Success("Post saved.");
                return RedirectToAction(nameof(Edit), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await FormBagAsync(input.LanguageId, input.Id == 0 ? null : input.Id, ct);
        return View("Edit", input);
    }

    [HttpPost]
    public async Task<IActionResult> Submit(int id, CancellationToken ct) => Done(await reviews.SubmitAsync(ReviewEntityType.Post, id, ct), "Sent for review.", id);

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) => Done(await posts.PublishAsync(id, ct), "Published.", id);

    [HttpPost]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct) => Done(await posts.SetStatusAsync(id, ContentStatus.Draft, ct), "Moved back to Draft.", id);

    [HttpPost]
    public async Task<IActionResult> Archive(int id, CancellationToken ct) => Done(await posts.SetStatusAsync(id, ContentStatus.Archived, ct), "Archived.", id);

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await posts.DeleteAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Post deleted.");
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Done(Application.Common.Result result, string message, int id)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<SelectList> LanguagesAsync(int? selected, CancellationToken ct) =>
        new((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);

    private async Task FormBagAsync(int? languageId, int? postId, CancellationToken ct)
    {
        ViewBag.Languages = await LanguagesAsync(languageId, ct);
        ViewBag.Lectures = languageId is > 0
            ? new SelectList((await posts.LectureOptionsAsync(languageId.Value, ct)).Select(l => new { l.Id, l.Title }), "Id", "Title")
            : new SelectList(Array.Empty<object>());
        if (postId is { } id)
        {
            ViewBag.State = await posts.GetStateAsync(id, ct);
            ViewBag.History = await reviews.HistoryAsync(ReviewEntityType.Post, id, ct);
        }
    }
}

/// <summary>Review queue (FR-A70 .. FR-A72).</summary>
[Authorize(Policy = Policies.Reviewers)]
public sealed class ReviewsController(IReviewService reviews, Application.Features.Ai.IAiGenerationService ai) : AdminControllerBase
{
    public async Task<IActionResult> Index(ReviewEntityType? type, CancellationToken ct)
    {
        ViewBag.Type = type;
        var pending = await reviews.PendingAsync(type, ct);
        // FR-A72: AI drafts show the prompt, the model and the native-speaker flag.
        var provenance = new Dictionary<int, Application.Features.Ai.AiProvenance>();
        foreach (var item in pending.Where(p => p.Source == ContentSource.Ai))
            if (await ai.ProvenanceAsync(item.EntityType, item.EntityId, ct) is { } info) provenance[item.Id] = info;
        ViewBag.Provenance = provenance;
        return View(pending);
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id, string? comment, CancellationToken ct)
    {
        var result = await reviews.ApproveAsync(id, comment, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Approved. A content admin can now publish it.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id, string comment, CancellationToken ct)
    {
        var result = await reviews.RejectAsync(id, comment, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Sent back to the author with your comment.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Preview of a lecture under review (reviewers may not have the content-admin role).</summary>
    public async Task<IActionResult> Lecture(int id, [FromServices] ILectureContentAdminService lectures, CancellationToken ct)
    {
        var model = await lectures.GetEditorAsync(id, ct);
        return model is null ? NotFound() : View("~/Views/Lectures/Preview.cshtml", model);
    }

    public async Task<IActionResult> LectureFrame(int id, [FromServices] ILectureContentAdminService lectures, CancellationToken ct)
    {
        var model = await lectures.GetEditorAsync(id, ct);
        return model is null ? NotFound() : Content(LectureHtml.Build(model.Input.BodyHtml, model.Direction, languageCode: model.LanguageIsoCode), "text/html");
    }

    public async Task<IActionResult> Question(int id, [FromServices] IQuestionAdminService questions, CancellationToken ct)
    {
        var model = await questions.GetEditorAsync(id, ct);
        return model is null ? NotFound() : View("QuestionPreview", model);
    }

    public async Task<IActionResult> Practice(int id, [FromServices] Application.Features.Practice.IPracticeAdminService practice, CancellationToken ct)
    {
        var model = await practice.GetAsync(id, ct);
        return model is null ? NotFound() : View("PracticePreview", model);
    }

    public async Task<IActionResult> Post(int id, [FromServices] IPostAdminService posts, CancellationToken ct)
    {
        var model = await posts.GetAsync(id, ct);
        return model is null ? NotFound() : View("PostPreview", model);
    }
}

/// <summary>Error reports from learners (FR-L37).</summary>
[Authorize(Policy = Policies.AdminPanel)]
public sealed class ReportsController(IContentReportAdminService reports) : AdminControllerBase
{
    public async Task<IActionResult> Index(ReportStatus? status = ReportStatus.Open, CancellationToken ct = default)
    {
        ViewBag.Status = status;
        return View(await reports.ListAsync(status, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Close(int id, ReportStatus status, string? note, CancellationToken ct)
    {
        var result = await reports.CloseAsync(id, status, note, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success($"Report {status.ToString().ToLowerInvariant()}.");
        return RedirectToAction(nameof(Index));
    }
}
