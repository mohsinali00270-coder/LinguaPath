using LinguaPath.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Notifications;

/// <summary>Data-retention windows (config section "Retention", NFR-08).</summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    public bool Enabled { get; set; } = true;
    /// <summary>Practice voice recordings are deleted after this many days. Exam recordings stay with the marked attempt.</summary>
    public int PracticeRecordingDays { get; set; } = 30;
    public int ReadNotificationDays { get; set; } = 90;
    public int UnreadNotificationDays { get; set; } = 180;
    /// <summary>What the anonymised account still holds (practice text, translation history, inbox) is removed after this.</summary>
    public int DeletedAccountPurgeDays { get; set; } = 30;
    public int ExpiredTokenDays { get; set; } = 30;
    /// <summary>Hour (UTC) the daily clean-up runs.</summary>
    public int RunAtUtcHour { get; set; } = 2;
}

public sealed record MaintenanceResult(
    int RecordingsDeleted, int NotificationsDeleted, int TokensDeleted, int OtpsDeleted, int DeletedAccountRowsPurged);

public interface IMaintenanceService
{
    Task<MaintenanceResult> RunAsync(DateTimeOffset utcNow, CancellationToken ct = default);
}

internal sealed class MaintenanceService(
    IApplicationDbContext db,
    IFileStorage storage,
    IOptions<RetentionOptions> options,
    ILogger<MaintenanceService> logger) : IMaintenanceService
{
    private const int Batch = 500;
    private readonly RetentionOptions _o = options.Value;

    public async Task<MaintenanceResult> RunAsync(DateTimeOffset utcNow, CancellationToken ct = default)
    {
        var recordings = await PracticeRecordingsAsync(utcNow.AddDays(-_o.PracticeRecordingDays), ct);

        var readBefore = utcNow.AddDays(-_o.ReadNotificationDays);
        var unreadBefore = utcNow.AddDays(-_o.UnreadNotificationDays);
        var notifications = await DeleteInBatchesAsync(
            db.Notifications, n => (n.ReadAt != null && n.ReadAt < readBefore) || n.CreatedAt < unreadBefore, ct);

        var tokenBefore = utcNow.AddDays(-_o.ExpiredTokenDays);
        var tokens = await DeleteInBatchesAsync(db.RefreshTokens, t => t.ExpiresAt < tokenBefore, ct);
        var otpBefore = utcNow.AddDays(-1);
        var otps = await DeleteInBatchesAsync(db.EmailOtps, o => o.ExpiresAt < otpBefore, ct);

        var purged = await PurgeDeletedAccountsAsync(utcNow.AddDays(-_o.DeletedAccountPurgeDays), ct);

        var result = new MaintenanceResult(recordings, notifications, tokens, otps, purged);
        logger.LogInformation("Maintenance finished: {@Result}", result);
        return result;
    }

    /// <summary>NFR-08: practice recordings are deleted after 30 days; the score and feedback stay.</summary>
    private async Task<int> PracticeRecordingsAsync(DateTimeOffset before, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var rows = await db.PracticeAttempts.Where(a => a.AudioUrl != null && a.CreatedAt < before)
                .OrderBy(a => a.Id).Take(Batch).ToListAsync(ct);
            if (rows.Count == 0) return total;
            foreach (var a in rows)
            {
                await DeleteFileAsync(a.AudioUrl, ct);
                a.AudioUrl = null;
            }
            await db.SaveChangesAsync(ct);
            total += rows.Count;
            if (rows.Count < Batch) return total;
        }
    }

    /// <summary>Accounts are anonymised the moment they're deleted; this removes the leftovers that could still be personal.</summary>
    private async Task<int> PurgeDeletedAccountsAsync(DateTimeOffset before, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.IsDeleted && u.DeletedAt != null && u.DeletedAt < before)
            .Select(u => u.Id).ToListAsync(ct);
        if (users.Count == 0) return 0;

        var total = 0;
        foreach (var chunk in users.Chunk(Batch))
        {
            var ids = chunk.ToList();
            var attempts = await db.PracticeAttempts.Where(a => ids.Contains(a.UserId)).ToListAsync(ct);
            foreach (var a in attempts) await DeleteFileAsync(a.AudioUrl, ct);
            db.PracticeAttempts.RemoveRange(attempts);
            var history = await db.TranslationEntries.Where(t => ids.Contains(t.UserId)).ToListAsync(ct);
            db.TranslationEntries.RemoveRange(history);
            var inbox = await db.Notifications.Where(n => ids.Contains(n.UserId)).ToListAsync(ct);
            db.Notifications.RemoveRange(inbox);
            var devices = await db.UserDevices.Where(d => ids.Contains(d.UserId)).ToListAsync(ct);
            db.UserDevices.RemoveRange(devices);
            await db.SaveChangesAsync(ct);
            total += attempts.Count + history.Count + inbox.Count + devices.Count;
        }
        return total;
    }

    private async Task<int> DeleteInBatchesAsync<T>(DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> where, CancellationToken ct)
        where T : class
    {
        // Load-and-remove keeps it provider-neutral (ExecuteDelete isn't on the IApplicationDbContext surface).
        var total = 0;
        while (true)
        {
            var rows = await set.Where(where).Take(Batch).ToListAsync(ct);
            if (rows.Count == 0) return total;
            set.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            total += rows.Count;
            if (rows.Count < Batch) return total;
        }
    }

    private async Task DeleteFileAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { await storage.DeleteAsync(url, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not delete stored file {Url}", url);
        }
    }
}
