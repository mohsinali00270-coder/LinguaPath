using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Languages (FR-A10, FR-A11).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class LanguagesController(ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await languages.ListAsync(ct));

    [HttpGet]
    public IActionResult Create() => View("Edit", new LanguageInput());

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await languages.GetAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(LanguageInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View("Edit", input);
        var result = await languages.SaveAsync(input, ct);
        if (result.IsFailure)
        {
            AddErrors(result.Error!);
            return View("Edit", input);
        }
        Success($"{input.Name} saved.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Languages learners asked for (FR-A20). AI generation for new languages arrives in Phase 5 (FR-A21).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class LanguageRequestsController(ILanguageRequestAdminService requests) : AdminControllerBase
{
    public async Task<IActionResult> Index(LanguageRequestStatus? status, CancellationToken ct)
    {
        ViewBag.Status = status;
        return View(await requests.ListAsync(status, ct));
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(string name, LanguageRequestStatus status, CancellationToken ct)
    {
        var result = await requests.SetStatusAsync(name, status, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success($"Requests for \"{name}\" marked {status}.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Courses and the roadmap builder (FR-A12, FR-A30, FR-A32, FR-A33).</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class CoursesController(ICourseAdminService courses, ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(int? languageId, CancellationToken ct)
    {
        ViewBag.LanguageId = languageId;
        ViewBag.Languages = await LanguageListAsync(ct, languageId);
        return View(await courses.ListAsync(languageId, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? languageId, CancellationToken ct)
    {
        ViewBag.Languages = await LanguageListAsync(ct, languageId);
        return View("Edit", new CourseInput { LanguageId = languageId ?? 0 });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await courses.GetAsync(id, ct);
        if (model is null) return NotFound();
        ViewBag.Languages = await LanguageListAsync(ct, model.LanguageId);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(CourseInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await courses.SaveAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Course saved.");
                return RedirectToAction(nameof(Builder), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        ViewBag.Languages = await LanguageListAsync(ct, input.LanguageId);
        return View("Edit", input);
    }

    /// <summary>Roadmap builder: modules and lectures with drag-and-drop ordering.</summary>
    public async Task<IActionResult> Builder(int id, [FromServices] Application.Features.Ai.IAiGenerationService ai, CancellationToken ct)
    {
        var model = await courses.GetBuilderAsync(id, ct);
        if (model is null) return NotFound();
        ViewBag.AiReview = await ai.RoadmapAwaitingReviewAsync(id, ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, ContentStatus status, CancellationToken ct)
    {
        var result = await courses.SetStatusAsync(id, status, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(status == ContentStatus.Published ? "Course published. Learners can now pick it." : $"Course set to {status}.");
        return RedirectToAction(nameof(Builder), new { id });
    }

    // ------------------------------------------------------------- modules

    [HttpGet]
    public async Task<IActionResult> CreateModule(int courseId, CancellationToken ct)
    {
        var course = await courses.GetAsync(courseId, ct);
        if (course is null) return NotFound();
        await ModuleViewBagAsync(courseId, null, ct);
        return View("ModuleEdit", new ModuleInput { CourseId = courseId, Level = course.StartLevel });
    }

    [HttpGet]
    public async Task<IActionResult> EditModule(int id, CancellationToken ct)
    {
        var model = await courses.GetModuleAsync(id, ct);
        if (model is null) return NotFound();
        await ModuleViewBagAsync(model.CourseId, id, ct);
        return View("ModuleEdit", model);
    }

    [HttpPost]
    public async Task<IActionResult> SaveModule(ModuleInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await courses.SaveModuleAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Module saved.");
                return RedirectToAction(nameof(Builder), new { id = input.CourseId });
            }
            AddErrors(result.Error!);
        }
        await ModuleViewBagAsync(input.CourseId, input.Id == 0 ? null : input.Id, ct);
        return View("ModuleEdit", input);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteModule(int id, CancellationToken ct)
    {
        var result = await courses.DeleteModuleAsync(id, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
            return RedirectToAction(nameof(Index));
        }
        Success("Module deleted.");
        return RedirectToAction(nameof(Builder), new { id = result.Value });
    }

    [HttpPost]
    public async Task<IActionResult> ReorderModules([FromBody] ReorderInput input, CancellationToken ct)
    {
        var result = await courses.ReorderModulesAsync(input, ct);
        return result.IsSuccess ? Ok() : BadRequest(new { message = result.Error!.Message });
    }

    // ------------------------------------------------------------- lectures

    [HttpGet]
    public async Task<IActionResult> CreateLecture(int moduleId, CancellationToken ct)
    {
        var module = await courses.GetModuleAsync(moduleId, ct);
        if (module is null) return NotFound();
        ViewBag.CourseId = module.CourseId;
        ViewBag.ModuleTitle = module.Title;
        return View("LectureEdit", new LectureInput { ModuleId = moduleId, Level = module.Level });
    }

    [HttpGet]
    public async Task<IActionResult> EditLecture(int id, CancellationToken ct)
    {
        var model = await courses.GetLectureAsync(id, ct);
        if (model is null) return NotFound();
        var module = await courses.GetModuleAsync(model.ModuleId, ct);
        ViewBag.CourseId = module?.CourseId;
        ViewBag.ModuleTitle = module?.Title;
        return View("LectureEdit", model);
    }

    [HttpPost]
    public async Task<IActionResult> SaveLecture(LectureInput input, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await courses.SaveLectureAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Lecture saved.");
                return RedirectToAction(nameof(Builder), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        var module = await courses.GetModuleAsync(input.ModuleId, ct);
        ViewBag.CourseId = module?.CourseId;
        ViewBag.ModuleTitle = module?.Title;
        return View("LectureEdit", input);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteLecture(int id, CancellationToken ct)
    {
        var result = await courses.DeleteLectureAsync(id, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
            return RedirectToAction(nameof(Index));
        }
        Success("Lecture deleted.");
        return RedirectToAction(nameof(Builder), new { id = result.Value });
    }

    [HttpPost]
    public async Task<IActionResult> ReorderLectures([FromBody] ReorderInput input, CancellationToken ct)
    {
        var result = await courses.ReorderLecturesAsync(input, ct);
        return result.IsSuccess ? Ok() : BadRequest(new { message = result.Error!.Message });
    }

    // ------------------------------------------------------------- helpers

    private async Task<SelectList> LanguageListAsync(CancellationToken ct, int? selected) =>
        new((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);

    private async Task ModuleViewBagAsync(int courseId, int? moduleId, CancellationToken ct)
    {
        ViewBag.Prerequisites = new SelectList(
            (await courses.ModuleOptionsAsync(courseId, moduleId, ct)).Select(m => new { m.Id, m.Title }), "Id", "Title");
    }
}
