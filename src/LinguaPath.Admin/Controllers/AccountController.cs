using LinguaPath.Admin.Infrastructure;
using LinguaPath.Admin.Models;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Admin.Controllers;

public sealed class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<AccountController> logger) : AdminControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        const string invalid = "Email or password is incorrect.";
        var user = await userManager.FindByEmailAsync(model.Email.Trim());
        if (user is null || user.IsDeleted || user.UserType != UserType.Admin)
        {
            ModelState.AddModelError(string.Empty, invalid);
            return View(model);
        }
        if (!user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "This admin account is deactivated. Ask a Super Admin.");
            return View(model);
        }

        var roles = await userManager.GetRolesAsync(user);
        if (!roles.Any(r => Roles.AdminRoles.Contains(r)))
        {
            ModelState.AddModelError(string.Empty, "This account has no admin role.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Too many failed attempts. Try again in 10 minutes.");
            return View(model);
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, invalid);
            return View(model);
        }

        user.LastLoginAt = clock.GetUtcNow();
        await userManager.UpdateAsync(user);
        logger.LogInformation("Admin {Email} signed in", user.Email);

        return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : Url.Action("Index", "Dashboard")!);
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    [HttpGet]
    [Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.GetUserAsync(User);
        if (user is null) return RedirectToAction(nameof(Login));

        var result = await userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code == "PasswordMismatch" ? nameof(model.CurrentPassword) : nameof(model.NewPassword), e.Description);
            return View(model);
        }

        await signInManager.RefreshSignInAsync(user);
        await audit.LogAsync("ChangePassword", "AdminUser", user.Id.ToString());
        Success("Your password was changed.");
        return RedirectToAction("Index", "Dashboard");
    }
}

[AllowAnonymous]
public sealed class ErrorController : Controller
{
    [Route("Error")]
    public IActionResult Index(int? code = null)
    {
        ViewBag.Code = code ?? 500;
        return View("~/Views/Shared/Error.cshtml");
    }
}
