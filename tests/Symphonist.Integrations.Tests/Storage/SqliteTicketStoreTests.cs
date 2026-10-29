using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
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
            LastSourceStatus = "In Progress", PendingSourceStatus = "Planned", Branch = "feat/x", SpecPath = "s.md", PlanPath = "p.md",
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
    public async Task Claude_handled_time_round_trips()
    {
        var t = NewTicket("A", "triage", StageStatus.Pending);
        t.ClaudeCommentsHandledAt = new DateTimeOffset(2026, 10, 21, 14, 30, 15, 123, TimeSpan.FromHours(2));
        await _store.SaveAsync(t, default);

        var found = await _store.FindAsync("orders", "A", default);

        Assert.Equal(t.ClaudeCommentsHandledAt, found!.ClaudeCommentsHandledAt);
        Assert.Equal(t.ClaudeCommentsHandledAt.Value.Offset, found.ClaudeCommentsHandledAt!.Value.Offset);

        t.ClaudeCommentsHandledAt = null;
        await _store.SaveAsync(t, default);
        Assert.Null((await _store.FindAsync("orders", "A", default))!.ClaudeCommentsHandledAt);
    }

    [Fact]
    public async Task Tracker_retry_status_round_trips()
    {
        var t = NewTicket("A", "plan", StageStatus.Pending);
        t.TrackerRetryStatus = "In Progress";
        await _store.SaveAsync(t, default);

        Assert.Equal("In Progress", (await _store.FindAsync("orders", "A", default))!.TrackerRetryStatus);

        t.TrackerRetryStatus = null;
        await _store.SaveAsync(t, default);
        Assert.Null((await _store.FindAsync("orders", "A", default))!.TrackerRetryStatus);
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
    public async Task Delete_removes_ticket_events_and_runs()
    {
        var gone = NewTicket("A", "plan", StageStatus.Pending);
        var kept = NewTicket("B", "plan", StageStatus.Pending);
        await _store.SaveAsync(gone, default);
        await _store.SaveAsync(kept, default);
        foreach (var t in new[] { gone, kept })
        {
            await _store.AddEventAsync(t.Id, "plan", "event", Now, default);
            await _store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(1), true, 0.1m, null, "a.json"), default);
        }

        Assert.True(await _store.DeleteAsync("orders", "A", default));

        Assert.Null(await _store.FindAsync("orders", "A", default));
        Assert.Empty(await _store.EventsAsync(gone.Id, default));
        Assert.Empty(await _store.AgentRunsAsync(gone.Id, default));
        Assert.NotNull(await _store.FindAsync("orders", "B", default));
        Assert.Single(await _store.EventsAsync(kept.Id, default));
        Assert.Single(await _store.AgentRunsAsync(kept.Id, default));
    }

    [Fact]
    public async Task Delete_unknown_returns_false()
    {
        await _store.SaveAsync(NewTicket("A", "plan", StageStatus.Pending), default);

        Assert.False(await _store.DeleteAsync("orders", "NOPE", default));
        Assert.False(await _store.DeleteAsync("other", "A", default));
        Assert.NotNull(await _store.FindAsync("orders", "A", default));
    }

    [Fact]
    public async Task Agent_runs_round_trip_with_summary_oldest_first()
    {
        var t = NewTicket("A", "plan", StageStatus.Pending);
        await _store.SaveAsync(t, default);
        var second = new AgentRunRecord(t.Id, "build", Now.AddMinutes(1), TimeSpan.FromSeconds(2), false, null, "boom", "b.json", "second");
        var first = new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(5), true, 0.12m, null, "a.json", "first");
        await _store.AddAgentRunAsync(second, default);
        await _store.AddAgentRunAsync(first, default);

        var runs = await _store.AgentRunsAsync(t.Id, default);

        Assert.Equal([first, second], runs);
        Assert.Empty(await _store.AgentRunsAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Agent_costs_sum_per_ticket_and_skip_null()
    {
        var t1 = NewTicket("A", "plan", StageStatus.Pending);
        var t2 = NewTicket("B", "plan", StageStatus.Pending);
        await _store.SaveAsync(t1, default);
        await _store.SaveAsync(t2, default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t1.Id, "plan", Now, TimeSpan.Zero, true, 0.12m, null, "1"), default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t1.Id, "plan", Now, TimeSpan.Zero, true, 0.30m, null, "2"), default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t1.Id, "plan", Now, TimeSpan.Zero, false, null, "x", "3"), default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t2.Id, "plan", Now, TimeSpan.Zero, false, null, "x", "4"), default);

        var costs = await _store.AgentCostsAsync(default);

        Assert.Equal(new Dictionary<Guid, decimal> { [t1.Id] = 0.42m }, costs);
    }

    [Fact]
    public async Task Migrates_database_created_before_summary_column()
    {
        var path = Path.Combine(_dir, "old.db");
        var ticketId = Guid.NewGuid();
        await using (var db = new SymphonistDbContext(new DbContextOptionsBuilder<SymphonistDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261022035014_PendingSourceStatus");
            // The entity now has a Summary column the old schema lacks, so insert with raw SQL.
            await db.Database.OpenConnectionAsync();
            await using var cmd = (SqliteCommand)db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "INSERT INTO AgentRuns (TicketId, StageId, StartedAt, Duration, Succeeded, CostUsd, Error, RawLogPath) " +
                "VALUES ($t, 'plan', $s, $d, 1, NULL, NULL, 'old.json')";
            cmd.Parameters.AddWithValue("$t", ticketId);
            cmd.Parameters.AddWithValue("$s", new DateTimeOffsetToBinaryConverter().ConvertToProvider(Now));
            cmd.Parameters.AddWithValue("$d", "00:00:01");
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var runs = await new SqliteTicketStore(path).AgentRunsAsync(ticketId, default);

        var run = Assert.Single(runs);
        Assert.Null(run.Summary);
        Assert.Equal("old.json", run.RawLogPath);
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

    private static readonly DateTimeOffset Precise = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero).AddTicks(1230000);

    private static GateVerdict Verdict(RiskLevel risk, string? error = null) =>
        new(risk, "summary", ["r1", "r2"], ["c1"], ["pricing"], error);

    private static GateReview Review(Ticket t, string stage, DateTimeOffset round, RiskLevel risk, DateTimeOffset? at = null) =>
        new(t.Id, t.ProjectId, stage, round, at ?? round.AddMinutes(1), ReviewMode.Shadow, GatePolicy.Human, Verdict(risk));

    private static GateDecisionRecord Decision(Ticket t, string stage, DateTimeOffset round, GateDecision d, bool auto = false, DateTimeOffset? at = null) =>
        new(t.Id, t.ProjectId, stage, round, d, auto, at ?? round.AddMinutes(2));

    [Fact]
    public async Task Gate_review_round_trips_with_lists()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        var review = new GateReview(t.Id, "orders", "plan", Precise, Precise.AddMinutes(1), ReviewMode.Active, GatePolicy.AutoWhenLowRisk,
            new GateVerdict(RiskLevel.High, "sum", ["why 1", "why \"2\""], ["checked a"], ["pricing", "auth"], "boom"));
        await _store.AddGateReviewAsync(review, default);

        var loaded = Assert.Single(await _store.GateReviewsAsync(t.Id, default));

        Assert.Equal(review.Verdict.Reasons, loaded.Verdict.Reasons);
        Assert.Equal(review.Verdict.Checked, loaded.Verdict.Checked);
        Assert.Equal(review.Verdict.AreasTouched, loaded.Verdict.AreasTouched);
        Assert.Equal("boom", loaded.Verdict.Error);
        Assert.Equal(RiskLevel.High, loaded.Verdict.Risk);
        Assert.Equal("sum", loaded.Verdict.Summary);
        Assert.Equal(ReviewMode.Active, loaded.Mode);
        Assert.Equal(GatePolicy.AutoWhenLowRisk, loaded.Gate);
        Assert.Equal("plan", loaded.StageId);
        Assert.Equal("orders", loaded.ProjectId);
        Assert.Null(loaded.Decision);
        Assert.False(loaded.AutoDecided);
        Assert.Equal(Precise.UtcTicks, loaded.WaitingSince.UtcTicks);
        Assert.Equal(Precise.Offset, loaded.WaitingSince.Offset);
        Assert.Equal(review.At.UtcTicks, loaded.At.UtcTicks);

        var noError = Review(t, "plan", Now, RiskLevel.Low) with { Verdict = new GateVerdict(RiskLevel.Low, "s", [], ["x"], []) };
        await _store.AddGateReviewAsync(noError, default);
        var second = (await _store.GateReviewsAsync(t.Id, default)).Single(r => r.WaitingSince == Now);
        Assert.Null(second.Verdict.Error);
        Assert.Empty(second.Verdict.Reasons);
    }

    [Fact]
    public async Task Gate_reviews_are_newest_first()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        await _store.AddGateReviewAsync(Review(t, "plan", Now, RiskLevel.Low), default);
        await _store.AddGateReviewAsync(Review(t, "implement", Now.AddHours(1), RiskLevel.High), default);

        var reviews = await _store.GateReviewsAsync(t.Id, default);

        Assert.Equal(["implement", "plan"], reviews.Select(r => r.StageId));
    }

    [Fact]
    public async Task Decision_is_joined_to_its_round_only()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        var roundA = Now;
        var roundB = Now.AddHours(1);
        await _store.AddGateReviewAsync(Review(t, "plan", roundA, RiskLevel.Low), default);
        await _store.AddGateDecisionAsync(Decision(t, "plan", roundA, GateDecision.Approved), default);
        await _store.AddGateReviewAsync(Review(t, "plan", roundB, RiskLevel.High), default);

        var reviews = await _store.GateReviewsAsync(t.Id, default);

        Assert.Equal(GateDecision.Approved, reviews.Single(r => r.WaitingSince == roundA).Decision);
        Assert.Null(reviews.Single(r => r.WaitingSince == roundB).Decision);
    }

    [Fact]
    public async Task Latest_decision_of_a_round_wins_and_carries_auto_flag()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        await _store.AddGateReviewAsync(Review(t, "plan", Now, RiskLevel.Low), default);
        await _store.AddGateDecisionAsync(Decision(t, "plan", Now, GateDecision.SentBack, at: Now.AddMinutes(5)), default);
        await _store.AddGateDecisionAsync(Decision(t, "plan", Now, GateDecision.Approved, auto: true, at: Now.AddMinutes(9)), default);

        var review = Assert.Single(await _store.GateReviewsAsync(t.Id, default));

        Assert.Equal(GateDecision.Approved, review.Decision);
        Assert.True(review.AutoDecided);
    }

    [Fact]
    public async Task Decision_before_review_still_joins()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        await _store.AddGateDecisionAsync(Decision(t, "plan", Precise, GateDecision.SentBack), default);
        await _store.AddGateReviewAsync(Review(t, "plan", Precise, RiskLevel.High), default);

        var review = Assert.Single(await _store.GateReviewsAsync(t.Id, default));

        Assert.Equal(GateDecision.SentBack, review.Decision);
    }

    [Fact]
    public async Task Agreement_is_computed_per_project_stage()
    {
        var t1 = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        var t2 = NewTicket("B", "plan", StageStatus.AwaitingApproval);
        var other = NewTicket("C", "plan", StageStatus.AwaitingApproval);
        other.ProjectId = "billing";
        foreach (var t in new[] { t1, t2, other }) await _store.SaveAsync(t, default);
        // Human: low+approved (agree), low+sent back (false low). Auto: one approval. A decision with no matching review is not counted.
        await _store.AddGateReviewAsync(Review(t1, "plan", Now, RiskLevel.Low), default);
        await _store.AddGateDecisionAsync(Decision(t1, "plan", Now, GateDecision.Approved), default);
        await _store.AddGateReviewAsync(Review(t2, "plan", Now, RiskLevel.Low), default);
        await _store.AddGateDecisionAsync(Decision(t2, "plan", Now, GateDecision.SentBack), default);
        await _store.AddGateReviewAsync(Review(t1, "plan", Now.AddHours(1), RiskLevel.Low), default);
        await _store.AddGateDecisionAsync(Decision(t1, "plan", Now.AddHours(1), GateDecision.Approved, auto: true), default);
        await _store.AddGateReviewAsync(Review(t2, "plan", Now.AddHours(2), RiskLevel.High), default);
        await _store.AddGateDecisionAsync(Decision(t2, "plan", Now.AddHours(3), GateDecision.Approved), default);
        await _store.AddGateReviewAsync(Review(other, "plan", Now, RiskLevel.High), default);
        await _store.AddGateDecisionAsync(Decision(other, "plan", Now, GateDecision.SentBack), default);

        var orders = Assert.Single(await _store.ReviewAgreementAsync("orders", default));
        Assert.Equal(new StageAgreement("orders", "plan", 2, 1, 1, 1), orders);

        var all = await _store.ReviewAgreementAsync(null, default);
        Assert.Equal(["billing", "orders"], all.Select(a => a.ProjectId));
        Assert.Equal(new StageAgreement("billing", "plan", 1, 1, 0, 0), all[0]);
    }

    [Fact]
    public async Task Ticket_review_fields_and_run_flag_round_trip()
    {
        var t = NewTicket("A", "plan", StageStatus.AwaitingApproval);
        t.ReviewDue = true;
        t.Risk = RiskLevel.High;
        await _store.SaveAsync(t, default);

        var found = (await _store.FindAsync("orders", "A", default))!;
        Assert.True(found.ReviewDue);
        Assert.Equal(RiskLevel.High, found.Risk);

        (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead) = ("main", "abc123", "def456");
        t.MergeAcceptedAt = Now;
        t.MergeHolds = 2;
        await _store.SaveAsync(t, default);
        found = (await _store.FindAsync("orders", "A", default))!;
        Assert.Equal(("main", "abc123", "def456"), (found.ApprovedBranch, found.ApprovedHead, found.ApprovedBaseHead));
        Assert.Equal((Now, 2), (found.MergeAcceptedAt, found.MergeHolds));

        t.ReviewDue = false;
        t.Risk = null;
        await _store.SaveAsync(t, default);
        found = (await _store.FindAsync("orders", "A", default))!;
        Assert.False(found.ReviewDue);
        Assert.Null(found.Risk);

        var review = new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(3), true, 0.2m, null, "r.json", "s", IsReview: true);
        var normal = new AgentRunRecord(t.Id, "plan", Now.AddMinutes(1), TimeSpan.FromSeconds(3), true, 0.2m, null, "n.json");
        await _store.AddAgentRunAsync(review, default);
        await _store.AddAgentRunAsync(normal, default);
        Assert.Equal([review, normal], await _store.AgentRunsAsync(t.Id, default));
    }

    [Fact]
    public async Task Millisecond_waiting_since_and_offset_round_trip_exactly()
    {
        var withOffset = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(2)).AddTicks(1230000);
        foreach (var round in new[] { Precise, withOffset })
        {
            var t = NewTicket("K" + round.Offset.Hours, "plan", StageStatus.AwaitingApproval);
            t.WaitingSince = round;
            await _store.SaveAsync(t, default);
            await _store.AddGateReviewAsync(Review(t, "plan", round, RiskLevel.Low), default);
            await _store.AddGateDecisionAsync(Decision(t, "plan", round, GateDecision.Approved), default);

            var found = (await _store.FindAsync("orders", t.Key, default))!;
            Assert.Equal(round, found.WaitingSince);
            Assert.Equal(round.Ticks, found.WaitingSince!.Value.Ticks);
            Assert.Equal(round.Offset, found.WaitingSince.Value.Offset);

            var review = Assert.Single(await _store.GateReviewsAsync(t.Id, default));
            Assert.Equal(round.Ticks, review.WaitingSince.Ticks);
            Assert.Equal(round.Offset, review.WaitingSince.Offset);
            Assert.Equal(GateDecision.Approved, review.Decision);
        }
    }

    [Fact]
    public async Task Delete_removes_reviews_and_decisions()
    {
        var gone = NewTicket("A", "plan", StageStatus.Pending);
        var kept = NewTicket("B", "plan", StageStatus.Pending);
        await _store.SaveAsync(gone, default);
        await _store.SaveAsync(kept, default);
        foreach (var t in new[] { gone, kept })
        {
            await _store.AddGateReviewAsync(Review(t, "plan", Now, RiskLevel.Low), default);
            await _store.AddGateDecisionAsync(Decision(t, "plan", Now, GateDecision.Approved), default);
        }

        Assert.True(await _store.DeleteAsync("orders", "A", default));

        Assert.Empty(await _store.GateReviewsAsync(gone.Id, default));
        Assert.Equal(GateDecision.Approved, Assert.Single(await _store.GateReviewsAsync(kept.Id, default)).Decision);
        // The deleted ticket's decision is gone too: re-adding only a review leaves it undecided.
        await _store.AddGateReviewAsync(Review(gone, "plan", Now, RiskLevel.Low), default);
        Assert.Null(Assert.Single(await _store.GateReviewsAsync(gone.Id, default)).Decision);
        Assert.Equal(1, (await _store.ReviewAgreementAsync(null, default)).Sum(a => a.Decided));
    }

    private static EvidenceRecord Ev(Ticket t, DateTimeOffset at, bool issues = false, string? report = "{\"a\":1}") =>
        new(t.Id, t.ProjectId, "implement", Now, at, "abc123", report, issues, null, ["a.png", "b/c.png"], Posted: true);

    [Fact]
    public async Task Evidence_records_round_trip_newest_first()
    {
        var t = NewTicket("E1", "implement", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        await _store.AddEvidenceAsync(Ev(t, Now.AddMinutes(1)) with { Head = "old" }, default);
        await _store.AddEvidenceAsync(Ev(t, Now.AddMinutes(2)) with { Head = "new", Error = "boom" }, default);

        var all = await _store.EvidenceAsync(t.Id, default);

        Assert.Equal(["new", "old"], all.Select(x => x.Head));
        Assert.Equal(["a.png", "b/c.png"], all[0].Screenshots);
        Assert.Equal("{\"a\":1}", all[0].ReportJson);
        Assert.Equal((Now, Now.AddMinutes(2), "boom", true), (all[0].WaitingSince, all[0].At, all[0].Error, all[0].Posted));
    }

    [Fact]
    public async Task Evidence_issues_reports_latest_record_per_ticket()
    {
        var a = NewTicket("E2", "implement", StageStatus.AwaitingApproval);
        var b = NewTicket("E3", "implement", StageStatus.AwaitingApproval);
        await _store.SaveAsync(a, default);
        await _store.SaveAsync(b, default);
        await _store.AddEvidenceAsync(Ev(a, Now.AddMinutes(1), issues: true), default);
        await _store.AddEvidenceAsync(Ev(a, Now.AddMinutes(2), issues: false), default);
        await _store.AddEvidenceAsync(Ev(b, Now.AddMinutes(1), issues: true), default);

        var issues = await _store.EvidenceIssuesAsync(default);

        Assert.False(issues[a.Id]);
        Assert.True(issues[b.Id]);
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public async Task Delete_removes_evidence()
    {
        var gone = NewTicket("E4", "implement", StageStatus.Pending);
        var kept = NewTicket("E5", "implement", StageStatus.Pending);
        await _store.SaveAsync(gone, default);
        await _store.SaveAsync(kept, default);
        await _store.AddEvidenceAsync(Ev(gone, Now), default);
        await _store.AddEvidenceAsync(Ev(kept, Now), default);

        Assert.True(await _store.DeleteAsync("orders", "E4", default));

        Assert.Empty(await _store.EvidenceAsync(gone.Id, default));
        Assert.Single(await _store.EvidenceAsync(kept.Id, default));
    }

    [Fact]
    public async Task New_ticket_fields_persist()
    {
        var t = NewTicket("E6", "implement", StageStatus.AwaitingApproval);
        t.EvidenceDue = true;
        t.PrCommentsHandledAt = new DateTimeOffset(2026, 10, 21, 12, 0, 0, 123, TimeSpan.Zero);
        t.PrFeedbackJson = "[{\"id\":\"issue:1\"}]";
        await _store.SaveAsync(t, default);

        var found = await _store.FindAsync("orders", "E6", default);

        Assert.True(found!.EvidenceDue);
        Assert.Equal(t.PrCommentsHandledAt.Value.Ticks, found.PrCommentsHandledAt!.Value.Ticks);
        Assert.Equal(t.PrFeedbackJson, found.PrFeedbackJson);
    }

    [Fact]
    public async Task Agent_run_is_evidence_persists()
    {
        var t = NewTicket("E7", "implement", StageStatus.AwaitingApproval);
        await _store.SaveAsync(t, default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t.Id, "implement", Now, TimeSpan.FromSeconds(3), true, 0.2m, null, "r.json", "s", IsEvidence: true), default);
        await _store.AddAgentRunAsync(new AgentRunRecord(t.Id, "implement", Now.AddSeconds(5), TimeSpan.FromSeconds(3), true, null, null, "r2.json"), default);

        var runs = await _store.AgentRunsAsync(t.Id, default);

        Assert.Equal([true, false], runs.Select(r => r.IsEvidence));
        Assert.False(runs[0].IsReview);
    }

    [Fact]
    public async Task Provider_pauses_round_trip_and_upsert_by_key()
    {
        await _store.SaveProviderPauseAsync(new ProviderPause("claude-code", Now, Now.AddHours(2), null), default);
        await _store.SaveProviderPauseAsync(new ProviderPause("codex:chatgpt", Now, null, Now.AddMinutes(30)), default);
        await _store.SaveProviderPauseAsync(new ProviderPause("claude-code", Now, null, Now.AddMinutes(45)), default);

        var pauses = (await _store.ProviderPausesAsync(default)).OrderBy(p => p.Key).ToList();
        Assert.Equal(
            [new ProviderPause("claude-code", Now, null, Now.AddMinutes(45)),
             new ProviderPause("codex:chatgpt", Now, null, Now.AddMinutes(30))], pauses);

        await _store.RemoveProviderPauseAsync("claude-code", default);
        await _store.RemoveProviderPauseAsync("missing", default);
        Assert.Equal(["codex:chatgpt"], (await _store.ProviderPausesAsync(default)).Select(p => p.Key));
    }

    [Fact]
    public async Task Touches_filter_by_project_and_time_and_go_with_the_ticket()
    {
        var t = new Ticket { Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-9", StageId = "plan", Title = "x" };
        await _store.SaveAsync(t, default);
        await _store.AddTouchAsync(new TouchRecord(t.Id, "orders", "implement", TouchKind.SendBack, Now), default);
        await _store.AddTouchAsync(new TouchRecord(t.Id, "orders", "implement", TouchKind.Unblock, Now.AddDays(-3)), default);
        await _store.AddTouchAsync(new TouchRecord(Guid.NewGuid(), "billing", "plan", TouchKind.SendBack, Now), default);

        Assert.Equal(2, (await _store.TouchesAsync(null, Now.AddDays(-1), default)).Count);
        Assert.Equal(3, (await _store.TouchesAsync(null, Now.AddDays(-4), default)).Count);
        var orders = await _store.TouchesAsync("orders", Now.AddDays(-1), default);
        Assert.Equal([new TouchRecord(t.Id, "orders", "implement", TouchKind.SendBack, Now)], orders);

        Assert.True(await _store.DeleteAsync("orders", "ORD-9", default));
        Assert.Empty(await _store.TouchesAsync("orders", Now.AddDays(-4), default));
        Assert.Single(await _store.TouchesAsync(null, Now.AddDays(-4), default));
    }

    [Fact]
    public async Task Gate_decisions_and_recent_reviews_filter_by_project_and_time()
    {
        var t = new Ticket { Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-8", StageId = "plan", Title = "x" };
        await _store.SaveAsync(t, default);
        await _store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "plan", Now.AddDays(-9), GateDecision.Approved, false, Now.AddDays(-8)), default);
        await _store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "plan", Now.AddDays(-1), GateDecision.SentBack, false, Now), default);
        await _store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "billing", "plan", Now.AddDays(-1), GateDecision.Approved, true, Now), default);
        await _store.AddGateReviewAsync(Review(t, "plan", Now.AddDays(-40), RiskLevel.Low), default);
        await _store.AddGateReviewAsync(Review(t, "plan", Now.AddDays(-9), RiskLevel.High), default);

        Assert.Equal(2, (await _store.GateDecisionsAsync(null, Now.AddDays(-1), default)).Count);
        var orders = Assert.Single(await _store.GateDecisionsAsync("orders", Now.AddDays(-1), default));
        Assert.Equal(GateDecision.SentBack, orders.Decision);
        Assert.Equal(3, (await _store.GateDecisionsAsync(null, Now.AddDays(-10), default)).Count);

        var reviews = await _store.GateReviewsSinceAsync(null, Now.AddDays(-1), default);
        Assert.Equal([Now.AddDays(-9)], reviews.Select(r => r.WaitingSince)); // the 40-day-old round is more than 30 days before
    }
}
