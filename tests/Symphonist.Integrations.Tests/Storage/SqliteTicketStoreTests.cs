using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Integrations.Storage;

namespace Symphonist.Integrations.Tests.Storage;

public sealed class SqliteTicketStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("symphonist-store-").FullName;
    private readonly SqliteTicketStore _store;

    public SqliteTicketStoreTests() => _store = new SqliteTicketStore(Path.Combine(_dir, "symphonist.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static Ticket NewTicket(string key, string stage, StageStatus status) => new()
    {
        Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = "T " + key, StageId = stage,
        Status = status, CreatedAt = Now, UpdatedAt = Now,
    };

    [Fact]
    public async Task Save_then_find_roundtrips_all_fields()
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "Title", StageId = "plan",
            Status = StageStatus.AwaitingApproval, BlockedReason = "reason", Feedback = "fb", Attempts = 3,
            LastSourceStatus = "In Progress", Branch = "feat/x", SpecPath = "s.md", PlanPath = "p.md",
            PullRequestId = 42, MergeCommit = "abc", ReleasePullRequestId = 43, ReleaseMergeCommit = "def",
            CreatedAt = Now, UpdatedAt = Now.AddMinutes(1), StageStartedAt = Now.AddMinutes(2),
            WaitingSince = Now.AddMinutes(3),
        };
        await _store.SaveAsync(t, default);

        var found = await _store.FindAsync("orders", "ORD-1", default);

        Assert.NotNull(found);
        Assert.Equivalent(t, found, strict: true);
        Assert.Null(await _store.FindAsync("orders", "nope", default));
    }

    [Fact]
    public async Task Save_twice_updates_not_duplicates()
    {
        var t = NewTicket("A", "plan", StageStatus.Pending);
        await _store.SaveAsync(t, default);
        t.StageId = "build";
        await _store.SaveAsync(t, default);

        var all = await _store.ListAsync("orders", default);
        Assert.Single(all);
        Assert.Equal("build", all[0].StageId);
        Assert.Empty(await _store.ListAsync("other", default));
        Assert.Single(await _store.ListAsync(null, default));
    }

    [Fact]
    public async Task Events_are_returned_in_order()
    {
        var t = NewTicket("A", "plan", StageStatus.Pending);
        await _store.SaveAsync(t, default);
        await _store.AddEventAsync(t.Id, "plan", "first", Now, default);
        await _store.AddEventAsync(t.Id, "plan", "second", Now.AddSeconds(1), default);
        await _store.AddEventAsync(t.Id, "build", "third", Now.AddSeconds(2), default);

        var events = await _store.EventsAsync(t.Id, default);

        Assert.Equal(["first", "second", "third"], events.Select(e => e.Message));
    }

    [Fact]
    public async Task Agent_runs_are_persisted()
    {
        var t = NewTicket("A", "plan", StageStatus.Pending);
        await _store.SaveAsync(t, default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(5), true, 0.12m, null, "log.txt"), default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(1), false, null, "boom", "log2.txt"), default);
        await using var db = new SymphonistDbContext(new DbContextOptionsBuilder<SymphonistDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "symphonist.db")}").Options);
        var runs = await db.AgentRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync();

        Assert.Equal(2, runs.Count);
        Assert.Equal(t.Id, runs[0].TicketId);
        Assert.Equal("plan", runs[0].StageId);
        Assert.Equal(Now, runs[0].StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(5), runs[0].Duration);
        Assert.True(runs[0].Succeeded);
        Assert.Equal(0.12m, runs[0].CostUsd);
        Assert.Null(runs[0].Error);
        Assert.Equal("log.txt", runs[0].RawLogPath);
        Assert.False(runs[1].Succeeded);
        Assert.Null(runs[1].CostUsd);
        Assert.Equal("boom", runs[1].Error);
        Assert.Equal(TimeSpan.FromSeconds(1), runs[1].Duration);
        Assert.Equal("log2.txt", runs[1].RawLogPath);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_fail()
    {
        var tickets = Enumerable.Range(0, 20).Select(i => NewTicket("K" + i, "plan", StageStatus.Pending)).ToList();
        await Task.WhenAll(tickets.Select(t => _store.SaveAsync(t, default)));
        Assert.Equal(20, (await _store.ListAsync(null, default)).Count);
    }

    [Fact]
    public async Task Reset_interrupted_moves_running_agent_stages_to_pending()
    {
        await _store.SaveAsync(NewTicket("A", "plan", StageStatus.Running), default);
        await _store.SaveAsync(NewTicket("B", "build", StageStatus.Running), default);
        await _store.SaveAsync(NewTicket("C", "plan", StageStatus.Pending), default);

        var n = await _store.ResetInterruptedRunsAsync((_, stage) => stage == "plan", Now, default);

        Assert.Equal(1, n);
        var a = (await _store.FindAsync("orders", "A", default))!;
        Assert.Equal(StageStatus.Pending, a.Status);
        Assert.Equal(1, a.Attempts);
        Assert.Equal(Now, a.UpdatedAt);
        Assert.Contains("interrupted by restart; retrying", (await _store.EventsAsync(a.Id, default)).Select(e => e.Message));
        Assert.Equal(StageStatus.Running, (await _store.FindAsync("orders", "B", default))!.Status);
        Assert.Equal(StageStatus.Pending, (await _store.FindAsync("orders", "C", default))!.Status);
    }
}
