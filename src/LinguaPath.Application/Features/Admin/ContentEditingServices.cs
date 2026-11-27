using System.Text.Json;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Admin;

// ===================================================================== review workflow (FR-A70 .. FR-A72)

public interface IReviewService
{
    Task<Result> SubmitAsync(ReviewEntityType type, int entityId, CancellationToken ct = default);
    Task<Result> ApproveAsync(int reviewId, string? comment, CancellationToken ct = default);
    Task<Result> RejectAsync(int reviewId, string comment, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewQueueItem>> PendingAsync(ReviewEntityType? type, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewHistoryItem>> HistoryAsync(ReviewEntityType type, int entityId, CancellationToken ct = default);
    Task<int> PendingCountAsync(CancellationToken ct = default);
}

/// <summary>
/// Draft → In review → Approved → Published. Submitting creates a pending review item; a Reviewer approves it,
/// or rejects it with a comment (back to Draft). Reviewers cannot approve their own submissions (Super Admins can).
/// </summary>
internal sealed class ReviewService(IApplicationDbContext db, ICurrentUser currentUser, IAuditLogger audit, TimeProvider clock) : IReviewService
{
    public async Task<Result> SubmitAsync(ReviewEntityType type, int entityId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(type, entityId, ct);
        if (status is null) return Error.NotFound(type.ToString());
        if (status != ContentStatus.Draft) return Error.Validation("Status", $"Only drafts can be sent for review (this one is {status}).");
        if (type == ReviewEntityType.Lecture && !await db.Lectures.AnyAsync(l => l.Id == entityId && l.BodyHtml != null && l.BodyHtml != "", ct))
            return Error.Validation("BodyHtml", "Write the lesson text before sending it for review.");

        await CloseOpenReviewsAsync(type, entityId, ct);
        db.ReviewItems.Add(new ReviewItem
        {
            EntityType = type, EntityId = entityId, Status = ReviewStatus.Pending,
            SubmittedBy = currentUser.UserId, SubmittedByEmail = currentUser.Email, SubmittedAt = clock.GetUtcNow()
        });
        await SetStatusAsync(type, entityId, ContentStatus.InReview, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SubmitForReview", type.ToString(), entityId.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> ApproveAsync(int reviewId, string? comment, CancellationToken ct = default)
    {
        var review = await db.ReviewItems.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null || review.Status != ReviewStatus.Pending) return Error.NotFound("Pending review");
        if (review.SubmittedBy == currentUser.UserId && !currentUser.IsInRole(Roles.SuperAdmin))
            return Error.Forbidden(ErrorCodes.Forbidden, "Another reviewer has to approve your own submission.");
        if (await GetStatusAsync(review.EntityType, review.EntityId, ct) != ContentStatus.InReview)
            return Error.Validation("Status", "This item is no longer waiting for review.");

        Close(review, ReviewStatus.Approved, comment);
        await SetStatusAsync(review.EntityType, review.EntityId, ContentStatus.Approved, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Approve", review.EntityType.ToString(), review.EntityId.ToString(), null, new { comment }, ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(int reviewId, string comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comment)) return Error.Validation("Comment", "Say what needs to change.");
        var review = await db.ReviewItems.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null || review.Status != ReviewStatus.Pending) return Error.NotFound("Pending review");

        Close(review, ReviewStatus.Rejected, comment);
        await SetStatusAsync(review.EntityType, review.EntityId, ContentStatus.Draft, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Reject", review.EntityType.ToString(), review.EntityId.ToString(), null, new { comment }, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<ReviewQueueItem>> PendingAsync(ReviewEntityType? type, CancellationToken ct = default)
    {
        var pending = await db.ReviewItems.AsNoTracking()
            .Where(r => r.Status == ReviewStatus.Pending && (type == null || r.EntityType == type))
            .OrderBy(r => r.SubmittedAt).ToListAsync(ct);

        var lectureIds = pending.Where(r => r.EntityType == ReviewEntityType.Lecture).Select(r => r.EntityId).ToList();
        var postIds = pending.Where(r => r.EntityType == ReviewEntityType.Post).Select(r => r.EntityId).ToList();
        var lectures = await db.Lectures.AsNoTracking().Where(l => lectureIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title, l.Source, Context = l.Module.Course.Language.Name + " · " + l.Module.Course.Title + " · " + l.Module.Title })
            .ToListAsync(ct);
        var posts = await db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title, p.Source, Context = p.Language.Name + " · " + p.PostType })
            .ToListAsync(ct);
        var questionIds = pending.Where(r => r.EntityType == ReviewEntityType.Question).Select(r => r.EntityId).ToList();
        var questions = await db.Questions.AsNoTracking().Where(q => questionIds.Contains(q.Id))
            .Select(q => new { q.Id, Title = q.Stem, q.Source, q.Type, q.Level, Language = q.Language.Name, Lecture = q.Lecture != null ? q.Lecture.Title : null })
            .ToListAsync(ct);
        var practiceIds = pending.Where(r => r.EntityType == ReviewEntityType.PracticeItem).Select(r => r.EntityId).ToList();
        var practice = await db.PracticeItems.AsNoTracking().Where(p => practiceIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title, p.Source, Context = p.Language.Name + " · " + p.Type + " · " + p.Level })
            .ToListAsync(ct);

        return pending.Select(r =>
        {
            if (r.EntityType == ReviewEntityType.Lecture)
            {
                var l = lectures.FirstOrDefault(x => x.Id == r.EntityId);
                return l is null ? null : new ReviewQueueItem(r.Id, r.EntityType, r.EntityId, l.Title, l.Context, l.Source, r.SubmittedByEmail, r.SubmittedAt, null);
            }
            if (r.EntityType == ReviewEntityType.Question)
            {
                var q = questions.FirstOrDefault(x => x.Id == r.EntityId);
                return q is null ? null : new ReviewQueueItem(r.Id, r.EntityType, r.EntityId, q.Title.Length > 120 ? q.Title[..120] + "…" : q.Title,
                    $"{q.Language} · {q.Level} · {q.Type}{(q.Lecture is null ? "" : " · " + q.Lecture)}", q.Source, r.SubmittedByEmail, r.SubmittedAt, null);
            }
            if (r.EntityType == ReviewEntityType.PracticeItem)
            {
                var x = practice.FirstOrDefault(i => i.Id == r.EntityId);
                return x is null ? null : new ReviewQueueItem(r.Id, r.EntityType, r.EntityId, x.Title, x.Context, x.Source, r.SubmittedByEmail, r.SubmittedAt, null);
            }
            var p = posts.FirstOrDefault(x => x.Id == r.EntityId);
            return p is null ? null : new ReviewQueueItem(r.Id, r.EntityType, r.EntityId, p.Title, p.Context, p.Source, r.SubmittedByEmail, r.SubmittedAt, null);
        }).Where(x => x is not null).Select(x => x!).ToList();
    }

    public async Task<IReadOnlyList<ReviewHistoryItem>> HistoryAsync(ReviewEntityType type, int entityId, CancellationToken ct = default) =>
        await db.ReviewItems.AsNoTracking().Where(r => r.EntityType == type && r.EntityId == entityId)
            .OrderByDescending(r => r.Id)
            .Select(r => new ReviewHistoryItem(r.Status, r.SubmittedByEmail, r.SubmittedAt, r.ReviewerEmail, r.ReviewedAt, r.Comment))
            .ToListAsync(ct);

    public Task<int> PendingCountAsync(CancellationToken ct = default) => db.ReviewItems.CountAsync(r => r.Status == ReviewStatus.Pending, ct);

    private void Close(ReviewItem review, ReviewStatus status, string? comment)
    {
        review.Status = status;
        review.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        review.ReviewerId = currentUser.UserId;
        review.ReviewerEmail = currentUser.Email;
        review.ReviewedAt = clock.GetUtcNow();
    }

    private async Task CloseOpenReviewsAsync(ReviewEntityType type, int entityId, CancellationToken ct)
    {
        var open = await db.ReviewItems.Where(r => r.EntityType == type && r.EntityId == entityId && r.Status == ReviewStatus.Pending).ToListAsync(ct);
        foreach (var r in open) Close(r, ReviewStatus.Rejected, "Superseded by a new submission.");
    }

    internal async Task<ContentStatus?> GetStatusAsync(ReviewEntityType type, int id, CancellationToken ct) => type switch
    {
        ReviewEntityType.Lecture => await db.Lectures.Where(l => l.Id == id).Select(l => (ContentStatus?)l.Status).FirstOrDefaultAsync(ct),
        ReviewEntityType.Post => await db.Posts.Where(p => p.Id == id).Select(p => (ContentStatus?)p.Status).FirstOrDefaultAsync(ct),
        ReviewEntityType.Question => await db.Questions.Where(q => q.Id == id && !q.IsDeleted).Select(q => (ContentStatus?)q.Status).FirstOrDefaultAsync(ct),
        ReviewEntityType.PracticeItem => await db.PracticeItems.Where(p => p.Id == id).Select(p => (ContentStatus?)p.Status).FirstOrDefaultAsync(ct),
        _ => null
    };

    private async Task SetStatusAsync(ReviewEntityType type, int id, ContentStatus status, CancellationToken ct)
    {
        if (type == ReviewEntityType.Lecture)
        {
            var lecture = await db.Lectures.FirstAsync(l => l.Id == id, ct);
            lecture.Status = status;
        }
        else if (type == ReviewEntityType.Question)
        {
            var question = await db.Questions.FirstAsync(q => q.Id == id, ct);
            question.Status = status;
        }
        else if (type == ReviewEntityType.PracticeItem)
        {
            var item = await db.PracticeItems.FirstAsync(p => p.Id == id, ct);
            item.Status = status;
        }
        else
        {
            var post = await db.Posts.FirstAsync(p => p.Id == id, ct);
            post.Status = status;
        }
    }
}

// ===================================================================== lecture content (FR-A40, FR-A41, FR-A46, FR-A47)

public interface ILectureContentAdminService
{
    Task<PagedResult<LectureListItem>> ListAsync(LectureFilter filter, CancellationToken ct = default);
    Task<LectureEditorModel?> GetEditorAsync(int id, CancellationToken ct = default);
    Task<Result> SaveAsync(LectureContentInput input, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> UnpublishAsync(int id, CancellationToken ct = default);
    Task<Result> ArchiveAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<LectureVersionItem>> VersionsAsync(int lectureId, CancellationToken ct = default);
    Task<LectureVersionView?> GetVersionAsync(int versionId, CancellationToken ct = default);
    Task<Result> RestoreAsync(int versionId, CancellationToken ct = default);
}

internal sealed class LectureContentAdminService(
    IApplicationDbContext db,
    IHtmlSanitizerService sanitizer,
    ILearnerCourseService learnerCourses,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider clock) : ILectureContentAdminService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Extras(List<ExampleInput> Examples, List<VocabularyInput> Vocabulary, List<LinkInput> Links);

