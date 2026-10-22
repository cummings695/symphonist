using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Tests.Orchestration;

public sealed class CoalescedTriggerTests
{
    [Fact] public async Task Requests_while_running_queue_at_most_one_more_run()
    {
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new CoalescedTrigger(async () =>
        {
            if (Interlocked.Increment(ref runs) == 1) await gate.Task;
        }, NullLogger.Instance);

        trigger.Request();
        while (Volatile.Read(ref runs) == 0) await Task.Delay(5);
        trigger.Request();
        trigger.Request();
        trigger.Request();
        gate.SetResult();
        await trigger.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, runs);

        trigger.Request();
        await trigger.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, runs);
    }

    [Fact] public async Task A_failing_run_is_logged_and_later_requests_still_run()
    {
        var runs = 0;
        var trigger = new CoalescedTrigger(() =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("boom");
        }, NullLogger.Instance);

        trigger.Request();
        await trigger.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        trigger.Request();
        await trigger.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, runs);
    }
}
