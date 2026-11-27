using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Notifications;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LinguaPath.Api.Controllers;

/// <summary>In-app notification inbox (FR-L112). Reminders themselves are created by the reminder worker (FR-L110).</summary>
[Route("api/v1/notifications")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class NotificationsController(INotificationService notifications) : ApiControllerBase
{
    /// <summary>Newest first. Pass the last id you have as <c>before</c> for the next page.</summary>
    [HttpGet]
    [ProducesResponseType<MyApiResponse<NotificationPageDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] long? before, [FromQuery] int take = 30, CancellationToken ct = default) =>
        OkData(await notifications.ListAsync(CurrentUserId, before, take, ct));

    [HttpGet("unread-count")]
    [ProducesResponseType<MyApiResponse<UnreadCountDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) =>
        OkData(new UnreadCountDto(await notifications.UnreadCountAsync(CurrentUserId, ct)));

    [HttpPut("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken ct) =>
        FromResult(await notifications.MarkReadAsync(CurrentUserId, id, ct));

    [HttpPut("read-all")]
    [ProducesResponseType<MyApiResponse<UnreadCountDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(CurrentUserId, ct);
        return OkData(new UnreadCountDto(0));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) =>
        FromResult(await notifications.DeleteAsync(CurrentUserId, id, ct));
}

/// <summary>
/// Crash and unexpected-error reports from the app (NFR-14). They go to the server log (Seq/files), not the database.
/// Anonymous is allowed so crashes before sign-in are seen too; the rate limit stops floods.
/// </summary>
[Route("api/v1/client-errors")]
[AllowAnonymous]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ApiControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.ClientErrors)]
    [RequestSizeLimit(64 * 1024)]
    public IActionResult Report(ClientErrorReport report)
    {
        Guid? userId = User.Identity?.IsAuthenticated == true ? CurrentUserId : null;
        logger.LogError(
            "Client error {ExceptionType} on {Platform} {OsVersion} ({DeviceModel}), app {AppVersion}, page {Page}, user {UserId}, at {OccurredAt}: {ClientMessage}\n{ClientStackTrace}",
            Cut(report.ExceptionType, 200), Cut(report.Platform, 20), Cut(report.OsVersion, 40), Cut(report.DeviceModel, 80),
            Cut(report.AppVersion, 20), Cut(report.Page, 100), userId, report.OccurredAt, Cut(report.Message, 1000), Cut(report.StackTrace, 8000));
        return Accepted();
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
