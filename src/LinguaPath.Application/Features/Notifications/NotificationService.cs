using System.Text.Json;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Application.Features.Notifications;

/// <summary>Writes inbox entries and sends the matching push (FR-L110, FR-L112).</summary>
public interface INotificationService
{
    /// <summary>
    /// Creates an inbox entry per learner (honouring their switches and the dedup key) and pushes to those who want it.
    /// Returns how many entries were created.
    /// </summary>
    Task<int> NotifyAsync(IReadOnlyCollection<Guid> userIds, NotificationType type, string title, string body,
        IReadOnlyDictionary<string, string>? data = null, string? dedupKey = null, CancellationToken ct = default);

    Task<NotificationPageDto> ListAsync(Guid userId, long? beforeId, int take, CancellationToken ct = default);
    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default);
    Task<Result> MarkReadAsync(Guid userId, long id, CancellationToken ct = default);
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid userId, long id, CancellationToken ct = default);
}

internal sealed class NotificationService(
    IApplicationDbContext db,
    IPushSender push,
    TimeProvider clock,
    ILogger<NotificationService> logger) : INotificationService
{
    private const int MaxPage = 50;

    public async Task<int> NotifyAsync(IReadOnlyCollection<Guid> userIds, NotificationType type, string title, string body,
        IReadOnlyDictionary<string, string>? data = null, string? dedupKey = null, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return 0;
        var ids = userIds.Distinct().ToList();
        var created = 0;
        // Chunks keep the IN lists small on SQL Server.
        foreach (var chunk in ids.Chunk(500))
            created += await NotifyChunkAsync(chunk, type, title, body, data, dedupKey, ct);
        return created;
    }

    private async Task<int> NotifyChunkAsync(Guid[] ids, NotificationType type, string title, string body,
        IReadOnlyDictionary<string, string>? data, string? dedupKey, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && !u.IsDeleted && u.IsActive)
            .Select(u => new
            {
                u.Id,
                Switches = new NotificationSwitches(u.NotifyStudyReminders, u.NotifyQuizReady, u.NotifyExamReminders,
                    u.NotifyStreakAlerts, u.NotifyCertificates, u.NotifyNewPosts)
            })
            .ToListAsync(ct);

        var already = dedupKey is null
            ? new HashSet<Guid>()
            : (await db.Notifications.AsNoTracking().Where(n => ids.Contains(n.UserId) && n.DedupKey == dedupKey)
                .Select(n => n.UserId).ToListAsync(ct)).ToHashSet();

        var now = clock.GetUtcNow();
        var json = data is null || data.Count == 0 ? null : JsonSerializer.Serialize(data);
        var rows = new List<Notification>();
        var pushTo = new List<Guid>();
        foreach (var u in users)
        {
            if (already.Contains(u.Id)) continue;
            var (inbox, wantsPush) = ReminderRules.Delivery(type, u.Switches);
            if (!inbox) continue;
            rows.Add(new Notification
            {
                UserId = u.Id, Type = type, Title = ReminderRules.Trim(title, 150), Body = ReminderRules.Trim(body, 500),
                DataJson = json, DedupKey = dedupKey, PushSent = wantsPush, CreatedAt = now
            });
            if (wantsPush) pushTo.Add(u.Id);
        }
        if (rows.Count == 0) return 0;

        db.Notifications.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another server wrote the same reminder first (unique UserId + DedupKey). Nothing is lost.
            db.DiscardChanges();
            logger.LogInformation(ex, "Notification {Key} was already created elsewhere", dedupKey);
            return 0;
        }

        if (pushTo.Count > 0)
        {
            var pushData = new Dictionary<string, string>(data ?? new Dictionary<string, string>()) { ["notificationType"] = type.ToString() };
            try
            {
                await push.SendAsync(pushTo, new PushMessage(title, ReminderRules.Trim(body, 180), pushData), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The inbox entry is there; a failed push isn't worth failing the caller for.
                logger.LogWarning(ex, "Push for {Type} failed for {Count} learner(s)", type, pushTo.Count);
            }
        }
        return rows.Count;
    }

    public async Task<NotificationPageDto> ListAsync(Guid userId, long? beforeId, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxPage);
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (beforeId is { } before) q = q.Where(n => n.Id < before);
        var rows = await q.OrderByDescending(n => n.Id).Take(take + 1)
            .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.DataJson, n.CreatedAt, n.ReadAt })
            .ToListAsync(ct);
        var items = rows.Take(take).Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, ReadData(n.DataJson), n.CreatedAt, n.ReadAt != null))
            .ToList();
        return new NotificationPageDto(items, await UnreadCountAsync(userId, ct), rows.Count > take);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default) =>
        db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

    public async Task<Result> MarkReadAsync(Guid userId, long id, CancellationToken ct = default)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (n is null) return Error.NotFound("Notification");
        if (n.ReadAt is null)
        {
            n.ReadAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Result.Success();
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var unread = await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var n in unread) n.ReadAt = now;
        if (unread.Count > 0) await db.SaveChangesAsync(ct);
        return unread.Count;
    }

    public async Task<Result> DeleteAsync(Guid userId, long id, CancellationToken ct = default)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (n is null) return Error.NotFound("Notification");
        db.Notifications.Remove(n);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    internal static IReadOnlyDictionary<string, string>? ReadData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return null; }
    }
}
