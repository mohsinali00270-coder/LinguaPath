using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Admin;

// ===================================================================== dashboard (FR-A01, partial)

public interface IDashboardService
{
    Task<DashboardStats> GetAsync(CancellationToken ct = default);
}

internal sealed class DashboardService(IApplicationDbContext db, TimeProvider clock) : IDashboardService
{
    public async Task<DashboardStats> GetAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var learners = db.Users.AsNoTracking().Where(u => u.UserType == UserType.Learner && !u.IsDeleted);

        var since14 = todayStart.AddDays(-13);
        var recent = await learners.Where(u => u.CreatedAt >= since14).Select(u => u.CreatedAt).ToListAsync(ct);
        var signups = Enumerable.Range(0, 14)
            .Select(i => DateOnly.FromDateTime(since14.AddDays(i).UtcDateTime))
            .Select(day => new DailyCount(day, recent.Count(c => DateOnly.FromDateTime(c.UtcDateTime) == day)))
            .ToList();

        return new DashboardStats(
            TotalLearners: await learners.CountAsync(ct),
            ActiveToday: await learners.CountAsync(u => u.LastLoginAt >= todayStart, ct),
            ActiveLast30Days: await learners.CountAsync(u => u.LastLoginAt >= todayStart.AddDays(-30), ct),
            NewThisWeek: await learners.CountAsync(u => u.CreatedAt >= todayStart.AddDays(-7), ct),
            UnverifiedLearners: await learners.CountAsync(u => !u.EmailConfirmed, ct),
            SuspendedLearners: await learners.CountAsync(u => !u.IsActive && !u.IsBlocked, ct),
            BlockedLearners: await learners.CountAsync(u => u.IsBlocked, ct),
            AdminUsers: await db.Users.CountAsync(u => u.UserType == UserType.Admin && !u.IsDeleted, ct),
            PublishedLanguages: await db.Languages.CountAsync(l => !l.IsDeleted && l.Status == LanguageStatus.Published, ct),
            ComingSoonLanguages: await db.Languages.CountAsync(l => !l.IsDeleted && l.Status == LanguageStatus.ComingSoon, ct),
            SignupsLast14Days: signups,
            ActiveEnrolments: await db.LearnerCourses.CountAsync(c => c.Status == LearnerCourseStatus.Active || c.Status == LearnerCourseStatus.Paused, ct),
            PublishedCourses: await db.Courses.CountAsync(c => c.Status == ContentStatus.Published, ct),
            PendingLanguageRequests: await db.LanguageRequests.CountAsync(r => r.Status == LanguageRequestStatus.Pending, ct),
            PendingReviews: await db.ReviewItems.CountAsync(r => r.Status == ReviewStatus.Pending, ct),
            OpenReports: await db.ContentReports.CountAsync(r => r.Status == ReportStatus.Open, ct),
            PendingGrading: await db.AttemptAnswers.CountAsync(a => a.NeedsReview && a.Attempt.Status == AttemptStatus.PendingReview, ct));
    }
}

// ===================================================================== admin users (FR-A91)

public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserListItem>> ListAsync(CancellationToken ct = default);
    Task<EditAdminUserInput?> GetForEditAsync(Guid id, CancellationToken ct = default);
    Task<Result<Guid>> CreateAsync(CreateAdminUserInput input, string loginUrl, CancellationToken ct = default);
    Task<Result> UpdateAsync(EditAdminUserInput input, CancellationToken ct = default);
    Task<Result> ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct = default);
}

