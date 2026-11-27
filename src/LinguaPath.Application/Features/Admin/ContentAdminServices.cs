using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Admin;

// ===================================================================== languages (FR-A10, FR-A11)

public interface ILanguageAdminService
{
    Task<IReadOnlyList<LanguageListItem>> ListAsync(CancellationToken ct = default);
    Task<LanguageInput?> GetAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(LanguageInput input, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Name)>> OptionsAsync(CancellationToken ct = default);
}

internal sealed class LanguageAdminService(IApplicationDbContext db, IAuditLogger audit) : ILanguageAdminService
{
    public async Task<IReadOnlyList<LanguageListItem>> ListAsync(CancellationToken ct = default)
    {
        var languages = await db.Languages.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        var courses = await db.Courses.AsNoTracking()
            .GroupBy(c => c.LanguageId)
            .Select(g => new { g.Key, Total = g.Count(), Published = g.Count(c => c.Status == ContentStatus.Published) })
            .ToListAsync(ct);
        var learners = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.Status == LearnerCourseStatus.Active || c.Status == LearnerCourseStatus.Paused)
            .GroupBy(c => c.LanguageId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var requests = await db.LanguageRequests.AsNoTracking()
            .Where(r => r.LanguageId != null && r.Status == LanguageRequestStatus.Pending)
            .GroupBy(r => r.LanguageId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        return languages.Select(l => new LanguageListItem(l.Id, l.Name, l.NativeName, l.IsoCode, l.Direction, l.Status,
            courses.FirstOrDefault(c => c.Key == l.Id)?.Total ?? 0,
            courses.FirstOrDefault(c => c.Key == l.Id)?.Published ?? 0,
            learners.FirstOrDefault(x => x.Key == l.Id)?.Count ?? 0,
            requests.FirstOrDefault(x => x.Key == l.Id)?.Count ?? 0,
            l.SortOrder)).ToList();
    }

    public async Task<LanguageInput?> GetAsync(int id, CancellationToken ct = default)
    {
        var l = await db.Languages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new LanguageInput
        {
            Id = l.Id, Name = l.Name, NativeName = l.NativeName, IsoCode = l.IsoCode, FlagUrl = l.FlagUrl, Direction = l.Direction,
            TtsVoice = l.TtsVoice, SttLocale = l.SttLocale, MinLevel = l.MinLevel, MaxLevel = l.MaxLevel, Status = l.Status, SortOrder = l.SortOrder
        };
    }

    public async Task<Result<int>> SaveAsync(LanguageInput input, CancellationToken ct = default)
    {
        var iso = input.IsoCode.Trim();
        if (input.MaxLevel < input.MinLevel) return Error.Validation(nameof(input.MaxLevel), "Highest level must be at or above the lowest level.");
        if (await db.Languages.IgnoreQueryFilters().AnyAsync(l => l.IsoCode == iso && l.Id != input.Id, ct))
            return Error.Validation(nameof(input.IsoCode), "Another language already uses this ISO code.");

        if (input.Status == LanguageStatus.Published && !await db.Courses.AnyAsync(c => c.LanguageId == input.Id && c.Status == ContentStatus.Published, ct))
            return Error.Validation(nameof(input.Status), "Publish at least one course first. Use \"Coming soon\" to show it greyed out in the app.");

        Language language;
        object? before = null;
        if (input.Id == 0)
        {
            language = new Language();
            db.Languages.Add(language);
        }
        else
        {
            language = await db.Languages.FirstOrDefaultAsync(l => l.Id == input.Id, ct) ?? throw new InvalidOperationException();
            before = new { language.Name, language.IsoCode, Status = language.Status.ToString() };
        }

        language.Name = input.Name.Trim();
        language.NativeName = input.NativeName.Trim();
        language.IsoCode = iso;
        language.FlagUrl = string.IsNullOrWhiteSpace(input.FlagUrl) ? null : input.FlagUrl.Trim();
        language.Direction = input.Direction;
        language.TtsVoice = input.TtsVoice?.Trim();
        language.SttLocale = input.SttLocale?.Trim();
        language.MinLevel = input.MinLevel;
        language.MaxLevel = input.MaxLevel;
        language.Status = input.Status;
        language.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);

        // Learners who asked for this language by name are linked to it once it exists.
        var normalized = Learning.CourseCatalogService.Normalize(language.Name);
        var waiting = await db.LanguageRequests.Where(r => r.LanguageId == null && r.NormalizedName == normalized).ToListAsync(ct);
        foreach (var r in waiting) r.LanguageId = language.Id;
        if (language.Status == LanguageStatus.Published)
        {
            var open = await db.LanguageRequests.Where(r => r.LanguageId == language.Id && r.Status != LanguageRequestStatus.Added).ToListAsync(ct);
            foreach (var r in open) r.Status = LanguageRequestStatus.Added;
        }
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Language", language.Id.ToString(), before,
            new { language.Name, language.IsoCode, Status = language.Status.ToString() }, ct);
        return language.Id;
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> OptionsAsync(CancellationToken ct = default) =>
        (await db.Languages.AsNoTracking().OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
        .Select(l => (l.Id, l.Name)).ToList();
}

// ===================================================================== language requests (FR-A20)

public interface ILanguageRequestAdminService
{
    Task<IReadOnlyList<LanguageRequestGroup>> ListAsync(LanguageRequestStatus? status, CancellationToken ct = default);
    Task<Result> SetStatusAsync(string normalizedName, LanguageRequestStatus status, CancellationToken ct = default);
}

internal sealed class LanguageRequestAdminService(IApplicationDbContext db, IAuditLogger audit) : ILanguageRequestAdminService
{
    public async Task<IReadOnlyList<LanguageRequestGroup>> ListAsync(LanguageRequestStatus? status, CancellationToken ct = default)
    {
        var rows = await db.LanguageRequests.AsNoTracking()
            .Select(r => new { r.NormalizedName, r.LanguageName, r.LanguageId, LanguageStatus = (LanguageStatus?)r.Language!.Status, r.Status, r.CreatedAt })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.NormalizedName)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.CreatedAt).First();
                var groupStatus = g.Any(r => r.Status == LanguageRequestStatus.Pending) ? LanguageRequestStatus.Pending : latest.Status;
                return new LanguageRequestGroup(g.Key, latest.LanguageName, g.Max(r => r.LanguageId), g.Max(r => r.LanguageStatus),
                    g.Count(), g.Count(r => r.Status == LanguageRequestStatus.Pending), groupStatus,
                    g.Min(r => r.CreatedAt), g.Max(r => r.CreatedAt));
            })
            .Where(g => status is null || g.Status == status)
            .OrderByDescending(g => g.PendingCount).ThenByDescending(g => g.Count)
            .ToList();
    }

    public async Task<Result> SetStatusAsync(string normalizedName, LanguageRequestStatus status, CancellationToken ct = default)
    {
        var requests = await db.LanguageRequests.Where(r => r.NormalizedName == normalizedName).ToListAsync(ct);
        if (requests.Count == 0) return Error.NotFound("Language request");
        foreach (var r in requests) r.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SetStatus", "LanguageRequest", normalizedName, null, new { Status = status.ToString(), requests.Count }, ct);
        return Result.Success();
    }
}

