using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Content;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Lecture reader (FR-L32 .. FR-L37).</summary>
[Route("api/v1/lectures")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class LecturesController(ILectureReaderService lectures) : ApiControllerBase
{
    /// <summary>Open a lecture from the learner's roadmap. Locked lectures return 403 "lecture_locked".</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<ApiResponse<LectureDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, [FromQuery] int? learnerCourseId, CancellationToken ct) =>
        FromResult(await lectures.GetAsync(CurrentUserId, id, learnerCourseId, ct));

    /// <summary>Report reading time (sent when the reader is closed or every few minutes).</summary>
    [HttpPost("{id:int}/progress")]
    public async Task<IActionResult> Progress(int id, LectureProgressRequest request, CancellationToken ct) =>
        FromResult(await lectures.AddProgressAsync(CurrentUserId, id, request, ct));

    /// <summary>Mark complete (FR-L34). Needs the minimum reading time; unlocks the next lecture (FR-L31).</summary>
    [HttpPost("{id:int}/complete")]
    [ProducesResponseType<ApiResponse<CompleteLectureResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(int id, CompleteLectureRequest request, CancellationToken ct) =>
        FromResult(await lectures.CompleteAsync(CurrentUserId, id, request, ct), "Lecture completed.");

    [HttpGet("bookmarks")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<BookmarkDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bookmarks(CancellationToken ct) => OkData(await lectures.BookmarksAsync(CurrentUserId, ct));

    [HttpPost("{id:int}/bookmark")]
    public async Task<IActionResult> Bookmark(int id, CancellationToken ct) =>
        FromResult(await lectures.SetBookmarkAsync(CurrentUserId, id, true, ct), "Bookmarked.");

    [HttpDelete("{id:int}/bookmark")]
    public async Task<IActionResult> RemoveBookmark(int id, CancellationToken ct) =>
        FromResult(await lectures.SetBookmarkAsync(CurrentUserId, id, false, ct), "Bookmark removed.");

    /// <summary>Save (or clear, with empty text) a personal note (FR-L35).</summary>
    [HttpPut("{id:int}/note")]
    public async Task<IActionResult> Note(int id, SaveNoteRequest request, CancellationToken ct) =>
        FromResult(await lectures.SaveNoteAsync(CurrentUserId, id, request, ct), "Note saved.");

    [HttpPost("{id:int}/rating")]
    public async Task<IActionResult> Rate(int id, RateLectureRequest request, CancellationToken ct) =>
        FromResult(await lectures.RateAsync(CurrentUserId, id, request, ct), "Thanks for rating!");

    /// <summary>Report an error in a lecture (FR-L37); it goes to the admin panel.</summary>
    [HttpPost("{id:int}/report")]
    public async Task<IActionResult> Report(int id, ReportContentRequest request, CancellationToken ct) =>
        FromResult(await lectures.ReportAsync(CurrentUserId, ReportEntityType.Lecture, id, request, ct), "Thanks! The team will check it.");
}

/// <summary>Posts feed (FR-L40 .. FR-L42).</summary>
[Route("api/v1/posts")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class PostsController(IPostFeedService posts, ILectureReaderService lectures) : ApiControllerBase
{
    /// <summary>Feed of the learner's languages (or one language), pinned first. Filter by type, level or saved.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<PostDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Feed([FromQuery] int? languageId, [FromQuery] PostType? type, [FromQuery] CefrLevel? level,
        [FromQuery] bool saved = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        OkData(await posts.GetFeedAsync(CurrentUserId, new PostQuery(languageId, type, level, saved, page, pageSize), ct));

    [HttpPost("{id:int}/like")]
    [ProducesResponseType<ApiResponse<PostInteractionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Like(int id, CancellationToken ct) => FromResult(await posts.ToggleLikeAsync(CurrentUserId, id, ct));

    [HttpPost("{id:int}/save")]
    [ProducesResponseType<ApiResponse<PostInteractionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(int id, CancellationToken ct) => FromResult(await posts.ToggleSaveAsync(CurrentUserId, id, ct));

    [HttpPost("{id:int}/report")]
    public async Task<IActionResult> Report(int id, ReportContentRequest request, CancellationToken ct) =>
        FromResult(await lectures.ReportAsync(CurrentUserId, ReportEntityType.Post, id, request, ct), "Thanks! The team will check it.");
}