    public async Task<PagedResult<LectureListItem>> ListAsync(LectureFilter filter, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 10, 100);
        var q = db.Lectures.AsNoTracking().Where(l => !l.Module.IsDeleted);
        if (filter.CourseId is { } courseId) q = q.Where(l => l.Module.CourseId == courseId);
        if (filter.Status is { } status) q = q.Where(l => l.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(l => l.Title.Contains(s) || (l.Topic != null && l.Topic.Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(l => l.Module.Course.Language.Name).ThenBy(l => l.Module.Course.Title)
            .ThenBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder)
            .Skip((page - 1) * size).Take(size)
            .Select(l => new LectureListItem(l.Id, l.Title, l.Topic, l.Module.CourseId, l.Module.Course.Title, l.Module.Course.Language.Name,
                l.Module.Title, l.Level, l.Status, l.Source, l.Version, l.BodyHtml != null && l.BodyHtml != "", l.UpdatedAt ?? l.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<LectureListItem> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<LectureEditorModel?> GetEditorAsync(int id, CancellationToken ct = default)
    {
        var l = await db.Lectures.AsNoTracking()
            .Include(x => x.Module).ThenInclude(m => m.Course).ThenInclude(c => c.Language)
            .Include(x => x.Examples).Include(x => x.Vocabulary).Include(x => x.Links)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return null;

        var lastReview = await db.ReviewItems.AsNoTracking()
            .Where(r => r.EntityType == ReviewEntityType.Lecture && r.EntityId == id && r.Status != ReviewStatus.Pending)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

        var input = new LectureContentInput
        {
            Id = l.Id, Title = l.Title, Topic = l.Topic, Objectives = l.Objectives, Level = l.Level, EstimatedMinutes = l.EstimatedMinutes,
            IsCore = l.IsCore, CoverUrl = l.CoverUrl, BodyHtml = l.BodyHtml,
            Examples = l.Examples.OrderBy(e => e.SortOrder).Select(e => new ExampleInput { TargetText = e.TargetText, Translation = e.Translation, Note = e.Note }).ToList(),
            Vocabulary = l.Vocabulary.OrderBy(v => v.SortOrder).Select(v => new VocabularyInput { Word = v.Word, Meaning = v.Meaning, Pronunciation = v.Pronunciation, PartOfSpeech = v.PartOfSpeech }).ToList(),
            Links = l.Links.OrderBy(k => k.SortOrder).Select(k => new LinkInput { Title = k.Title, Url = k.Url, LinkType = k.LinkType }).ToList()
        };
        var lang = l.Module.Course.Language;
        return new LectureEditorModel(input, l.ModuleId, l.Module.Title, l.Module.CourseId, l.Module.Course.Title, lang.Name, lang.IsoCode,
            lang.Direction, l.Status, l.Source, l.Version, lastReview?.Comment, lastReview?.Status);
    }

    public async Task<Result> SaveAsync(LectureContentInput input, CancellationToken ct = default)
    {
        var lecture = await db.Lectures.Include(l => l.Examples).Include(l => l.Vocabulary).Include(l => l.Links).Include(l => l.Module)
            .FirstOrDefaultAsync(l => l.Id == input.Id, ct);
        if (lecture is null) return Error.NotFound("Lecture");
        if (lecture.Status == ContentStatus.Archived) return Error.Validation("Status", "Archived lectures can't be edited.");

        // Keep the state before the very first editor save, so it can be restored.
        if (!await db.LectureVersions.AnyAsync(v => v.LectureId == lecture.Id, ct))
            db.LectureVersions.Add(Snapshot(lecture, "Before first edit"));

        var pacingChanged = lecture.EstimatedMinutes != input.EstimatedMinutes || lecture.IsCore != input.IsCore;

        lecture.Title = input.Title.Trim();
        lecture.Topic = input.Topic?.Trim();
        lecture.Objectives = input.Objectives?.Trim();
        lecture.Level = input.Level;
        lecture.EstimatedMinutes = input.EstimatedMinutes;
        lecture.IsCore = input.IsCore;
        lecture.CoverUrl = string.IsNullOrWhiteSpace(input.CoverUrl) ? null : input.CoverUrl.Trim();
        lecture.BodyHtml = sanitizer.Sanitize(input.BodyHtml);

        db.LectureExamples.RemoveRange(lecture.Examples);
        db.LectureVocabulary.RemoveRange(lecture.Vocabulary);
        db.LectureLinks.RemoveRange(lecture.Links);
        lecture.Examples = input.Examples.Where(e => !string.IsNullOrWhiteSpace(e.TargetText))
            .Select((e, i) => new LectureExample { TargetText = e.TargetText.Trim(), Translation = e.Translation?.Trim(), Note = e.Note?.Trim(), SortOrder = i + 1 })
            .ToList();
        lecture.Vocabulary = input.Vocabulary.Where(v => !string.IsNullOrWhiteSpace(v.Word))
            .Select((v, i) => new LectureVocabulary { Word = v.Word.Trim(), Meaning = v.Meaning.Trim(), Pronunciation = v.Pronunciation?.Trim(), PartOfSpeech = v.PartOfSpeech?.Trim(), SortOrder = i + 1 })
            .ToList();
        lecture.Links = input.Links.Where(k => !string.IsNullOrWhiteSpace(k.Url))
            .Select((k, i) => new LectureLink { Title = k.Title.Trim(), Url = k.Url.Trim(), LinkType = k.LinkType, SortOrder = i + 1 })
            .ToList();

        // An approved lecture that changes needs a new review. Published lectures stay live; every change is versioned.
        if (lecture.Status == ContentStatus.Approved) lecture.Status = ContentStatus.Draft;
        lecture.Version++;
        db.LectureVersions.Add(Snapshot(lecture, input.ChangeNote));
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("UpdateContent", "Lecture", lecture.Id.ToString(), null,
            new { lecture.Title, lecture.Version, input.ChangeNote, Status = lecture.Status.ToString() }, ct);
        if (pacingChanged && lecture.Status == ContentStatus.Published)
            await learnerCourses.SyncCourseAsync(lecture.Module.CourseId, ct);
        return Result.Success();
    }

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var lecture = await db.Lectures.Include(l => l.Module).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lecture is null) return Error.NotFound("Lecture");
        if (lecture.Status != ContentStatus.Approved)
            return Error.Validation("Status", "Only approved lectures can be published. Send it for review first.");

        lecture.Status = ContentStatus.Published;
        lecture.PublishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "Lecture", id.ToString(), ct: ct);
        await learnerCourses.SyncCourseAsync(lecture.Module.CourseId, ct); // reaches enrolled learners' roadmaps
        return Result.Success();
    }

    public Task<Result> UnpublishAsync(int id, CancellationToken ct = default) => TakeDownAsync(id, ContentStatus.Draft, "Unpublish", ct);

    public Task<Result> ArchiveAsync(int id, CancellationToken ct = default) => TakeDownAsync(id, ContentStatus.Archived, "Archive", ct);

    private async Task<Result> TakeDownAsync(int id, ContentStatus status, string action, CancellationToken ct)
    {
        var lecture = await db.Lectures.Include(l => l.Module).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lecture is null) return Error.NotFound("Lecture");
        var wasLive = lecture.Status == ContentStatus.Published;
        if (wasLive && await IsLastPublishedLectureOfLiveCourseAsync(lecture, ct))
            return Error.Validation("Status", "This is the only published lecture of a published course. Unpublish the course first.");

        lecture.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(action, "Lecture", id.ToString(), ct: ct);
        if (wasLive) await learnerCourses.SyncCourseAsync(lecture.Module.CourseId, ct);
        return Result.Success();
    }

