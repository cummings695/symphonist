using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

public sealed class NotifyingTicketStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");

    private readonly FakeTimeProvider time = new();
    private readonly FakeTicketStore inner = new();
    private readonly BoardNotifier notifier;
    private readonly NotifyingTicketStore store;
    private int raised;

    public NotifyingTicketStoreTests()
    {
        notifier = new BoardNotifier(time);
        notifier.Changed += () => raised++;
        store = new NotifyingTicketStore(inner, notifier);
    }

    private static Ticket NewTicket(string key) => new()
    {
        Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = key, StageId = "triage", Status = StageStatus.Pending,
    };

    [Fact]
    public async Task Writes_notify_reads_do_not()
    {
        var t = NewTicket("ORD-1");

        await store.SaveAsync(t, default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(1, raised);

        await store.AddEventAsync(t.Id, "triage", "hello", Now, default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(2, raised);

        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "triage", Now, TimeSpan.FromSeconds(1), true, 0.5m, null, "log"), default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(3, raised);

        await store.FindAsync("orders", "ORD-1", default);
        await store.ListAsync(null, default);
        await store.EventsAsync(t.Id, default);
        await store.AgentRunsAsync(t.Id, default);
        await store.AgentCostsAsync(default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task Reset_notifies_only_when_runs_were_reset()
    {
        Assert.Equal(0, await store.ResetInterruptedRunsAsync((_, _) => true, Now, default));
        time.Advance(BoardNotifier.Window);
        Assert.Equal(0, raised);

        var running = NewTicket("ORD-1");
        running.Status = StageStatus.Running;
        await inner.SaveAsync(running, default);
        Assert.Equal(1, await store.ResetInterruptedRunsAsync((_, _) => true, Now, default));
        time.Advance(BoardNotifier.Window);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Failed_write_does_not_notify()
    {
        await inner.SaveAsync(NewTicket("ORD-1"), default);

        // Same (project, key) under another id violates the unique index.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(NewTicket("ORD-1"), default));

        time.Advance(BoardNotifier.Window);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Gate_writes_notify()
    {
        var t = NewTicket("ORD-1");
        var verdict = new GateVerdict(RiskLevel.Low, "s", [], ["c"], []);

        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "plan", Now, Now, ReviewMode.Shadow, GatePolicy.Human, verdict), default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(1, raised);

        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "plan", Now, GateDecision.Approved, false, Now), default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(2, raised);

        var reviews = await store.GateReviewsAsync(t.Id, default);
        await store.ReviewAgreementAsync(null, default);
        time.Advance(BoardNotifier.Window);
        Assert.Equal(2, raised);
        Assert.Equal(GateDecision.Approved, Assert.Single(reviews).Decision);
    }
}
