using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Profile;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Profile;

public interface IProfileService
{
    Task<Result<UserProfileDto>> GetAsync(Guid userId, CancellationToken ct = default);
    Task<Result<UserProfileDto>> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);
    Task<Result<PhotoUploadResponse>> UploadPhotoAsync(Guid userId, Stream content, string fileName, string contentType, long length, CancellationToken ct = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task<Result> DeleteAccountAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default);
    Task<Result<Dictionary<string, object?>>> ExportAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Learner profile and data rights (FR-L05, FR-L06).</summary>
internal sealed class ProfileService(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IFileStorage storage,
    Moderation.IModerationService moderation,
    TimeProvider clock) : IProfileService
{
    public const long MaxPhotoBytes = 10 * 1024 * 1024; // NFR-17
    private static readonly Dictionary<string, string> AllowedPhotoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp"
    };

    public async Task<Result<UserProfileDto>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.NativeLanguage)
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return Error.NotFound("User");

        var logins = await db.UserLogins.AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => l.LoginProvider)
            .ToListAsync(ct);

        return ToDto(user, logins);
    }

    public async Task<Result<UserProfileDto>> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return Error.NotFound("User");

        if (request.NativeLanguageId is { } langId &&
            !await db.Languages.AnyAsync(l => l.Id == langId && !l.IsDeleted, ct))
            return Error.Validation(nameof(request.NativeLanguageId), "Selected native language does not exist.");

        // FR-M01 / BR-25: names and bios are public, so links, contacts and blocked words are refused.
        if (request.DisplayName != user.DisplayName)
        {
            var nameCheck = await moderation.CheckTextAsync(userId, LinguaPath.Shared.ModerationTarget.DisplayName, request.DisplayName, ct);
            if (nameCheck.IsFailure) return nameCheck.Error!;
        }
        if (request.Bio != user.Bio)
        {
            var bioCheck = await moderation.CheckTextAsync(userId, LinguaPath.Shared.ModerationTarget.Bio, request.Bio, ct);
            if (bioCheck.IsFailure) return bioCheck.Error!;
        }

        user.FullName = request.FullName.Trim();
        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        user.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        user.NativeLanguageId = request.NativeLanguageId;
        user.UiLanguage = request.UiLanguage;
        user.TimeZone = request.TimeZone;
        user.PreferredStudyTime = request.PreferredStudyTime;
        // BR-32: the date of birth can be added once; changing it later goes through support.
        if (user.DateOfBirth is null && request.DateOfBirth is { } dob) user.DateOfBirth = dob;
        user.NotifyStudyReminders = request.Notifications.StudyReminders;
        user.NotifyQuizReady = request.Notifications.QuizReady;
        user.NotifyExamReminders = request.Notifications.ExamReminders;
        user.NotifyStreakAlerts = request.Notifications.StreakAlerts;
        user.NotifyCertificates = request.Notifications.Certificates;
        user.NotifyNewPosts = request.Notifications.NewPosts;

        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, ct);
    }

    public async Task<Result<PhotoUploadResponse>> UploadPhotoAsync(Guid userId, Stream content, string fileName,
        string contentType, long length, CancellationToken ct = default)
    {
        if (length <= 0) return Error.Validation("Photo", "The file is empty.");
        if (length > MaxPhotoBytes) return Error.Validation("Photo", "The photo must be 10 MB or smaller.");
        if (!AllowedPhotoTypes.TryGetValue(contentType, out var extension))
            return Error.Validation("Photo", "Only JPG, PNG or WEBP images are allowed.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return Error.NotFound("User");

        var url = await storage.SaveAsync(content, "avatars", extension, contentType, ct);
        var old = user.PhotoUrl;
        user.PhotoUrl = url;
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(old, ct);

        return new PhotoUploadResponse(url);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDeleted) return Error.NotFound("User");

        // Google/Apple-only accounts have no password yet: this sets one.
        var result = await userManager.HasPasswordAsync(user)
            ? await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword)
            : await userManager.AddPasswordAsync(user, request.NewPassword);

        if (result.Succeeded) return Result.Success();
        if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
            return Error.Validation(nameof(request.CurrentPassword), "Current password is incorrect.");
        return result.ToError(nameof(request.NewPassword));
    }

    public async Task<Result> DeleteAccountAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDeleted) return Error.NotFound("User");

        if (await userManager.HasPasswordAsync(user))
        {
            if (string.IsNullOrEmpty(request.Password) || !await userManager.CheckPasswordAsync(user, request.Password))
                return Error.Validation(nameof(request.Password), "Password is incorrect.");
        }
        else if (!string.Equals(request.Confirmation, "DELETE", StringComparison.Ordinal))
        {
            return Error.Validation(nameof(request.Confirmation), "Type DELETE to confirm.");
        }

        var now = clock.GetUtcNow();
        var photo = user.PhotoUrl;

        // Anonymise personal data right away (NFR-08). The row stays so foreign keys and audit history remain valid.
        var anonymous = $"deleted-{user.Id:N}@deleted.linguapath";
        user.Email = anonymous;
        user.NormalizedEmail = anonymous.ToUpperInvariant();
        user.UserName = anonymous;
        user.NormalizedUserName = anonymous.ToUpperInvariant();
        user.FullName = "Deleted user";
        user.DisplayName = null;
        user.Bio = null;
        user.PhotoUrl = null;
        user.PhoneNumber = null;
        user.DateOfBirth = null;
        user.PasswordHash = null;
        user.IsActive = false;
        user.IsDeleted = true;
        user.DeletedAt = now;
        user.SecurityStamp = Guid.NewGuid().ToString();

        var logins = await db.UserLogins.Where(l => l.UserId == userId).ToListAsync(ct);
        db.UserLogins.RemoveRange(logins);
        var devices = await db.UserDevices.Where(d => d.UserId == userId).ToListAsync(ct);
        db.UserDevices.RemoveRange(devices);
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens)
        {
            t.RevokedAt = now;
            t.RevokedReason = "account deleted";
        }

        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(photo, ct);
        return Result.Success();
    }

    public async Task<Result<Dictionary<string, object?>>> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await GetAsync(userId, ct);
        if (profile.IsFailure) return profile.Error!;

        var devices = await db.UserDevices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new { d.DeviceId, Platform = d.Platform.ToString(), d.AppVersion, d.CreatedAt, d.LastSeenAt })
            .ToListAsync(ct);

        var courses = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new
            {
                Course = c.Course.Title, Language = c.Course.Language.Name, c.CurrentLevel, c.TargetLevel, Goal = c.Goal.ToString(),
                c.StartDate, c.EndDate, c.DailyMinutes, Status = c.Status.ToString(),
                CompletedLectures = c.Items.Count(i => i.State == LinguaPath.Shared.RoadmapItemState.Completed)
            })
            .ToListAsync(ct);
        var requests = await db.LanguageRequests.AsNoTracking()
            .Where(r => r.RequestedByUserId == userId)
            .Select(r => new { r.LanguageName, r.CreatedAt })
            .ToListAsync(ct);

        // Later phases add marks, certificates, points and chat messages.
        return new Dictionary<string, object?>
        {
            ["exportedAt"] = clock.GetUtcNow(),
            ["profile"] = profile.Value,
            ["devices"] = devices,
            ["courses"] = courses,
            ["languageRequests"] = requests
        };
    }

    private static UserProfileDto ToDto(ApplicationUser u, IReadOnlyList<string> logins) => new(
        u.Id, u.Email!, u.FullName, u.DisplayName, u.Bio, u.PhotoUrl,
        u.NativeLanguageId, u.NativeLanguage?.Name, u.UiLanguage, u.TimeZone, u.PreferredStudyTime,
        new NotificationPreferencesDto(u.NotifyStudyReminders, u.NotifyQuizReady, u.NotifyExamReminders,
            u.NotifyStreakAlerts, u.NotifyCertificates, u.NotifyNewPosts),
        HasPassword: u.PasswordHash != null,
        ExternalLogins: logins,
        u.CreatedAt,
        u.DateOfBirth);
}