    private async Task<bool> IsLastPublishedLectureOfLiveCourseAsync(Lecture lecture, CancellationToken ct) =>
        await db.Courses.AnyAsync(c => c.Id == lecture.Module.CourseId && c.Status == ContentStatus.Published, ct) &&
        !await db.Lectures.AnyAsync(l => l.Id != lecture.Id && l.Module.CourseId == lecture.Module.CourseId && !l.Module.IsDeleted &&
                                         l.Status == ContentStatus.Published, ct);

    public async Task<IReadOnlyList<LectureVersionItem>> VersionsAsync(int lectureId, CancellationToken ct = default) =>
        await db.LectureVersions.AsNoTracking().Where(v => v.LectureId == lectureId).OrderByDescending(v => v.Version)
            .Select(v => new LectureVersionItem(v.Id, v.Version, v.Title, v.Note, v.ChangedByEmail, v.ChangedAt, v.BodyHtml == null ? 0 : v.BodyHtml.Length))
            .ToListAsync(ct);

    public async Task<LectureVersionView?> GetVersionAsync(int versionId, CancellationToken ct = default)
    {
        var v = await db.LectureVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;
        var current = await db.Lectures.AsNoTracking().Where(l => l.Id == v.LectureId)
            .Select(l => new { l.Title, l.BodyHtml, l.Version, l.Module.Course.Language.Direction }).FirstOrDefaultAsync(ct);
        if (current is null) return null;
        return new LectureVersionView(v.Id, v.LectureId, v.Version, v.Title, v.BodyHtml, v.Note, v.ChangedByEmail, v.ChangedAt,
            current.Title, current.BodyHtml, current.Version, current.Direction);
    }

