using System.Security.Cryptography;
using LinguaPath.Admin.Infrastructure;
using LinguaPath.Admin.Models;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Admin.Controllers;

public sealed class DashboardController(IDashboardService dashboard, Application.Features.Ai.IAiContentService ai,
    Application.Features.Progress.ICertificateAdminService certificates) : AdminControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewBag.AiBudget = await ai.BudgetAsync(ct);
        // FR-A01: certificates issued this month.
        ViewBag.CertificatesThisMonth = await certificates.IssuedThisMonthAsync(ct);
        return View(await dashboard.GetAsync(ct));
    }
}

/// <summary>Admin accounts and roles (FR-A91). Super Admin only.</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class AdminUsersController(IAdminUserService admins) : AdminControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await admins.ListAsync(ct));

    [HttpGet]
    public IActionResult Create() => View(new CreateAdminUserInput());

    [HttpPost]
    public async Task<IActionResult> Create(CreateAdminUserInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);

        var loginUrl = Url.Action("Login", "Account", null, Request.Scheme)!;
        var result = await admins.CreateAsync(input, loginUrl, ct);
        if (result.IsFailure)
        {
            AddErrors(result.Error!);
            return View(input);
        }
        Success($"Admin {input.Email} created. Share the temporary password with them securely.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var model = await admins.GetForEditAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(EditAdminUserInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);

        var result = await admins.UpdateAsync(input, ct);
        if (result.IsFailure)
        {
            AddErrors(result.Error!);
            return View(input);
        }
        Success("Admin user saved.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Sets a random temporary password and shows it once.</summary>
    [HttpPost]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken ct)
    {
        var temp = TemporaryPassword();
        var result = await admins.ResetPasswordAsync(id, temp, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success($"Temporary password: {temp} — it is shown only once. Ask the admin to change it after signing in.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    private static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789";
        var all = upper + lower + digits;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)]
        };
        while (chars.Count < 12) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).ToArray());
    }
}

/// <summary>Learner list, details and suspend/reactivate (FR-A90). Progress views arrive with Phase 2+.</summary>
public sealed class LearnersController(ILearnerAdminService learners) : AdminControllerBase
{
    public async Task<IActionResult> Index(string? search, LearnerStatusFilter status = LearnerStatusFilter.All, int page = 1, CancellationToken ct = default) =>
        View(new LearnerIndexViewModel
        {
            Search = search,
            Status = status,
            Results = await learners.SearchAsync(search, status, page, 25, ct)
        });

    public async Task<IActionResult> Details(Guid id, [FromServices] IGradingService grading,
        [FromServices] Application.Features.Progress.IPointsService points,
        [FromServices] Application.Features.Progress.IPointsAdminService pointsAdmin,
        [FromServices] Application.Features.Progress.ICertificateAdminService certificates,
        [FromServices] ILanguageAdminService languages,
        [FromServices] Application.Features.Moderation.IModerationAdminService moderation, CancellationToken ct)
    {
        var model = await learners.GetAsync(id, ct);
        if (model is null) return NotFound();
        ViewBag.Attempts = await grading.AttemptsForLearnerAsync(id, ct);
        // Phase 7: wallet, streak, badges, certificates.
        ViewBag.Points = await points.SummaryAsync(id, ct);
        ViewBag.Transactions = await pointsAdmin.TransactionsAsync(id, 50, ct);
        ViewBag.Certificates = await certificates.ForLearnerAsync(id, ct);
        // Phase 8: strikes, suspension, recent moderation events.
        ViewBag.Moderation = await moderation.LearnerAsync(id, ct);
        ViewBag.LanguageOptions = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
            (await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name");
        return View(model);
    }

    [HttpPost]
    [Authorize(Policy = Policies.Moderators)]
    public async Task<IActionResult> Suspend(Guid id, string? reason, CancellationToken ct)
    {
        var result = await learners.SetActiveAsync(id, false, reason, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Learner suspended and signed out of all devices.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [Authorize(Policy = Policies.Moderators)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var result = await learners.SetActiveAsync(id, true, null, ct);
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success("Learner reactivated.");
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>System settings and business-rule values (FR-A111, FR-A112). Super Admin only.</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class SettingsController(ISettingsService settings) : AdminControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var all = await settings.GetAllAsync(ct);
        return View(new SettingsViewModel { Groups = all.GroupBy(s => s.Group).ToList() });
    }

    [HttpPost]
    public async Task<IActionResult> Save(Dictionary<string, string> values, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (key, value) in values)
        {
            var result = await settings.SetAsync(key, value, ct);
            if (result.IsFailure) errors.Add($"{key}: {result.Error!.Message}");
        }

        if (errors.Count > 0) Failure(string.Join(" ", errors));
        else Success("Settings saved.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Read-only audit trail (FR-A92). Super Admin only.</summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
public sealed class AuditLogsController(IAuditLogQueryService auditLogs) : AdminControllerBase
{
    public async Task<IActionResult> Index(string? search, string? entityType, DateOnly? from, DateOnly? to, int page = 1, CancellationToken ct = default)
    {
        var filter = new AuditLogFilter(search, entityType, from, to, page, 50);
        return View(new AuditLogIndexViewModel
        {
            Filter = filter,
            EntityTypes = await auditLogs.EntityTypesAsync(ct),
            Results = await auditLogs.SearchAsync(filter, ct)
        });
    }
}
