using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>Issued certificates: view, PDF, revoke, reissue, eligibility check (FR-A83).</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class CertificatesController(ICertificateAdminService certificates, ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(string? search, int? languageId, bool? revoked, int page = 1, CancellationToken ct = default)
    {
        ViewBag.Search = search;
        ViewBag.LanguageId = languageId;
        ViewBag.Revoked = revoked;
        ViewBag.Page = page;
        ViewBag.Languages = new SelectList((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", languageId);
        ViewBag.ThisMonth = await certificates.IssuedThisMonthAsync(ct);
        return View(await certificates.ListAsync(new CertificateFilter(search, languageId, revoked, page), ct));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var model = await certificates.GetAsync(id, ct);
        if (model is null) return NotFound();
        var eligibility = await certificates.EligibilityAsync(model.LearnerCourseId, ct);
        ViewBag.Eligibility = eligibility.IsSuccess ? eligibility.Value : null;
        return View(model);
    }

    /// <summary>Opens in the browser (inline) so admins can look before downloading.</summary>
    public async Task<IActionResult> Pdf(int id, CancellationToken ct)
    {
        var pdf = await certificates.PdfAsync(id, ct);
        if (pdf is null) return NotFound();
        Response.Headers.ContentDisposition = "inline; filename=certificate.pdf";
        return File(pdf, "application/pdf");
    }

    [HttpPost]
    public async Task<IActionResult> Revoke(int id, string? reason, bool revokePoints, CancellationToken ct)
    {
        var result = await certificates.RevokeAsync(id, reason ?? "", revokePoints, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Certificate revoked. The verification page now shows it as revoked.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Reissue(int id, CancellationToken ct)
    {
        var result = await certificates.ReissueAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Certificate reissued with current details and template; the number stays the same.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>BR-06 checklist for one learner course, with "issue now" when eligible (e.g. after a rule change).</summary>
    public async Task<IActionResult> Eligibility(int learnerCourseId, Guid? learnerId, CancellationToken ct)
    {
        var result = await certificates.EligibilityAsync(learnerCourseId, ct);
        if (result.IsFailure) return NotFound();
        ViewBag.LearnerId = learnerId;
        return View(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Issue(int learnerCourseId, Guid? learnerId, CancellationToken ct)
    {
        var result = await certificates.IssueIfEligibleAsync(learnerCourseId, ct);
        if (result.IsFailure)
        {
            Failure(result.Error!.Message);
            return RedirectToAction(nameof(Eligibility), new { learnerCourseId, learnerId });
        }
        Success("Certificate issued.");
        return RedirectToAction(nameof(Details), new { id = result.Value });
    }
}

/// <summary>Certificate designs with placeholders, signature and logo; per-language override (FR-A82).</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class CertificateTemplatesController(ICertificateAdminService certificates, ILanguageAdminService languages, IFileStorage storage)
    : AdminControllerBase
{
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp"
    };
    private const long MaxImageBytes = 2 * 1024 * 1024;

    public async Task<IActionResult> Index(CancellationToken ct) => View(await certificates.TemplatesAsync(ct));

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await BagAsync(null, ct);
        return View("Edit", new CertificateTemplateInput { Name = "New template" });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var model = await certificates.GetTemplateAsync(id, ct);
        if (model is null) return NotFound();
        await BagAsync(model.LanguageId, ct);
        return View(model);
    }

    [HttpPost]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Save(CertificateTemplateInput input, IFormFile? logo, IFormFile? signature, IFormFile? background,
        bool removeLogo, bool removeSignature, bool removeBackground, string? then, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var images = await ApplyImagesAsync(input, logo, signature, background, removeLogo, removeSignature, removeBackground, ct);
            if (images is not null) ModelState.AddModelError(string.Empty, images);
        }
        if (ModelState.IsValid)
        {
            if (then == "preview")
            {
                // Preview what's in the form without saving it.
                var pdf = await certificates.PreviewTemplateAsync(input, ct);
                Response.Headers.ContentDisposition = "inline; filename=preview.pdf";
                return File(pdf, "application/pdf");
            }
            var result = await certificates.SaveTemplateAsync(input, ct);
            if (result.IsSuccess)
            {
                Success("Template saved. New certificates use it; existing ones keep theirs until reissued.");
                return RedirectToAction(nameof(Edit), new { id = result.Value });
            }
            AddErrors(result.Error!);
        }
        await BagAsync(input.LanguageId, ct);
        return View("Edit", input);
    }

    public async Task<IActionResult> Preview(int id, CancellationToken ct)
    {
        var model = await certificates.GetTemplateAsync(id, ct);
        if (model is null) return NotFound();
        Response.Headers.ContentDisposition = "inline; filename=preview.pdf";
        return File(await certificates.PreviewTemplateAsync(model, ct), "application/pdf");
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await certificates.DeleteTemplateAsync(id, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Template deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<string?> ApplyImagesAsync(CertificateTemplateInput input, IFormFile? logo, IFormFile? signature, IFormFile? background,
        bool removeLogo, bool removeSignature, bool removeBackground, CancellationToken ct)
    {
        async Task<(string? Url, string? Error)> Upload(IFormFile? file, string? current, bool remove)
        {
            if (remove) return (null, null);
            if (file is null || file.Length == 0) return (current, null);
            if (file.Length > MaxImageBytes) return (current, $"{file.FileName}: images can be at most 2 MB.");
            if (!ImageTypes.TryGetValue(file.ContentType, out var ext)) return (current, $"{file.FileName}: use PNG, JPEG or WebP.");
            await using var stream = file.OpenReadStream();
            return (await storage.SaveAsync(stream, "certificates", ext, file.ContentType, ct), null);
        }

        var l = await Upload(logo, input.LogoUrl, removeLogo);
        var s = await Upload(signature, input.SignatureUrl, removeSignature);
        var b = await Upload(background, input.BackgroundUrl, removeBackground);
        input.LogoUrl = l.Url;
        input.SignatureUrl = s.Url;
        input.BackgroundUrl = b.Url;
        return l.Error ?? s.Error ?? b.Error;
    }

    private async Task BagAsync(int? languageId, CancellationToken ct) =>
        ViewBag.Languages = new SelectList((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", languageId);
}

/// <summary>Points rules and learner adjustments (FR-S03, FR-S10).</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class PointsController(IPointsAdminService points) : AdminControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await points.RulesAsync(ct));

    [HttpPost]
    public async Task<IActionResult> UpdateRule(PointAction rule, int value, int dailyCap, bool isActive, CancellationToken ct)
    {
        // "rule", not "action": MVC fills a parameter called action from the route.
        var result = await points.UpdateRuleAsync(rule, value, dailyCap, isActive, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success($"Rule \"{rule}\" saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Adjust(Guid userId, int amount, string? reason, int? languageId, CancellationToken ct)
    {
        var result = await points.AdjustAsync(userId, amount, reason ?? "", languageId, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success($"{(amount > 0 ? "Added" : "Deducted")} {Math.Abs(amount)} points.");
        return RedirectToAction("Details", "Learners", new { id = userId });
    }

    [HttpPost]
    public async Task<IActionResult> Revoke(long id, Guid userId, string? reason, CancellationToken ct)
    {
        var result = await points.RevokeAsync(id, reason ?? "", ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Points revoked.");
        return RedirectToAction("Details", "Learners", new { id = userId });
    }
}