    public async Task<Result> RestoreAsync(int versionId, CancellationToken ct = default)
    {
        var v = await db.LectureVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return Error.NotFound("Version");
        var editor = await GetEditorAsync(v.LectureId, ct);
        if (editor is null) return Error.NotFound("Lecture");

        var extras = string.IsNullOrEmpty(v.ExtrasJson) ? null : JsonSerializer.Deserialize<Extras>(v.ExtrasJson, Json);
        var input = editor.Input;
        input.Title = v.Title;
        input.BodyHtml = v.BodyHtml;
        input.Examples = extras?.Examples ?? [];
        input.Vocabulary = extras?.Vocabulary ?? [];
        input.Links = extras?.Links ?? [];
        input.ChangeNote = $"Restored version {v.Version}";
        return await SaveAsync(input, ct);
    }

    private LectureVersion Snapshot(Lecture l, string? note) => new()
    {
        LectureId = l.Id,
        Version = l.Version,
        Title = l.Title,
        BodyHtml = l.BodyHtml,
        ExtrasJson = JsonSerializer.Serialize(new Extras(
            l.Examples.OrderBy(e => e.SortOrder).Select(e => new ExampleInput { TargetText = e.TargetText, Translation = e.Translation, Note = e.Note }).ToList(),
            l.Vocabulary.OrderBy(x => x.SortOrder).Select(x => new VocabularyInput { Word = x.Word, Meaning = x.Meaning, Pronunciation = x.Pronunciation, PartOfSpeech = x.PartOfSpeech }).ToList(),
            l.Links.OrderBy(k => k.SortOrder).Select(k => new LinkInput { Title = k.Title, Url = k.Url, LinkType = k.LinkType }).ToList()), Json),
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        ChangedBy = currentUser.UserId,
        ChangedByEmail = currentUser.Email,
        ChangedAt = clock.GetUtcNow()
    };
}

// ===================================================================== posts (FR-A50 .. FR-A52)

public interface IPostAdminService
{
    Task<IReadOnlyList<PostListItem>> ListAsync(int? languageId, ContentStatus? status, CancellationToken ct = default);
    Task<PostInput?> GetAsync(int id, CancellationToken ct = default);
    Task<(ContentStatus Status, ContentSource Source)?> GetStateAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(PostInput input, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default);
    Task<Result> DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Title)>> LectureOptionsAsync(int languageId, CancellationToken ct = default);
}

