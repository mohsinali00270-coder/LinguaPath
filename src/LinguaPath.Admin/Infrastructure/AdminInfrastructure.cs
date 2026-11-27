using System.Security.Claims;
using LinguaPath.Application.Common;
using LinguaPath.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace LinguaPath.Admin.Infrastructure;

/// <summary>Adds the admin's full name to the cookie so the layout can greet them.</summary>
public sealed class AdminClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    public const string FullNameClaim = "full_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(FullNameClaim, user.FullName));
        return identity;
    }
}

public abstract class AdminControllerBase : Controller
{
    protected void Success(string message) => TempData["Success"] = message;
    protected void Failure(string message) => TempData["Error"] = message;

    /// <summary>Copies a business error into ModelState so the form shows it beside the right field.</summary>
    protected void AddErrors(Error error, string? prefix = null)
    {
        if (error.Details is { Count: > 0 })
        {
            foreach (var (field, messages) in error.Details)
            foreach (var message in messages)
                ModelState.AddModelError(Key(prefix, field), message);
        }
        else
        {
            ModelState.AddModelError(string.Empty, error.Message);
        }
    }

    private static string Key(string? prefix, string field) =>
        field == "General" ? string.Empty : string.IsNullOrEmpty(prefix) ? field : $"{prefix}.{field}";
}

public static class ClaimsExtensions
{
    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(AdminClaimsPrincipalFactory.FullNameClaim) ?? user.Identity?.Name ?? "Admin";
}

/// <summary>Sidebar entry. Items with a Phase number are planned modules shown greyed out until built.</summary>
public sealed record NavItem(string Title, string Controller, string Action, string[] Roles, int? Phase = null, string? Section = null);

public static class Navigation
{
    private static readonly string[] Super = [LinguaPath.Domain.Constants.Roles.SuperAdmin];
    private static readonly string[] Content = [LinguaPath.Domain.Constants.Roles.SuperAdmin, LinguaPath.Domain.Constants.Roles.ContentAdmin];
    private static readonly string[] Review = [LinguaPath.Domain.Constants.Roles.SuperAdmin, LinguaPath.Domain.Constants.Roles.Reviewer];
    private static readonly string[] Moderate = [LinguaPath.Domain.Constants.Roles.SuperAdmin, LinguaPath.Domain.Constants.Roles.Moderator];
    private static readonly string[] Any = LinguaPath.Domain.Constants.Roles.AdminRoles;

    public static readonly IReadOnlyList<NavItem> Items =
    [
        new("Dashboard", "Dashboard", "Index", Any, Section: "Overview"),

        new("Languages", "Languages", "Index", Content, Section: "Content"),
        new("Courses & roadmaps", "Courses", "Index", Content, Section: "Content"),
        new("Language requests", "LanguageRequests", "Index", Content, Section: "Content"),
        new("Lectures", "Lectures", "Index", Content, Section: "Content"),
        new("Posts", "Posts", "Index", Content, Section: "Content"),
        new("Question bank", "Questions", "Index", Content, Section: "Content"),
        new("Quizzes & exams", "Quizzes", "Index", Content, Section: "Content"),
        new("Practice library", "Practice", "Index", Content, Section: "Content"),
        new("AI generation", "AiRequests", "Index", Content, Section: "Content"),

        new("Review queue", "Reviews", "Index", Review, Section: "Quality"),
        new("Error reports", "Reports", "Index", Any, Section: "Quality"),
        new("Grading queue", "Grading", "Index", Review, Section: "Quality"),
        new("Certificates", "Certificates", "Index", Super, Section: "Quality"),
        new("Certificate templates", "CertificateTemplates", "Index", Super, Section: "Quality"),

        new("Learners", "Learners", "Index", Any, Section: "People"),
        new("Moderation queue", "Moderation", "Index", Moderate, Section: "People"),
        new("Abusive words", "ModerationWords", "Index", Moderate, Section: "People"),
        new("Link rules", "ModerationLinks", "Index", Moderate, Section: "People"),
        new("Moderation report", "Moderation", "Report", Moderate, Section: "People"),
        new("Tournaments", "Tournaments", "Index", Content, Section: "People"),

        new("Analytics reports", "AnalyticsReports", "Index", Super, Section: "System"),
        new("Admin users", "AdminUsers", "Index", Super, Section: "System"),
        new("AI usage & prompts", "AiSettings", "Index", Super, Section: "System"),
        new("Points rules", "Points", "Index", Super, Section: "System"),
        new("Settings", "Settings", "Index", Super, Section: "System"),
        new("Audit log", "AuditLogs", "Index", Super, Section: "System"),
    ];
}

public static class ModelStateExtensions
{
    public static bool HasErrorFor(this ModelStateDictionary modelState, string key) =>
        modelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0;
}
