using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Learning;

public interface ICourseCatalogService
{
    /// <summary>Published courses of a published language (onboarding).</summary>
    Task<Result<IReadOnlyList<CourseDto>>> GetCoursesAsync(int languageId, CancellationToken ct = default);

    /// <summary>"Request this language" (FR-L10). Asking twice is harmless.</summary>
    Task<Result> RequestLanguageAsync(Guid userId, CreateLanguageRequest request, CancellationToken ct = default);
}

internal sealed class CourseCatalogService(IApplicationDbContext db, TimeProvider clock) : ICourseCatalogService
{
    public async Task<Result<IReadOnlyList<CourseDto>>> GetCoursesAsync(int languageId, CancellationToken ct = default)
    {
        var language = await db.Languages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == languageId, ct);
        if (language is null || language.Status != LanguageStatus.Published) return Error.NotFound("Language");

        var courses = await db.Courses.AsNoTracking()
            .Where(c => c.LanguageId == languageId && c.Status == ContentStatus.Published)
            .OrderBy(c => c.StartLevel).ThenBy(c => c.SortOrder)
            .Select(c => new
            {
                c.Id, c.LanguageId, c.Title, c.Description, c.CoverUrl, c.StartLevel, c.EndLevel,
                Modules = c.Modules.Count(m => !m.IsDeleted),
                Lectures = c.Modules.Where(m => !m.IsDeleted).SelectMany(m => m.Lectures)
                    .Count(l => !l.IsDeleted && l.Status == ContentStatus.Published),
                Minutes = c.Modules.Where(m => !m.IsDeleted).SelectMany(m => m.Lectures)
                    .Where(l => !l.IsDeleted && l.Status == ContentStatus.Published)
                    .Sum(l => (int?)l.EstimatedMinutes) ?? 0
            })
            .ToListAsync(ct);

        return courses.Select(c => new CourseDto(c.Id, c.LanguageId, c.Title, c.Description, c.CoverUrl, c.StartLevel, c.EndLevel,
            c.Modules, c.Lectures, Math.Round(c.Minutes / 60m, 1))).ToList();
    }

    public async Task<Result> RequestLanguageAsync(Guid userId, CreateLanguageRequest request, CancellationToken ct = default)
    {
        var name = request.LanguageName.Trim();
        int? languageId = null;
        if (request.LanguageId is { } id)
        {
            var language = await db.Languages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
            if (language is null) return Error.Validation(nameof(request.LanguageId), "Unknown language.");
            if (language.Status == LanguageStatus.Published)
                return Error.Validation(nameof(request.LanguageId), $"{language.Name} is already available. You can start it now.");
            name = language.Name;
            languageId = language.Id;
        }

        var normalized = Normalize(name);
        if (await db.LanguageRequests.AnyAsync(r => r.RequestedByUserId == userId && r.NormalizedName == normalized, ct))
            return Result.Success();

        db.LanguageRequests.Add(new LanguageRequest
        {
            LanguageName = name,
            NormalizedName = normalized,
            LanguageId = languageId,
            RequestedByUserId = userId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    internal static string Normalize(string name) =>
        string.Join(' ', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
