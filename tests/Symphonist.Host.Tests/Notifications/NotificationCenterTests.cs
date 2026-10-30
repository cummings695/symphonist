using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Notifications;

namespace Symphonist.Host.Tests.Notifications;

public sealed class NotificationCenterTests
{
    private static readonly DashboardAddress Address = new(5199);

    private sealed class RecordingNotifier : INotifier
    {
        private readonly Lock sync = new();
        private readonly List<DesktopNotification> sent = [];
        public IReadOnlyList<DesktopNotification> Sent { get { lock (sync) return sent.ToList(); } }

        public Task ShowAsync(DesktopNotification notification, CancellationToken ct)
        {
            lock (sync) sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static string Name(string id) => id == "orders" ? "Orders" : id;

    private static (NotificationCenter Center, RecordingNotifier Notifier, FakeTimeProvider Time) Create()
    {
        var notifier = new RecordingNotifier();
        var time = new FakeTimeProvider();
        return (new NotificationCenter(notifier, Address, Name, time, NullLogger<NotificationCenter>.Instance), notifier, time);
    }

    private static NotificationEvent Waiting(string key, string line = "plan") =>
        new(NotificationKind.WaitingOnYou, "orders", key, line);

    [Fact]
    public async Task Single_event_gets_a_detailed_notification()
    {
        var (center, notifier, time) = Create();
        center.Post(Waiting("ORD-412", "plan · reviewer says medium risk"));
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();

        var n = Assert.Single(notifier.Sent);
        Assert.Equal("ORD-412 is waiting on you", n.Title);
        Assert.Equal("Orders · plan · reviewer says medium risk", n.Body);
        Assert.Equal("http://127.0.0.1:5199/tickets/orders/ORD-412", n.Link.AbsoluteUri);
    }

    [Fact]
    public async Task Events_within_the_window_fold_into_one()
    {
        var (center, notifier, time) = Create();
        center.Post(Waiting("ORD-1"));
        center.Post(Waiting("ORD-2"));
        center.Post(new NotificationEvent(NotificationKind.Blocked, "orders", "ORD-3", "stuck"));
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();

        var n = Assert.Single(notifier.Sent);
        Assert.Equal("3 tickets need you", n.Title);
        Assert.Equal("2 waiting on you · 1 blocked", n.Body);
        Assert.Equal("http://127.0.0.1:5199/", n.Link.AbsoluteUri);
    }

    [Fact]
    public async Task Ten_blocked_in_one_tick_send_one_notification()
    {
        var (center, notifier, time) = Create();
        for (var i = 0; i < 10; i++)
            center.Post(new NotificationEvent(NotificationKind.Blocked, "orders", $"ORD-{i}", "stuck"));
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();

        var n = Assert.Single(notifier.Sent);
        Assert.Equal("10 tickets need you", n.Title);
        Assert.Equal("10 blocked", n.Body);
    }

    [Fact]
    public void Nothing_is_sent_before_the_window_ends()
    {
        var (center, notifier, time) = Create();
        center.Post(Waiting("ORD-1"));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(notifier.Sent);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Duplicate_event_in_a_window_is_dropped()
    {
        var (center, notifier, _) = Create();
        center.Post(Waiting("ORD-1"));
        center.Post(Waiting("ORD-1", "another line"));
        await center.FlushAsync();

        var n = Assert.Single(notifier.Sent);
        Assert.Equal("ORD-1 is waiting on you", n.Title);
    }

    [Fact]
    public void Project_events_link_to_the_filtered_board()
    {
        var n = NotificationCenter.Compose(
            [new NotificationEvent(NotificationKind.ReleaseReady, "orders", null, "2 tickets merged")], Address, Name);
        Assert.Equal("Orders has work ready to release", n.Title);
        Assert.Equal("http://127.0.0.1:5199/?project=orders", n.Link.AbsoluteUri);
    }

    [Fact]
    public void Limit_events_use_the_provider_name()
    {
        var paused = NotificationCenter.Compose(
            [new NotificationEvent(NotificationKind.LimitPaused, null, null, "resets at 5pm", "Claude Code")], Address, Name);
        Assert.Equal("Claude Code usage limit reached", paused.Title);
        Assert.Equal("resets at 5pm", paused.Body);
        var resumed = NotificationCenter.Compose(
            [new NotificationEvent(NotificationKind.LimitResumed, null, null, "back", "Claude Code")], Address, Name);
        Assert.Equal("Claude Code jobs resumed", resumed.Title);
    }

    [Fact]
    public void Line_is_cut_to_200_characters()
    {
        var n = NotificationCenter.Compose([Waiting("ORD-1", new string('x', 500))], Address, Name);
        Assert.Equal("Orders · ".Length + 200, n.Body.Length);
    }

    [Fact]
    public void Line_is_one_line()
    {
        var n = NotificationCenter.Compose(
            [new NotificationEvent(NotificationKind.Blocked, "orders", "ORD-1", "error: boom\r\n  at Foo()\n\tat Bar()")], Address, Name);
        Assert.Equal("Orders · error: boom at Foo() at Bar()", n.Body);
    }
}
