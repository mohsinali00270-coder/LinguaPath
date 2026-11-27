using LinguaPath.Application.Features.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.Ai;

/// <summary>
/// Works through the AI job queue (AiGenerationRequests) in the background (SRS 5.3).
/// Jobs are stored in the database, so they survive restarts; a job whose runner stops is picked up again when its lease ends.
/// Hangfire could replace this later without changing the jobs themselves.
/// </summary>
internal sealed class AiJobRunner(IServiceScopeFactory scopes, IOptions<AiOptions> options, ILogger<AiJobRunner> logger) : BackgroundService
{
    private readonly string _runnerId = $"{Environment.MachineName}:{Guid.NewGuid():N}" is var id && id.Length > 90 ? id[..90] : id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(options.Value.Runner.PollSeconds, 2, 300));
        logger.LogInformation("AI job runner {Runner} started", _runnerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                worked = await scope.ServiceProvider.GetRequiredService<IAiJobProcessor>().RunNextAsync(_runnerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI job runner loop failed");
            }

            // Keep going straight away while there is work; otherwise wait for the next poll.
            if (!worked)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