internal sealed class PostAdminService(IApplicationDbContext db, IAuditLogger audit, TimeProvider clock) : IPostAdminService
{
    public async Task<IReadOnlyList<PostListItem>> ListAsync(int? languageId, ContentStatus? status, CancellationToken ct = default)
    {
        var q = db.Posts.AsNoTracking().AsQueryable();
        if (languageId is { } l) q = q.Where(p => p.LanguageId == l);
        if (status is { } s) q = q.Where(p => p.Status == s);
        return await q.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.PublishAt ?? p.CreatedAt).Take(500)
            .Select(p => new PostListItem(p.Id, p.Title, p.PostType, p.Language.Name, p.Level, p.Status, p.Source, p.PublishAt, p.IsPinned,
                db.PostInteractions.Count(i => i.PostId == p.Id && i.Liked)))
            .ToListAsync(ct);
    }

    public async Task<PostInput?> GetAsync(int id, CancellationToken ct = default)
    {
        var p = await db.Posts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : new PostInput
        {
            Id = p.Id, LanguageId = p.LanguageId, PostType = p.PostType, Title = p.Title, Body = p.Body, Url = p.Url, ImageUrl = p.ImageUrl,
            Level = p.Level, Tags = p.Tags, LectureId = p.LectureId, PublishAtUtc = p.PublishAt?.UtcDateTime, IsPinned = p.IsPinned
        };
    }

    public async Task<(ContentStatus Status, ContentSource Source)?> GetStateAsync(int id, CancellationToken ct = default)
    {
        var p = await db.Posts.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Source }).FirstOrDefaultAsync(ct);
        return p is null ? null : (p.Status, p.Source);
    }

    public async Task<Result<int>> SaveAsync(PostInput input, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation(nameof(input.LanguageId), "Choose a language.");
        if (input.LectureId is { } lectureId &&
            !await db.Lectures.AnyAsync(l => l.Id == lectureId && l.Module.Course.LanguageId == input.LanguageId, ct))
            return Error.Validation(nameof(input.LectureId), "Pick a lecture of the same language.");
        if (input.PostType is PostType.Article or PostType.Video && string.IsNullOrWhiteSpace(input.Url))
            return Error.Validation(nameof(input.Url), "Articles and videos need a link.");

        Post post;
        if (input.Id == 0)
        {
            post = new Post { Status = ContentStatus.Draft, Source = ContentSource.Manual };
            db.Posts.Add(post);
        }
        else
        {
            post = await db.Posts.FirstOrDefaultAsync(p => p.Id == input.Id, ct) ?? throw new InvalidOperationException();
            if (post.Status == ContentStatus.Archived) return Error.Validation("Status", "Archived posts can't be edited.");
            if (post.Status == ContentStatus.Approved) post.Status = ContentStatus.Draft; // changes need a new review
        }

        post.LanguageId = input.LanguageId;
        post.PostType = input.PostType;
        post.Title = input.Title.Trim();
        post.Body = input.Body?.Trim();
        post.Url = string.IsNullOrWhiteSpace(input.Url) ? null : input.Url.Trim();
        post.ImageUrl = string.IsNullOrWhiteSpace(input.ImageUrl) ? null : input.ImageUrl.Trim();
        post.Level = input.Level;
        post.Tags = string.IsNullOrWhiteSpace(input.Tags) ? null
            : string.Join(", ", input.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());
        post.LectureId = input.LectureId;
        post.PublishAt = input.PublishAtUtc is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
        post.IsPinned = input.IsPinned;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Post", post.Id.ToString(), null,
            new { post.Title, Type = post.PostType.ToString(), post.LanguageId }, ct);
        return post.Id;
    }

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return Error.NotFound("Post");
        if (post.Status != ContentStatus.Approved) return Error.Validation("Status", "Only approved posts can be published. Send it for review first.");
        post.Status = ContentStatus.Published;
        post.PublishAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "Post", id.ToString(), null, new { post.PublishAt }, ct);
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default)
    {
        if (status is not (ContentStatus.Draft or ContentStatus.Archived))
            return Error.Validation("Status", "Use the review workflow to approve or publish.");
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return Error.NotFound("Post");
        post.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SetStatus", "Post", id.ToString(), null, new { Status = status.ToString() }, ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id, CancellationToken ct = default)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return Error.NotFound("Post");
        post.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "Post", id.ToString(), new { post.Title }, null, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<(int Id, string Title)>> LectureOptionsAsync(int languageId, CancellationToken ct = default) =>
        (await db.Lectures.AsNoTracking()
            .Where(l => l.Module.Course.LanguageId == languageId && !l.Module.IsDeleted && l.Status != ContentStatus.Archived)
            .OrderBy(l => l.Module.Course.Title).ThenBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder)
            .Select(l => new { l.Id, Title = l.Module.Course.Title + " · " + l.Title }).ToListAsync(ct))
        .Select(l => (l.Id, l.Title)).ToList();
}

