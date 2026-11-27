using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Content;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Content;

public record PostQuery(int? LanguageId, PostType? Type, CefrLevel? Level, bool SavedOnly = false, int Page = 1, int PageSize = 20);

public interface IPostFeedService
{
    Task<PagedResult<PostDto>> GetFeedAsync(Guid userId, PostQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<PostDto>> RelatedToLectureAsync(Guid userId, int lectureId, CancellationToken ct = default);
    Task<Result<PostInteractionResponse>> ToggleLikeAsync(Guid userId, int postId, CancellationToken ct = default);
    Task<Result<PostInteractionResponse>> ToggleSaveAsync(Guid userId, int postId, CancellationToken ct = default);
}

/// <summary>Posts feed (FR-L40 .. FR-L42): published posts whose publish time has passed, pinned first.</summary>
internal sealed class PostFeedService(IApplicationDbContext db, TimeProvider clock) : IPostFeedService
{
    public async Task<PagedResult<PostDto>> GetFeedAsync(Guid userId, PostQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 5, 50);
        var q = Visible();

        if (query.LanguageId is { } languageId)
        {
            q = q.Where(p => p.LanguageId == languageId);
        }
        else
        {
            // Default: languages the learner is studying.
            var mine = await db.LearnerCourses.Where(c => c.UserId == userId &&
                    (c.Status == LearnerCourseStatus.Active || c.Status == LearnerCourseStatus.Paused))
                .Select(c => c.LanguageId).Distinct().ToListAsync(ct);
            if (mine.Count > 0) q = q.Where(p => mine.Contains(p.LanguageId));
        }
        if (query.Type is { } type) q = q.Where(p => p.PostType == type);
        if (query.Level is { } level) q = q.Where(p => p.Level == null || p.Level == level);
        if (query.SavedOnly)
            q = q.Where(p => db.PostInteractions.Any(i => i.PostId == p.Id && i.UserId == userId && i.Saved));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.PublishAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(Projection(userId))
            .ToListAsync(ct);

        return new PagedResult<PostDto> { Items = rows.Select(ToDto).ToList(), Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyList<PostDto>> RelatedToLectureAsync(Guid userId, int lectureId, CancellationToken ct = default) =>
        (await Visible().Where(p => p.LectureId == lectureId)
            .OrderByDescending(p => p.PublishAt).Take(5)
            .Select(Projection(userId)).ToListAsync(ct))
        .Select(ToDto).ToList();

    public Task<Result<PostInteractionResponse>> ToggleLikeAsync(Guid userId, int postId, CancellationToken ct = default) =>
        ToggleAsync(userId, postId, like: true, ct);

    public Task<Result<PostInteractionResponse>> ToggleSaveAsync(Guid userId, int postId, CancellationToken ct = default) =>
        ToggleAsync(userId, postId, like: false, ct);

    private async Task<Result<PostInteractionResponse>> ToggleAsync(Guid userId, int postId, bool like, CancellationToken ct)
    {
        if (!await Visible().AnyAsync(p => p.Id == postId, ct)) return Error.NotFound("Post");

        var row = await db.PostInteractions.FirstOrDefaultAsync(i => i.PostId == postId && i.UserId == userId, ct);
        if (row is null)
        {
            row = new PostInteraction { PostId = postId, UserId = userId };
            db.PostInteractions.Add(row);
        }
        if (like) row.Liked = !row.Liked;
        else row.Saved = !row.Saved;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var likes = await db.PostInteractions.CountAsync(i => i.PostId == postId && i.Liked, ct);
        return new PostInteractionResponse(postId, likes, row.Liked, row.Saved);
    }

    private IQueryable<Post> Visible()
    {
        var now = clock.GetUtcNow();
        return db.Posts.AsNoTracking().Where(p => p.Status == ContentStatus.Published && (p.PublishAt == null || p.PublishAt <= now) &&
                                                  p.Language.Status != LanguageStatus.Disabled);
    }

    private sealed record Row(
        int Id, int LanguageId, string LanguageName, PostType PostType, string Title, string? Body, string? Url, string? ImageUrl,
        CefrLevel? Level, string? Tags, DateTimeOffset? PublishAt, DateTimeOffset CreatedAt, bool IsPinned, int? LectureId,
        string? LectureTitle, int Likes, bool Liked, bool Saved);

    private System.Linq.Expressions.Expression<Func<Post, Row>> Projection(Guid userId) => p => new Row(
        p.Id, p.LanguageId, p.Language.Name, p.PostType, p.Title, p.Body, p.Url, p.ImageUrl, p.Level, p.Tags, p.PublishAt, p.CreatedAt,
        p.IsPinned, p.LectureId, p.Lecture != null ? p.Lecture.Title : null,
        db.PostInteractions.Count(i => i.PostId == p.Id && i.Liked),
        db.PostInteractions.Any(i => i.PostId == p.Id && i.UserId == userId && i.Liked),
        db.PostInteractions.Any(i => i.PostId == p.Id && i.UserId == userId && i.Saved));

    private static PostDto ToDto(Row r) => new(
        r.Id, r.LanguageId, r.LanguageName, r.PostType, r.Title, r.Body, r.Url, r.ImageUrl, r.Level,
        string.IsNullOrWhiteSpace(r.Tags) ? [] : r.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        r.PublishAt ?? r.CreatedAt, r.IsPinned, r.LectureId, r.LectureTitle, r.Likes, r.Liked, r.Saved);
}