// ===================================================================== courses and roadmap builder (FR-A12, FR-A30 .. FR-A33)

public interface ICourseAdminService
{
    Task<IReadOnlyList<CourseListItem>> ListAsync(int? languageId, CancellationToken ct = default);
    Task<CourseInput?> GetAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(CourseInput input, CancellationToken ct = default);
    Task<CourseBuilderModel?> GetBuilderAsync(int id, CancellationToken ct = default);
    Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default);

    Task<ModuleInput?> GetModuleAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Title)>> ModuleOptionsAsync(int courseId, int? excludeId, CancellationToken ct = default);
    Task<Result<int>> SaveModuleAsync(ModuleInput input, CancellationToken ct = default);
    Task<Result<int>> DeleteModuleAsync(int id, CancellationToken ct = default);
    Task<Result> ReorderModulesAsync(ReorderInput input, CancellationToken ct = default);

    Task<LectureInput?> GetLectureAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveLectureAsync(LectureInput input, CancellationToken ct = default);
    Task<Result<int>> DeleteLectureAsync(int id, CancellationToken ct = default);
    /// <summary>Sets the order of lectures in a module; lectures dragged in from another module of the same course move here.</summary>
    Task<Result> ReorderLecturesAsync(ReorderInput input, CancellationToken ct = default);
}