// ===================================================================== error reports from learners (FR-L37, FR-A02)

public interface IContentReportAdminService
{
    Task<IReadOnlyList<ContentReportItem>> ListAsync(ReportStatus? status, CancellationToken ct = default);
    Task<Result> CloseAsync(int id, ReportStatus status, string? note, CancellationToken ct = default);
    Task<int> OpenCountAsync(CancellationToken ct = default);
}

internal sealed class ContentReportAdminService(IApplicationDbContext db, ICurrentUser currentUser, IAuditLogger audit, TimeProvider clock)
    : IContentReportAdminService
{
    public async Task<IReadOnlyList<ContentReportItem>> ListAsync(ReportStatus? status, CancellationToken ct = default)
    {
        var reports = await db.ContentReports.AsNoTracking()
            .Where(r => status == null || r.Status == status)
            .OrderByDescending(r => r.CreatedAt).Take(300)
            .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => new { r, u.Email })
            .ToListAsync(ct);

        var lectureIds = reports.Where(x => x.r.EntityType == ReportEntityType.Lecture).Select(x => x.r.EntityId).Distinct().ToList();
        var postIds = reports.Where(x => x.r.EntityType == ReportEntityType.Post).Select(x => x.r.EntityId).Distinct().ToList();
        var lectures = await db.Lectures.IgnoreQueryFilters().AsNoTracking().Where(l => lectureIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title }).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        var posts = await db.Posts.IgnoreQueryFilters().AsNoTracking().Where(p => postIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title }).ToDictionaryAsync(p => p.Id, p => p.Title, ct);

        return reports.Select(x => new ContentReportItem(x.r.Id, x.r.EntityType, x.r.EntityId,
            (x.r.EntityType == ReportEntityType.Lecture ? lectures.GetValueOrDefault(x.r.EntityId) : posts.GetValueOrDefault(x.r.EntityId)) ?? "(deleted)",
            x.r.Message, x.Email ?? "unknown", x.r.Status, x.r.CreatedAt, x.r.ResolutionNote)).ToList();
    }

    public async Task<Result> CloseAsync(int id, ReportStatus status, string? note, CancellationToken ct = default)
    {
        if (status == ReportStatus.Open) return Error.Validation("Status", "Choose Resolved or Dismissed.");
        var report = await db.ContentReports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return Error.NotFound("Report");
        report.Status = status;
        report.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        report.ResolvedBy = currentUser.UserId;
        report.ResolvedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(status.ToString(), "ContentReport", id.ToString(), null, new { note }, ct);
        return Result.Success();
    }

    public Task<int> OpenCountAsync(CancellationToken ct = default) => db.ContentReports.CountAsync(r => r.Status == ReportStatus.Open, ct);
}
