using LinguaPath.Application.Features.Assessment;

namespace LinguaPath.Api.Infrastructure;

/// <summary>
/// Closes exam attempts whose time ran out and daily quizzes left open past midnight (FR-L53, FR-L62).
/// Attempts are also closed whenever the learner opens them, so this only keeps marks up to date.
/// </summary>
internal sealed class AttemptExpiryService(IServiceScopeFactory scopes, ILogger<AttemptExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var closed = await scope.ServiceProvider.GetRequiredService<IAssessmentService>().CloseExpiredAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("Closed {Count} expired quiz attempt(s)", closed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Closing expired attempts failed");
            }
        }
    }
}