internal sealed class AdminUserService(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IEmailSender email,
    IOptions<AppOptions> appOptions,
    TimeProvider clock) : IAdminUserService
{
    public async Task<IReadOnlyList<AdminUserListItem>> ListAsync(CancellationToken ct = default)
    {
        var admins = await db.Users.AsNoTracking()
            .Where(u => u.UserType == UserType.Admin && !u.IsDeleted)
            .OrderBy(u => u.FullName)
            .ToListAsync(ct);

        var ids = admins.Select(a => a.Id).ToList();
        var roleMap = await (from ur in db.UserRoles
                             join r in db.Roles on ur.RoleId equals r.Id
                             where ids.Contains(ur.UserId)
                             select new { ur.UserId, r.Name }).ToListAsync(ct);

        return admins.Select(a => new AdminUserListItem(
            a.Id, a.Email!, a.FullName,
            roleMap.Where(m => m.UserId == a.Id).Select(m => m.Name!).OrderBy(n => n).ToList(),
            a.IsActive, a.LastLoginAt, a.CreatedAt)).ToList();
    }

    public async Task<EditAdminUserInput?> GetForEditAsync(Guid id, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || user.UserType != UserType.Admin || user.IsDeleted) return null;
        return new EditAdminUserInput
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email!,
            IsActive = user.IsActive,
            Roles = (await userManager.GetRolesAsync(user)).ToList()
        };
    }

    public async Task<Result<Guid>> CreateAsync(CreateAdminUserInput input, string loginUrl, CancellationToken ct = default)
    {
        var roles = input.Roles.Where(r => Roles.AdminRoles.Contains(r)).Distinct().ToList();
        if (roles.Count == 0) return Error.Validation(nameof(input.Roles), "Pick at least one admin role.");
        if (await userManager.FindByEmailAsync(input.Email) is not null)
            return Error.Validation(nameof(input.Email), "This email is already used by another account.");

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = input.Email.Trim(),
            Email = input.Email.Trim(),
            EmailConfirmed = true,
            FullName = input.FullName.Trim(),
            UserType = UserType.Admin,
            CreatedAt = clock.GetUtcNow()
        };
        var created = await userManager.CreateAsync(user, input.Password);
        if (!created.Succeeded) return created.ToError();
        await userManager.AddToRolesAsync(user, roles);

        await audit.LogAsync("Create", "AdminUser", user.Id.ToString(), null, new { user.Email, user.FullName, Roles = roles }, ct);

        var (subject, html) = EmailTemplates.AdminAccountCreated(appOptions.Value.Name, user.FullName, loginUrl);
        await email.SendAsync(user.Email!, subject, html, ct);
        return user.Id;
    }

    public async Task<Result> UpdateAsync(EditAdminUserInput input, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(input.Id.ToString());
        if (user is null || user.UserType != UserType.Admin || user.IsDeleted) return Error.NotFound("Admin user");

        var newRoles = input.Roles.Where(r => Roles.AdminRoles.Contains(r)).Distinct().ToList();
        if (newRoles.Count == 0) return Error.Validation(nameof(input.Roles), "Pick at least one admin role.");

        var oldRoles = (await userManager.GetRolesAsync(user)).ToList();
        var isSelf = currentUser.UserId == user.Id;

        if (isSelf && !input.IsActive)
            return Error.Validation(nameof(input.IsActive), "You cannot deactivate your own account.");
        if (isSelf && oldRoles.Contains(Roles.SuperAdmin) && !newRoles.Contains(Roles.SuperAdmin))
            return Error.Validation(nameof(input.Roles), "You cannot remove your own Super Admin role.");

        var losingSuperAdmin = oldRoles.Contains(Roles.SuperAdmin) && (!newRoles.Contains(Roles.SuperAdmin) || !input.IsActive);
        if (losingSuperAdmin)
        {
            var activeSupers = (await userManager.GetUsersInRoleAsync(Roles.SuperAdmin)).Count(u => u.IsActive && !u.IsDeleted);
            if (activeSupers <= 1)
                return Error.Validation(nameof(input.Roles), "There must always be at least one active Super Admin.");
        }

        var before = new { user.FullName, user.IsActive, Roles = oldRoles };
        user.FullName = input.FullName.Trim();
        var deactivated = user.IsActive && !input.IsActive;
        user.IsActive = input.IsActive;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded) return updated.ToError();

        var toRemove = oldRoles.Except(newRoles).ToList();
        var toAdd = newRoles.Except(oldRoles).ToList();
        if (toRemove.Count > 0) await userManager.RemoveFromRolesAsync(user, toRemove);
        if (toAdd.Count > 0) await userManager.AddToRolesAsync(user, toAdd);
        if (deactivated || toRemove.Count > 0) await userManager.UpdateSecurityStampAsync(user); // signs them out

        await audit.LogAsync("Update", "AdminUser", user.Id.ToString(), before, new { user.FullName, user.IsActive, Roles = newRoles }, ct);
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || user.UserType != UserType.Admin || user.IsDeleted) return Error.NotFound("Admin user");

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded) return result.ToError("NewPassword");
        await userManager.SetLockoutEndDateAsync(user, null);

        await audit.LogAsync("ResetPassword", "AdminUser", user.Id.ToString(), ct: ct);
        return Result.Success();
    }
}

// ===================================================================== learners (FR-A90, Phase 1 part)

public interface ILearnerAdminService
{
    Task<PagedResult<LearnerListItem>> SearchAsync(string? search, LearnerStatusFilter status, int page, int pageSize, CancellationToken ct = default);
    Task<LearnerDetails?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result> SetActiveAsync(Guid id, bool isActive, string? reason, CancellationToken ct = default);
}

