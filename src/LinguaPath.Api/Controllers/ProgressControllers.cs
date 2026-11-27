using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Certificates;
using LinguaPath.Shared.Contracts.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LinguaPath.Api.Controllers;

/// <summary>Points wallet, streak, badges and leaderboards (FR-S01 .. FR-S09, FR-L111).</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class PointsController(IPointsService points) : ApiControllerBase
{
    /// <summary>Total, tier, weekly/monthly points, today's capped points, per-language breakdown, streak and badges.</summary>
    [HttpGet("points")]
    [ProducesResponseType<MyApiResponse<PointsSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken ct) => OkData(await points.SummaryAsync(CurrentUserId, ct));

    /// <summary>FR-S09: date, action, language, points; 50 per page, newest first.</summary>
    [HttpGet("points/history")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<PointTransactionDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History([FromQuery] int page = 1, CancellationToken ct = default) =>
        OkData(await points.HistoryAsync(CurrentUserId, page, ct));

    /// <summary>FR-S07: all languages or one, or just friends (scope=friends); weekly (from Monday 00:00 UTC), monthly or all-time. Top 50 plus your own rank.</summary>
    [HttpGet("leaderboard")]
    [ProducesResponseType<MyApiResponse<LeaderboardDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Leaderboard([FromQuery] LeaderboardPeriod period = LeaderboardPeriod.Weekly, [FromQuery] int? languageId = null,
        [FromQuery] string? scope = null, CancellationToken ct = default) =>
        OkData(await points.LeaderboardAsync(CurrentUserId, period, languageId, ct,
            string.Equals(scope, "friends", StringComparison.OrdinalIgnoreCase)));

    /// <summary>FR-S24: hide my name and photo on leaderboards.</summary>
    [HttpPut("me/leaderboard-visibility")]
    public async Task<IActionResult> Visibility(LeaderboardVisibilityRequest request, CancellationToken ct) =>
        FromResult(await points.SetLeaderboardVisibilityAsync(CurrentUserId, request.Show, ct));
}

/// <summary>My certificates (FR-L80 .. FR-L83).</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class CertificatesController(ICertificateService certificates) : ApiControllerBase
{
    [HttpGet("certificates")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<CertificateDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => OkData(await certificates.ListAsync(CurrentUserId, ct));

    [HttpGet("certificates/{id:int}")]
    [ProducesResponseType<MyApiResponse<CertificateDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await certificates.GetAsync(CurrentUserId, id, ct));

    [HttpGet("certificates/{id:int}/pdf")]
    [Produces("application/pdf")]
    public async Task<IActionResult> Pdf(int id, CancellationToken ct)
    {
        var result = await certificates.PdfAsync(CurrentUserId, id, ct);
        if (!result.IsSuccess) return ProblemResults.ToResult(HttpContext, result.Error!);
        return File(result.Value.Content, "application/pdf", result.Value.FileName);
    }

    /// <summary>BR-06 checklist for one of my courses: core lectures, monthly exams, course grade.</summary>
    [HttpGet("learner-courses/{learnerCourseId:int}/certificate-progress")]
    [ProducesResponseType<MyApiResponse<CertificateProgressDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Progress(int learnerCourseId, CancellationToken ct) =>
        FromResult(await certificates.ProgressAsync(CurrentUserId, learnerCourseId, ct));
}

/// <summary>
/// Public certificate verification (FR-L82). The HTML page at /verify/{number} is what the QR code opens; the JSON version is for
/// integrations. Without the verification code only "valid / revoked / not found" is shown.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Verify)]
public sealed class VerifyController(ICertificateService certificates) : ControllerBase
{
    [HttpGet("/api/v1/verify/{number}")]
    [ProducesResponseType<MyApiResponse<VerificationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Json(string number, [FromQuery(Name = "c")] string? code, CancellationToken ct) =>
        Ok(MyApiResponse<VerificationDto>.Ok(await certificates.VerifyAsync(number, code, ct)));

    [HttpGet("/verify/{number}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Page(string number, [FromQuery(Name = "c")] string? code, CancellationToken ct)
    {
        var dto = await certificates.VerifyAsync(number, code, ct);
        Response.Headers["X-Robots-Tag"] = "noindex";
        Response.Headers.CacheControl = "no-store";
        return new ContentResult
        {
            Content = certificates.VerificationHtml(dto, code), ContentType = "text/html; charset=utf-8",
            StatusCode = dto.Found ? StatusCodes.Status200OK : StatusCodes.Status404NotFound
        };
    }

    [HttpGet("/verify/{number}/pdf")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Pdf(string number, [FromQuery(Name = "c")] string? code, CancellationToken ct)
    {
        var result = await certificates.PublicPdfAsync(number, code ?? "", ct);
        if (!result.IsSuccess) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(result.Value.Content, "application/pdf", result.Value.FileName);
    }
}
