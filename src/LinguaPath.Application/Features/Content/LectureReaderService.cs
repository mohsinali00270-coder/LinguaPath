using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Content;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Content;

public interface ILectureReaderService
{
    Task<Result<LectureDto>> GetAsync(Guid userId, int lectureId, int? learnerCourseId, CancellationToken ct = default);
    Task<Result> AddProgressAsync(Guid userId, int lectureId, LectureProgressRequest request, CancellationToken ct = default);
    Task<Result<CompleteLectureResponse>> CompleteAsync(Guid userId, int lectureId, CompleteLectureRequest request, CancellationToken ct = default);
    Task<Result> SetBookmarkAsync(Guid userId, int lectureId, bool bookmarked, CancellationToken ct = default);
    Task<IReadOnlyList<BookmarkDto>> BookmarksAsync(Guid userId, CancellationToken ct = default);
    Task<Result> SaveNoteAsync(Guid userId, int lectureId, SaveNoteRequest request, CancellationToken ct = default);
    Task<Result> RateAsync(Guid userId, int lectureId, RateLectureRequest request, CancellationToken ct = default);
    Task<Result> ReportAsync(Guid userId, ReportEntityType type, int entityId, ReportContentRequest request, CancellationToken ct = default);
}

/// <summary>Lecture reader, progress and completion (FR-L31 .. FR-L37).</summary>
internal sealed class LectureReaderService(
    IApplicationDbContext db,
    ISettingsService settings,
    IPostFeedService posts,
    Progress.IProgressService progress,
    TimeProvider clock) : ILectureReaderService
{
    private const int MaxSecondsPerReport = 3600;

    public async Task<Result<LectureDto>> GetAsync(Guid userId, int lectureId, int? learnerCourseId, CancellationToken ct = default)
    {
        var item = await FindItemAsync(userId, lectureId, learnerCourseId, ct);
        if (item is null) return Error.NotFound("Lecture") with { Message = "This lecture isn't in your roadmap." };
        if (item.State == RoadmapItemState.Locked)
            return Error.Forbidden("lecture_locked", "Finish the lectures before this one to unlock it.");

        var lecture = await db.Lectures.AsNoTracking()
            .Include(l => l.Module).ThenInclude(m => m.Course).ThenInclude(c => c.Language)
            .FirstOrDefaultAsync(l => l.Id == lectureId && l.Status == ContentStatus.Published, ct);
        if (lecture is null) return Error.NotFound("Lecture") with { Message = "This lecture is not available right now." };

        // Opening an available lecture marks it as started.
        if (item.State == RoadmapItemState.Available)
        {
            item.State = RoadmapItemState.InProgress;
            item.StartedAt ??= clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        var examples = await db.LectureExamples.AsNoTracking().Where(e => e.LectureId == lectureId).OrderBy(e => e.SortOrder)
            .Select(e => new LectureExampleDto(e.Id, e.TargetText, e.Translation, e.Note, e.AudioUrl)).ToListAsync(ct);
        var vocabulary = await db.LectureVocabulary.AsNoTracking().Where(v => v.LectureId == lectureId).OrderBy(v => v.SortOrder)
            .Select(v => new LectureVocabularyDto(v.Id, v.Word, v.Meaning, v.Pronunciation, v.PartOfSpeech, v.AudioUrl)).ToListAsync(ct);
        var links = await db.LectureLinks.AsNoTracking().Where(l => l.LectureId == lectureId).OrderBy(l => l.SortOrder)
            .Select(l => new LectureLinkDto(l.Id, l.Title, l.Url, l.LinkType)).ToListAsync(ct);

        var bookmarked = await db.LectureBookmarks.AnyAsync(b => b.UserId == userId && b.LectureId == lectureId, ct);
        var note = await db.LectureNotes.AsNoTracking().Where(n => n.UserId == userId && n.LectureId == lectureId)
            .Select(n => n.NoteText).FirstOrDefaultAsync(ct);
        var ratings = await db.LectureRatings.AsNoTracking().Where(r => r.LectureId == lectureId)
            .Select(r => new { r.UserId, r.Stars }).ToListAsync(ct);

        var next = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.LearnerCourseId == item.LearnerCourseId && i.SortOrder > item.SortOrder && i.State != RoadmapItemState.Completed)
            .OrderBy(i => i.SortOrder).Select(i => (int?)i.LectureId).FirstOrDefaultAsync(ct);

        var language = lecture.Module.Course.Language;
        return new LectureDto(
            lecture.Id, item.LearnerCourseId, item.Id, lecture.Title, lecture.Topic, lecture.Objectives, lecture.Level,
            lecture.EstimatedMinutes, lecture.IsCore, lecture.CoverUrl, lecture.BodyHtml ?? string.Empty, lecture.Module.Title,
            language.Name, language.IsoCode, language.SttLocale, language.Direction,
            examples, vocabulary, links, await posts.RelatedToLectureAsync(userId, lectureId, ct),
            item.State, item.TimeSpentSec, await MinimumSecondsAsync(lecture.EstimatedMinutes, ct),
            bookmarked, note,
            ratings.FirstOrDefault(r => r.UserId == userId)?.Stars,
            ratings.Count == 0 ? null : Math.Round(ratings.Average(r => r.Stars), 1), ratings.Count,
            next, lecture.Version, language.Id);
    }

    public async Task<Result> AddProgressAsync(Guid userId, int lectureId, LectureProgressRequest request, CancellationToken ct = default)
    {
        var item = await FindItemAsync(userId, lectureId, request.LearnerCourseId, ct);
        if (item is null) return Error.NotFound("Lecture");
        if (item.State == RoadmapItemState.Locked) return Error.Forbidden("lecture_locked", "This lecture is locked.");

        item.TimeSpentSec += Math.Clamp(request.Seconds, 0, MaxSecondsPerReport);
        if (item.State == RoadmapItemState.Available)
        {
            item.State = RoadmapItemState.InProgress;
            item.StartedAt ??= clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<CompleteLectureResponse>> CompleteAsync(Guid userId, int lectureId, CompleteLectureRequest request,
        CancellationToken ct = default)
    {
        var item = await FindItemAsync(userId, lectureId, request.LearnerCourseId, ct);
        if (item is null) return Error.NotFound("Lecture");
        if (item.State == RoadmapItemState.Locked) return Error.Forbidden("lecture_locked", "This lecture is locked.");

        var lecture = await db.Lectures.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lectureId && l.Status == ContentStatus.Published, ct);
        if (lecture is null) return Error.NotFound("Lecture");

        Shared.Contracts.Progress.PointsEarnedDto? earned = null;
        if (item.State != RoadmapItemState.Completed)
        {
            item.TimeSpentSec += Math.Clamp(request.Seconds, 0, MaxSecondsPerReport);

            // FR-S05 / Learning.MinReadingTimePercent: a lecture only counts after a minimum reading time.
            var minimum = await MinimumSecondsAsync(lecture.EstimatedMinutes, ct);
            if (item.TimeSpentSec < minimum)
            {
                await db.SaveChangesAsync(ct);
                var left = (int)Math.Ceiling((minimum - item.TimeSpentSec) / 60.0);
                return Error.Validation("Seconds", $"Take a little more time with this lecture — about {left} more minute(s).")
                    with { Code = "too_quick" };
            }

            item.State = RoadmapItemState.Completed;
            item.CompletedAt = clock.GetUtcNow();
            item.StartedAt ??= item.CompletedAt;

            var all = await db.LearnerRoadmapItems.Where(i => i.LearnerCourseId == item.LearnerCourseId).ToListAsync(ct);
            RoadmapRules.ApplyUnlock(all, await settings.GetBoolAsync(SettingKeys.LecturesUnlockSequentially, true, ct));
            item.LearnerCourse.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            // Phase 7: points (once per lecture, BR-21 bonus), streak day (BR-09), badges, certificate check (BR-06).
            earned = await progress.OnLectureCompletedAsync(userId, item.Id, ct);
        }

        var items = await db.LearnerRoadmapItems.AsNoTracking().Where(i => i.LearnerCourseId == item.LearnerCourseId)
            .OrderBy(i => i.SortOrder)
            .Select(i => new { i.LectureId, i.State, i.SortOrder, i.Lecture.Title })
            .ToListAsync(ct);
        var done = items.Count(i => i.State == RoadmapItemState.Completed);
        var next = items.FirstOrDefault(i => i.SortOrder > item.SortOrder && i.State != RoadmapItemState.Completed)
                   ?? items.FirstOrDefault(i => i.State != RoadmapItemState.Completed);

        return new CompleteLectureResponse(next?.LectureId, next?.Title,
            items.Count == 0 ? 0 : (int)Math.Round(100.0 * done / items.Count), done, items.Count, done == items.Count,
            earned?.Points ?? 0, earned?.Streak ?? 0, earned?.NewBadges.Select(b => $"{b.Icon} {b.Title}").ToList());
    }

    public async Task<Result> SetBookmarkAsync(Guid userId, int lectureId, bool bookmarked, CancellationToken ct = default)
    {
        if (await FindItemAsync(userId, lectureId, null, ct) is null) return Error.NotFound("Lecture");
        var existing = await db.LectureBookmarks.FirstOrDefaultAsync(b => b.UserId == userId && b.LectureId == lectureId, ct);
        if (bookmarked && existing is null)
            db.LectureBookmarks.Add(new LectureBookmark { UserId = userId, LectureId = lectureId, CreatedAt = clock.GetUtcNow() });
        else if (!bookmarked && existing is not null)
            db.LectureBookmarks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<BookmarkDto>> BookmarksAsync(Guid userId, CancellationToken ct = default) =>
        await db.LectureBookmarks.AsNoTracking()
            .Where(b => b.UserId == userId && b.Lecture.Status == ContentStatus.Published)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new BookmarkDto(b.LectureId, b.Lecture.Title, b.Lecture.Topic, b.Lecture.Module.Course.Language.Name,
                b.Lecture.Module.Course.Title, b.CreatedAt))
            .ToListAsync(ct);

    public async Task<Result> SaveNoteAsync(Guid userId, int lectureId, SaveNoteRequest request, CancellationToken ct = default)
    {
        if (await FindItemAsync(userId, lectureId, null, ct) is null) return Error.NotFound("Lecture");
        var note = await db.LectureNotes.FirstOrDefaultAsync(n => n.UserId == userId && n.LectureId == lectureId, ct);
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (note is not null) db.LectureNotes.Remove(note);
        }
        else if (note is null)
        {
            db.LectureNotes.Add(new LectureNote { UserId = userId, LectureId = lectureId, NoteText = text, UpdatedAt = clock.GetUtcNow() });
        }
        else
        {
            note.NoteText = text;
            note.UpdatedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RateAsync(Guid userId, int lectureId, RateLectureRequest request, CancellationToken ct = default)
    {
        if (await FindItemAsync(userId, lectureId, null, ct) is null) return Error.NotFound("Lecture");
        var rating = await db.LectureRatings.FirstOrDefaultAsync(r => r.UserId == userId && r.LectureId == lectureId, ct);
        if (rating is null)
        {
            rating = new LectureRating { UserId = userId, LectureId = lectureId };
            db.LectureRatings.Add(rating);
        }
        rating.Stars = request.Stars;
        rating.Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        rating.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReportAsync(Guid userId, ReportEntityType type, int entityId, ReportContentRequest request, CancellationToken ct = default)
    {
        var exists = type switch
        {
            ReportEntityType.Lecture => await FindItemAsync(userId, entityId, null, ct) is not null,
            ReportEntityType.Post => await db.Posts.AnyAsync(p => p.Id == entityId && p.Status == ContentStatus.Published, ct),
            _ => false
        };
        if (!exists) return Error.NotFound(type.ToString());

        db.ContentReports.Add(new ContentReport
        {
            UserId = userId, EntityType = type, EntityId = entityId, Message = request.Message.Trim(), CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>The learner's roadmap row for a lecture, in an active or paused course (or the given one).</summary>
    private Task<LearnerRoadmapItem?> FindItemAsync(Guid userId, int lectureId, int? learnerCourseId, CancellationToken ct) =>
        db.LearnerRoadmapItems.Include(i => i.LearnerCourse)
            .Where(i => i.LectureId == lectureId && i.LearnerCourse.UserId == userId &&
                        (learnerCourseId == null || i.LearnerCourseId == learnerCourseId) &&
                        (i.LearnerCourse.Status == LearnerCourseStatus.Active || i.LearnerCourse.Status == LearnerCourseStatus.Paused ||
                         i.LearnerCourse.Status == LearnerCourseStatus.Completed))
            .OrderByDescending(i => i.LearnerCourseId)
            .FirstOrDefaultAsync(ct);

    private async Task<int> MinimumSecondsAsync(int estimatedMinutes, CancellationToken ct)
    {
        var percent = await settings.GetIntAsync(SettingKeys.MinReadingTimePercent, 50, ct);
        return (int)Math.Round(estimatedMinutes * 60 * Math.Clamp(percent, 0, 100) / 100.0);
    }
}
