namespace Symphonist.Host.Orchestration;

/// <summary>
/// Resets interrupted runs once, then ticks every <see cref="OrchestratorOptions.PollInterval"/> until shutdown. On
/// shutdown it cancels in-flight agent runs and waits (up to <see cref="RunDrainTimeout"/>) for them to clean up, so no
/// agent container outlives the host.
/// </summary>
public sealed class OrchestratorService(Orchestrator o, OrchestratorOptions options, ILogger<OrchestratorService> log)
    : BackgroundService
{
    public static readonly TimeSpan RunDrainTimeout = TimeSpan.FromSeconds(30);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        o.CancelRuns();
        try
        {
            await o.WhenIdleAsync().WaitAsync(RunDrainTimeout, cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            log.LogWarning("Agent runs still running at shutdown; containers may be left behind (removed at next start)");
        }
    }

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
