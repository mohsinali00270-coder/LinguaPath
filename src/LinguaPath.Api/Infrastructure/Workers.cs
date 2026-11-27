using LinguaPath.Application.Features.Notifications;
using Microsoft.Extensions.Options;

namespace LinguaPath.Api.Infrastructure;

/// <summary>
/// Runs the FR-L110 reminders every few minutes. Safe on more than one server: each reminder has a dedup key
/// with a unique index, so a second server's copy is dropped.
/// </summary>
internal sealed class ReminderWorker(IServiceScopeFactory scopes, IOptions<ReminderOptions> options, TimeProvider clock,
    ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IReminderService>().RunAsync(clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reminder run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Daily data-retention clean-up (NFR-08): practice recordings, old inbox entries, expired tokens, deleted-account leftovers.</summary>
internal sealed class MaintenanceWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider clock,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        DateOnly? lastRun = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (lastRun == today || now.Hour < options.Value.RunAtUtcHour) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().RunAsync(now, stoppingToken);
                lastRun = today;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Maintenance run failed; it will be tried again in 10 minutes");
            }
        }
    }
}
