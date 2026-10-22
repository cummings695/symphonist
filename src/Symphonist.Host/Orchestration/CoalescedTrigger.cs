namespace Symphonist.Host.Orchestration;

/// <summary>
/// Runs <c>action</c> in the background on request. Requests made while a run is in progress coalesce into at most one
/// further run, so bursts never queue more than one pending run. Failures are logged, never thrown.
/// </summary>
public sealed class CoalescedTrigger(Func<Task> action, ILogger log)
{
    private readonly Lock sync = new();
    private bool running, pending;
    private Task current = Task.CompletedTask;

    public void Request()
    {
        lock (sync)
        {
            if (running)
            {
                pending = true;
                return;
            }
            running = true;
            current = Task.Run(LoopAsync);
        }
    }

    /// <summary>Completes when no run is in progress or pending.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task task;
            lock (sync) task = current;
            await task;
            lock (sync)
                if (!running && ReferenceEquals(task, current)) return;
        }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                log.LogError(e, "Background run failed");
            }

            lock (sync)
            {
                if (!pending)
                {
                    running = false;
                    return;
                }
                pending = false;
            }
        }
    }
}
