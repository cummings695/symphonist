using Microsoft.Extensions.Time.Testing;
using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Tests.Dashboard;

public sealed class BoardNotifierTests
{
    private readonly FakeTimeProvider time = new();

    [Fact]
    public void Burst_raises_changed_once()
    {
        var notifier = new BoardNotifier(time);
        var raised = 0;
        notifier.Changed += () => raised++;

        for (var i = 0; i < 10; i++) notifier.Notify();
        time.Advance(BoardNotifier.Window - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, raised);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Notify_after_window_raises_again()
    {
        var notifier = new BoardNotifier(time);
        var raised = 0;
        notifier.Changed += () => raised++;

        notifier.Notify();
        time.Advance(BoardNotifier.Window);
        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Throwing_subscriber_does_not_stop_others()
    {
        var notifier = new BoardNotifier(time);
        var counted = 0;
        notifier.Changed += () => throw new InvalidOperationException("boom");
        notifier.Changed += () => counted++;

        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        Assert.Equal(1, counted);
    }
}
