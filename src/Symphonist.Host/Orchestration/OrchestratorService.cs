namespace Symphonist.Host.Orchestration;

/// <summary>Resets interrupted runs once, then ticks every <see cref="OrchestratorOptions.PollInterval"/> until shutdown.</summary>
public sealed class OrchestratorService(Orchestrator o, OrchestratorOptions options, ILogger<OrchestratorService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                await o.RecoverAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Resetting interrupted agent runs failed");
            }

            using var timer = new PeriodicTimer(options.PollInterval);
            do
            {
                try
                {
                    await o.TickAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(e, "Orchestrator tick failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