internal sealed class CourseAdminService(
    IApplicationDbContext db,
    IAuditLogger audit,
    Learning.ILearnerCourseService learnerCourses,
    TimeProvider clock) : ICourseAdminService
{
    public async Task<IReadOnlyList<CourseListItem>> ListAsync(int? languageId, CancellationToken ct = default)
    {
        var q = db.Courses.AsNoTracking().AsQueryable();
        if (languageId is { } id) q = q.Where(c => c.LanguageId == id);

        var rows = await q.OrderBy(c => c.Language.Name).ThenBy(c => c.StartLevel).ThenBy(c => c.SortOrder)
            .Select(c => new
            {
                c.Id, c.LanguageId, LanguageName = c.Language.Name, c.Title, c.StartLevel, c.EndLevel, c.Status,
                Modules = c.Modules.Count(m => !m.IsDeleted),
                Lectures = c.Modules.Where(m => !m.IsDeleted).SelectMany(m => m.Lectures).Count(l => !l.IsDeleted),
                Minutes = c.Modules.Where(m => !m.IsDeleted).SelectMany(m => m.Lectures).Where(l => !l.IsDeleted).Sum(l => (int?)l.EstimatedMinutes) ?? 0
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var learners = await db.LearnerCourses.AsNoTracking()
            .Where(lc => ids.Contains(lc.CourseId) && (lc.Status == LearnerCourseStatus.Active || lc.Status == LearnerCourseStatus.Paused))
            .GroupBy(lc => lc.CourseId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        return rows.Select(r => new CourseListItem(r.Id, r.LanguageId, r.LanguageName, r.Title, r.StartLevel, r.EndLevel, r.Status,
            r.Modules, r.Lectures, Math.Round(r.Minutes / 60m, 1), learners.FirstOrDefault(l => l.Key == r.Id)?.Count ?? 0)).ToList();
    }

    public async Task<CourseInput?> GetAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : new CourseInput
        {
            Id = c.Id, LanguageId = c.LanguageId, Title = c.Title, Description = c.Description, CoverUrl = c.CoverUrl,
            StartLevel = c.StartLevel, EndLevel = c.EndLevel, SortOrder = c.SortOrder
        };
    }

    public async Task<Result<int>> SaveAsync(CourseInput input, CancellationToken ct = default)
    {
        if (input.EndLevel < input.StartLevel) return Error.Validation(nameof(input.EndLevel), "End level must be at or above the start level.");
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation(nameof(input.LanguageId), "Choose a language.");

        Course course;
        if (input.Id == 0)
        {
            course = new Course { Status = ContentStatus.Draft };
            db.Courses.Add(course);
        }
        else
        {
            course = await db.Courses.FirstOrDefaultAsync(c => c.Id == input.Id, ct) ?? throw new InvalidOperationException();
            if (course.LanguageId != input.LanguageId && await db.LearnerCourses.AnyAsync(lc => lc.CourseId == course.Id, ct))
                return Error.Validation(nameof(input.LanguageId), "Learners are enrolled, so the language can't change.");
        }

        course.LanguageId = input.LanguageId;
        course.Title = input.Title.Trim();
        course.Description = input.Description?.Trim();
        course.CoverUrl = string.IsNullOrWhiteSpace(input.CoverUrl) ? null : input.CoverUrl.Trim();
        course.StartLevel = input.StartLevel;
        course.EndLevel = input.EndLevel;
        course.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Course", course.Id.ToString(), null,
            new { course.Title, course.LanguageId, StartLevel = course.StartLevel.ToString(), EndLevel = course.EndLevel.ToString() }, ct);
        return course.Id;
    }

    public async Task<CourseBuilderModel?> GetBuilderAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Courses.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;

        var modules = await db.CourseModules.AsNoTracking().Where(m => m.CourseId == id).OrderBy(m => m.SortOrder).ToListAsync(ct);
        var moduleIds = modules.Select(m => m.Id).ToList();
        var lectures = await db.Lectures.AsNoTracking().Where(l => moduleIds.Contains(l.ModuleId)).OrderBy(l => l.SortOrder).ToListAsync(ct);
        var learners = await db.LearnerCourses.CountAsync(lc => lc.CourseId == id &&
            (lc.Status == LearnerCourseStatus.Active || lc.Status == LearnerCourseStatus.Paused), ct);

        return new CourseBuilderModel(c.Id, c.LanguageId, c.Language.Name, c.Language.Status, c.Title, c.Description,
            c.StartLevel, c.EndLevel, c.Status, learners,
            modules.Select(m => new BuilderModule(m.Id, m.Title, m.Objective, m.Level, m.MonthNo, m.SortOrder,
                modules.FirstOrDefault(p => p.Id == m.PrerequisiteModuleId)?.Title,
                lectures.Where(l => l.ModuleId == m.Id)
                    .Select(l => new BuilderLecture(l.Id, l.Title, l.Topic, l.Level, l.EstimatedMinutes, l.IsCore, l.Status, l.Source, l.SortOrder))
                    .ToList())).ToList());
    }

    public async Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default)
    {
        var course = await db.Courses.Include(c => c.Language).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return Error.NotFound("Course");

        if (status == ContentStatus.Published)
        {
            var lectureCount = await db.Lectures.CountAsync(l => l.Module.CourseId == id && !l.Module.IsDeleted && l.Status == ContentStatus.Published, ct);
            if (lectureCount == 0)
                return Error.Validation("Status", "Publish at least one lecture first (write it, send it for review, approve, publish).");
            course.PublishedAt ??= clock.GetUtcNow();
        }
        else if (course.Status == ContentStatus.Published && course.Language.Status == LanguageStatus.Published &&
                 !await db.Courses.AnyAsync(c => c.LanguageId == course.LanguageId && c.Id != id && c.Status == ContentStatus.Published, ct))
        {
            return Error.Validation("Status", $"This is the only published course of {course.Language.Name}. Set the language to \"Coming soon\" first.");
        }

        var old = course.Status;
        course.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SetStatus", "Course", id.ToString(), new { Status = old.ToString() }, new { Status = status.ToString() }, ct);
        return Result.Success();
    }

    // ------------------------------------------------------------- modules

    public async Task<ModuleInput?> GetModuleAsync(int id, CancellationToken ct = default)
    {
        var m = await db.CourseModules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return m is null ? null : new ModuleInput
        {
            Id = m.Id, CourseId = m.CourseId, Title = m.Title, Objective = m.Objective, Level = m.Level, MonthNo = m.MonthNo,
            PrerequisiteModuleId = m.PrerequisiteModuleId
        };
    }

    public async Task<IReadOnlyList<(int Id, string Title)>> ModuleOptionsAsync(int courseId, int? excludeId, CancellationToken ct = default) =>
        (await db.CourseModules.AsNoTracking().Where(m => m.CourseId == courseId && m.Id != excludeId)
            .OrderBy(m => m.SortOrder).Select(m => new { m.Id, m.Title }).ToListAsync(ct))
        .Select(m => (m.Id, m.Title)).ToList();

    public async Task<Result<int>> SaveModuleAsync(ModuleInput input, CancellationToken ct = default)
    {
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.CourseId, ct);
        if (course is null) return Error.NotFound("Course");
        if (input.Level < course.StartLevel || input.Level > course.EndLevel)
            return Error.Validation(nameof(input.Level), $"Pick a level between {course.StartLevel} and {course.EndLevel} for this course.");
        if (input.PrerequisiteModuleId is { } pre &&
            (pre == input.Id || !await db.CourseModules.AnyAsync(m => m.Id == pre && m.CourseId == input.CourseId, ct)))
            return Error.Validation(nameof(input.PrerequisiteModuleId), "Choose another module of this course.");

        CourseModule module;
        if (input.Id == 0)
        {
            var max = await db.CourseModules.Where(m => m.CourseId == input.CourseId).MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
            module = new CourseModule { CourseId = input.CourseId, SortOrder = max + 1 };
            db.CourseModules.Add(module);
        }
        else
        {
            module = await db.CourseModules.FirstOrDefaultAsync(m => m.Id == input.Id && m.CourseId == input.CourseId, ct)
                     ?? throw new InvalidOperationException();
        }

        module.Title = input.Title.Trim();
        module.Objective = input.Objective?.Trim();
        module.Level = input.Level;
        module.MonthNo = input.MonthNo;
        module.PrerequisiteModuleId = input.PrerequisiteModuleId;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Module", module.Id.ToString(), null,
            new { module.Title, module.CourseId, Level = module.Level.ToString(), module.MonthNo }, ct);
        if (input.Id != 0) await learnerCourses.SyncCourseAsync(input.CourseId, ct); // a level change can add or remove lectures
        return module.Id;
    }

    public async Task<Result<int>> DeleteModuleAsync(int id, CancellationToken ct = default)
    {
        var module = await db.CourseModules.Include(m => m.Lectures).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (module is null) return Error.NotFound("Module");

        // Soft delete: learners' roadmap rows for these lectures disappear from their plans; history stays in the database.
        foreach (var l in module.Lectures) l.IsDeleted = true;
        module.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "Module", id.ToString(), new { module.Title, Lectures = module.Lectures.Count }, null, ct);
        await learnerCourses.SyncCourseAsync(module.CourseId, ct);
        return module.CourseId;
    }

    public async Task<Result> ReorderModulesAsync(ReorderInput input, CancellationToken ct = default)
    {
        var modules = await db.CourseModules.Where(m => m.CourseId == input.ParentId).ToListAsync(ct);
        if (input.Ids.Length != modules.Count || input.Ids.Except(modules.Select(m => m.Id)).Any())
            return Error.Validation("Ids", "The module list changed. Reload the page and try again.");

        for (var i = 0; i < input.Ids.Length; i++)
            modules.First(m => m.Id == input.Ids[i]).SortOrder = i + 1;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Reorder", "Course", input.ParentId.ToString(), null, new { Modules = input.Ids }, ct);
        await learnerCourses.SyncCourseAsync(input.ParentId, ct);
        return Result.Success();
    }

    // ------------------------------------------------------------- lectures

    public async Task<LectureInput?> GetLectureAsync(int id, CancellationToken ct = default)
    {
        var l = await db.Lectures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new LectureInput
        {
            Id = l.Id, ModuleId = l.ModuleId, Title = l.Title, Topic = l.Topic, Objectives = l.Objectives, Level = l.Level,
            EstimatedMinutes = l.EstimatedMinutes, IsCore = l.IsCore
        };
    }

    public async Task<Result<int>> SaveLectureAsync(LectureInput input, CancellationToken ct = default)
    {
        var module = await db.CourseModules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == input.ModuleId, ct);
        if (module is null) return Error.NotFound("Module");

        Lecture lecture;
        if (input.Id == 0)
        {
            var max = await db.Lectures.Where(l => l.ModuleId == input.ModuleId).MaxAsync(l => (int?)l.SortOrder, ct) ?? 0;
            lecture = new Lecture { ModuleId = input.ModuleId, SortOrder = max + 1, Status = ContentStatus.Draft, Source = ContentSource.Manual };
            db.Lectures.Add(lecture);
        }
        else
        {
            lecture = await db.Lectures.FirstOrDefaultAsync(l => l.Id == input.Id, ct) ?? throw new InvalidOperationException();
        }

        var pacingChanged = input.Id != 0 && lecture.Status == ContentStatus.Published &&
                            (lecture.EstimatedMinutes != input.EstimatedMinutes || lecture.IsCore != input.IsCore);
        lecture.Title = input.Title.Trim();
        lecture.Topic = input.Topic?.Trim();
        lecture.Objectives = input.Objectives?.Trim();
        lecture.Level = input.Level;
        lecture.EstimatedMinutes = input.EstimatedMinutes;
        lecture.IsCore = input.IsCore;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Lecture", lecture.Id.ToString(), null,
            new { lecture.Title, lecture.ModuleId, lecture.EstimatedMinutes, lecture.IsCore }, ct);
        if (pacingChanged) await learnerCourses.SyncCourseAsync(module.CourseId, ct);
        return module.CourseId;
    }

    public async Task<Result<int>> DeleteLectureAsync(int id, CancellationToken ct = default)
    {
        var lecture = await db.Lectures.Include(l => l.Module).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lecture is null) return Error.NotFound("Lecture");
        lecture.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "Lecture", id.ToString(), new { lecture.Title }, null, ct);
        await learnerCourses.SyncCourseAsync(lecture.Module.CourseId, ct);
        return lecture.Module.CourseId;
    }

    public async Task<Result> ReorderLecturesAsync(ReorderInput input, CancellationToken ct = default)
    {
        var module = await db.CourseModules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == input.ParentId, ct);
        if (module is null) return Error.NotFound("Module");

        var lectures = await db.Lectures.Where(l => input.Ids.Contains(l.Id) && l.Module.CourseId == module.CourseId).ToListAsync(ct);
        if (lectures.Count != input.Ids.Length)
            return Error.Validation("Ids", "The lecture list changed. Reload the page and try again.");

        for (var i = 0; i < input.Ids.Length; i++)
        {
            var lecture = lectures.First(l => l.Id == input.Ids[i]);
            lecture.ModuleId = input.ParentId;
            lecture.SortOrder = i + 1;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Reorder", "Module", input.ParentId.ToString(), null, new { Lectures = input.Ids }, ct);
        await learnerCourses.SyncCourseAsync(module.CourseId, ct);
        return Result.Success();
    }
}