internal sealed class LearnerAdminService(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IAuditLogger audit,
    TimeProvider clock) : ILearnerAdminService
{
    public async Task<PagedResult<LearnerListItem>> SearchAsync(string? search, LearnerStatusFilter status, int page, int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        var q = db.Users.AsNoTracking().Where(u => u.UserType == UserType.Learner && !u.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(u => u.Email!.Contains(s) || u.FullName.Contains(s) || (u.DisplayName != null && u.DisplayName.Contains(s)));
        }
        q = status switch
        {
            LearnerStatusFilter.Active => q.Where(u => u.IsActive && !u.IsBlocked && u.EmailConfirmed),
            LearnerStatusFilter.Unverified => q.Where(u => !u.EmailConfirmed),
            LearnerStatusFilter.Suspended => q.Where(u => !u.IsActive && !u.IsBlocked),
            LearnerStatusFilter.Blocked => q.Where(u => u.IsBlocked),
            _ => q
        };

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new LearnerListItem(u.Id, u.Email!, u.FullName,
                u.NativeLanguage != null ? u.NativeLanguage.Name : null,
                u.EmailConfirmed, u.IsActive, u.IsBlocked, u.CreatedAt, u.LastLoginAt))
            .ToListAsync(ct);

        return new PagedResult<LearnerListItem> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<LearnerDetails?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().Include(x => x.NativeLanguage)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserType == UserType.Learner && !x.IsDeleted, ct);
        if (u is null) return null;

        var now = clock.GetUtcNow();
        var logins = await db.UserLogins.AsNoTracking().Where(l => l.UserId == id).Select(l => l.LoginProvider).ToListAsync(ct);
        var devices = await db.UserDevices.AsNoTracking().Where(d => d.UserId == id)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new LearnerDevice(d.DeviceId, d.Platform.ToString(), d.AppVersion, d.LastSeenAt))
            .ToListAsync(ct);
        var sessions = await db.RefreshTokens.CountAsync(t => t.UserId == id && t.RevokedAt == null && t.ExpiresAt > now, ct);
        var courses = (await db.LearnerCourses.AsNoTracking()
                .Where(c => c.UserId == id)
                .OrderByDescending(c => c.Id)
                .Select(c => new
                {
                    c.Id, Language = c.Course.Language.Name, Course = c.Course.Title, c.CurrentLevel, c.TargetLevel, c.Status, c.EndDate,
                    Total = c.Items.Count(), Done = c.Items.Count(i => i.State == RoadmapItemState.Completed)
                })
                .ToListAsync(ct))
            .Select(c => new LearnerCourseSummary(c.Language, c.Course, $"{c.CurrentLevel} → {c.TargetLevel}", c.Status.ToString(),
                c.Total == 0 ? 0 : (int)Math.Round(100.0 * c.Done / c.Total), c.EndDate, c.Id))
            .ToList();

        return new LearnerDetails(u.Id, u.Email!, u.FullName, u.DisplayName, u.NativeLanguage?.Name, u.UiLanguage, u.TimeZone,
            u.EmailConfirmed, u.IsActive, u.IsBlocked, u.BlockReason, u.BlockedAt, u.CreatedAt, u.LastLoginAt,
            logins, devices, sessions, courses);
    }

    public async Task<Result> SetActiveAsync(Guid id, bool isActive, string? reason, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || user.UserType != UserType.Learner || user.IsDeleted) return Error.NotFound("Learner");
        if (user.IsBlocked) return Error.Validation("IsActive", "This account is permanently blocked. Unblocking is handled in Moderation.");
        if (!isActive && string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required to suspend a learner.");
        if (user.IsActive == isActive) return Result.Success();

        user.IsActive = isActive;
        await userManager.UpdateAsync(user);

        if (!isActive)
        {
            var now = clock.GetUtcNow();
            var tokens = await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in tokens)
            {
                t.RevokedAt = now;
                t.RevokedReason = "suspended by admin";
            }
            await db.SaveChangesAsync(ct);
            await userManager.UpdateSecurityStampAsync(user);
        }

        await audit.LogAsync(isActive ? "Reactivate" : "Suspend", "Learner", id.ToString(),
            new { IsActive = !isActive }, new { IsActive = isActive, Reason = reason }, ct);
        return Result.Success();
    }
}

// ===================================================================== audit log viewer (FR-A92)

public interface IAuditLogQueryService
{
    Task<PagedResult<AuditLogItem>> SearchAsync(AuditLogFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default);
}

internal sealed class AuditLogQueryService(IApplicationDbContext db) : IAuditLogQueryService
{
    public async Task<PagedResult<AuditLogItem>> SearchAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 10, 200);
        var q = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(a => (a.UserEmail != null && a.UserEmail.Contains(s)) || a.Action.Contains(s) || (a.EntityId != null && a.EntityId.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) q = q.Where(a => a.EntityType == filter.EntityType);
        if (filter.From is { } from)
        {
            var f = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(a => a.CreatedAt >= f);
        }
        if (filter.To is { } to)
        {
            var t = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(a => a.CreatedAt < t);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(a => new AuditLogItem(a.Id, a.CreatedAt, a.UserEmail, a.Action, a.EntityType, a.EntityId, a.OldValues, a.NewValues, a.IpAddress))
            .ToListAsync(ct);
        return new PagedResult<AuditLogItem> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default) =>
        await db.AuditLogs.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(e => e).ToListAsync(ct);
}
