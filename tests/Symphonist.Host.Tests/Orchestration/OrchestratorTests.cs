using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Orchestration;

[Collection(SymphonistHomeCollection.Name)]
public sealed class OrchestratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");

    private const string OrdersYaml = """
        id: orders
        name: Orders (Acme)
        tracker:
          type: jira
          site: https://acme.atlassian.net
          email: dev@example.com
          tokenSecret: jira-acme
          jql: assignee = currentUser() AND labels = Claude
        repo:
          type: bitbucket
          workspace: acme
          slug: orders
          localPath: ../acme-orders
          mainBranch: main
          productionBranch: production
          email: dev@example.com
          tokenSecret: bitbucket-acme
        build:
          type: bitbucket-pipelines
        agent:
          type: claude-code
        pipeline:
          - { id: triage,    kind: triage,    gate: human, entryStatuses: [Backlog], approvedWhenStatus: Planned }
          - { id: plan,      kind: plan,      gate: human, entryStatuses: [Planned], onEnterStatus: Planned, approvedWhenStatus: In Progress }
          - { id: implement, kind: implement, gate: human, onEnterStatus: In Progress, onGateStatus: In Review }
          - { id: build,     kind: build }
          - { id: release,   kind: release,   gate: human }
          - { id: deploy,    kind: deploy }
          - { id: done,      kind: done,      onEnterStatus: Done }
        """;

    private readonly string home = Path.Combine(Path.GetTempPath(), "symphonist-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeGitWorkspace git = new();
    private readonly FakeAgentRunner agent = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTracker tracker = new();
    private readonly FakeSourceControl scm = new();
    private readonly FakeBuildWatcher builds = new();
    private readonly ProjectRegistry registry;

    public OrchestratorTests()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "projects"));
        File.WriteAllText(Path.Combine(home, "projects", "orders.yaml"), OrdersYaml);
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        agent.OnRun = _ => FakeAgentRunner.Ok(FakeAgentRunner.Output(
            summary: "done", specPath: "docs/spec.md", planPath: "docs/plan.md", prTitle: "Export orders"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private Orchestrator Create(int maxRuns = 1) =>
        new(registry, store, new FakeIntegrationFactory(new ProjectIntegrations(tracker, scm, builds)),
            new StageExecutor(git, agent, store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), maxRuns), NullLogger<Orchestrator>.Instance);

    private void Source(string key, string status, params SourceComment[] comments) =>
        tracker.Tickets.Add(new SourceTicket(key, $"Title {key}", $"Description {key}", status, comments,
            new Uri($"https://acme.atlassian.net/browse/{key}")));

    private async Task Seed(string key, string stageId, StageStatus status, string sourceStatus, Action<Ticket>? with = null)
    {
        Source(key, sourceStatus);
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = $"Title {key}", StageId = stageId, Status = status,
            LastSourceStatus = sourceStatus, Branch = $"symphonist/{key}", CreatedAt = Now, UpdatedAt = Now,
            StageStartedAt = Now, WaitingSince = status == StageStatus.AwaitingApproval ? Now : null,
        };
        with?.Invoke(ticket);
        await store.SaveAsync(ticket, CancellationToken.None);
    }

    private static async Task TickIdle(Orchestrator o)
    {
        await o.TickAsync(CancellationToken.None);
        await o.WhenIdleAsync();
    }

    [Fact] public async Task New_ticket_is_picked_up_at_mapped_stage()
    {
        Source("ORD-1", "Planned");
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task; // keep the run in flight

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("plan", t.StageId);
        Assert.Equal("Planned", t.LastSourceStatus);
        Assert.Equal("Title ORD-1", t.Title);
        Assert.Equal("picked up at plan", store.EventMessages("ORD-1")[0]);

        await o.TickAsync(CancellationToken.None); // second tick never creates a duplicate
        Assert.Single(store.Tickets);
    }

    [Fact] public async Task Plan_runs_then_waits_for_gate_and_sets_no_status()
    {
        Source("ORD-1", "Planned");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Equal("docs/spec.md", t.SpecPath);
        Assert.Equal("docs/plan.md", t.PlanPath);
        Assert.Empty(tracker.Transitions);
        Assert.Single(agent.Requests);
        Assert.Contains("plan awaiting approval", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Jira_move_to_In_Progress_approves_plan()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        tracker.Move("ORD-1", "In Progress");

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("implement", t.StageId);
        Assert.Equal("In Progress", t.LastSourceStatus);
        Assert.Contains("advanced to implement", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions); // the card is already in the mapped status
    }

    [Fact] public async Task Missing_transition_records_event_and_keeps_moving()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending, "In Progress");
        tracker.OnTransition = (_, status) => status != "In Review";
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(StageStatus.AwaitingApproval, t.Status);
        Assert.Equal("In Progress", t.LastSourceStatus);
        Assert.Contains("no transition to 'In Review' in tracker", store.EventMessages("ORD-1"));

        await TickIdle(o); // nothing throws, nothing changes
        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.True(outcome.Changed);
        Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Transition_exception_records_event_and_continues()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending, "In Progress");
        tracker.TransitionError = new HttpRequestException("503");
        var o = Create();

        await TickIdle(o);

        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
        Assert.Contains("tracker transition to 'In Review' failed: 503", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Ticket_leaving_query_is_blocked_but_failed_query_blocks_nothing()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        await Seed("ORD-2", "done", StageStatus.Done, "Done");
        var o = Create();

        tracker.OnQuery = () => throw new HttpRequestException("Jira is down");
        await TickIdle(o);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
        Assert.Empty(store.Events);

        tracker.OnQuery = () => [];
        await TickIdle(o);
        var t = store.Single("ORD-1");
        Assert.Equal(StageStatus.Blocked, t.Status);
        Assert.Equal("no longer matches tracker query", t.BlockedReason);
        Assert.Equal(StageStatus.Done, store.Single("ORD-2").Status);
        Assert.Empty(store.EventMessages("ORD-2"));
    }

    [Fact] public async Task Respects_max_concurrent_agent_runs()
    {
        Source("ORD-1", "Planned"); Source("ORD-2", "Planned"); Source("ORD-3", "Planned");
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => release.Task;
        var o = Create(maxRuns: 1);

        await o.TickAsync(CancellationToken.None);
        await o.TickAsync(CancellationToken.None);

        Assert.Single(store.Tickets, t => t.Status == StageStatus.Running);
        Assert.Equal(2, store.Tickets.Count(t => t.Status == StageStatus.Pending));

        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        await o.WhenIdleAsync();
        Assert.Single(store.Tickets, t => t.Status == StageStatus.AwaitingApproval);

        await TickIdle(o);
        Assert.Equal(2, store.Tickets.Count(t => t.Status == StageStatus.AwaitingApproval));
        Assert.Equal(2, agent.Requests.Count);
    }

    [Fact] public async Task Send_back_via_cli_reruns_with_feedback()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();

        var outcome = await o.SendBackAsync("orders", "ORD-1", "use X", null, CancellationToken.None);

        Assert.True(outcome.Changed);
        Assert.Equal((StageStatus.Pending, "use X"), (store.Single("ORD-1").Status, store.Single("ORD-1").Feedback));
        Assert.Contains("sent back to plan", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions); // already "Planned"

        await TickIdle(o);
        Assert.Contains("use X", Assert.Single(agent.Prompts));
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Commands_on_unknown_ticket_throw()
    {
        var o = Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.ApproveAsync("orders", "NOPE-1", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.UnblockAsync("orders", "NOPE-1", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.SendBackAsync("nope", "ORD-1", "x", null, CancellationToken.None));
    }

    [Fact] public async Task Unblock_returns_ticket_to_pending()
    {
        await Seed("ORD-1", "build", StageStatus.Blocked, "In Review");
        var o = Create();

        var outcome = await o.UnblockAsync("orders", "ORD-1", CancellationToken.None);

        Assert.True(outcome.Changed);
        Assert.Equal(StageStatus.Pending, store.Single("ORD-1").Status);
        Assert.Contains("unblocked", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Source_move_back_sends_back_with_latest_own_comment_as_feedback()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, "In Review");
        tracker.Tickets.Clear();
        Source("ORD-1", "Planned",
            new SourceComment("Me", true, Now.AddMinutes(-5), "before the gate"),
            new SourceComment("Me", true, Now.AddMinutes(10), "rethink the API"),
            new SourceComment("Me", true, Now.AddMinutes(5), "older note"),
            new SourceComment("Ann", false, Now.AddMinutes(20), "not mine"));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("plan", t.StageId);
        Assert.Equal("rethink the API", t.Feedback);
        Assert.Contains("sent back to plan", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Backward_source_move_while_running_is_recorded_and_ignored()
    {
        await Seed("ORD-1", "build", StageStatus.Running, "In Review", t => t.MergeCommit = "merge-1");
        builds.Statuses["merge-1"] = BuildStatus.Running;
        tracker.Move("ORD-1", "Planned");
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("build", StageStatus.Running), (t.StageId, t.Status));
        Assert.Equal("Planned", t.LastSourceStatus); // saved even though the engine reported no change
        Assert.Contains("source moved to 'Planned' while build is Running; ignored", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Waiting_merge_event_is_recorded_once()
    {
        var pr = await scm.OpenPullRequestAsync("symphonist/ORD-1", "main", "t", "d", CancellationToken.None);
        await Seed("ORD-1", "implement", StageStatus.Approved, "In Review", t => t.PullRequestId = pr.Id);
        scm.MergeResults.Enqueue(new MergeResult(false, null, "needs 1 approval"));
        scm.MergeResults.Enqueue(new MergeResult(false, null, "needs 1 approval"));
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);
        Assert.Equal(["waiting to merge: needs 1 approval"], store.EventMessages("ORD-1"));
        Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);

        await TickIdle(o);
        var t = store.Single("ORD-1");
        Assert.Equal(("build", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Equal($"merge-{pr.Id}", t.MergeCommit);
    }

    [Fact] public async Task Failed_merge_retries_merge_not_the_agent_then_blocks()
    {
        await Seed("ORD-1", "implement", StageStatus.Approved, "In Review", t => t.PullRequestId = 42);
        scm.PullRequests[42] = FakeSourceControl.Pr(42, PullRequestState.Declined);
        var o = Create();

        await TickIdle(o);
        Assert.Equal((StageStatus.Approved, 1), (store.Single("ORD-1").Status, store.Single("ORD-1").Attempts));
        await TickIdle(o);
        await TickIdle(o);

        Assert.Equal(StageStatus.Blocked, store.Single("ORD-1").Status);
        Assert.Contains("pull request 42 was declined", store.Single("ORD-1").BlockedReason);
        Assert.Empty(agent.Requests);
    }

    [Fact] public async Task Completion_after_ticket_blocked_mid_run_keeps_block_and_work()
    {
        Source("ORD-1", "Planned");
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => release.Task;
        var o = Create();
        await o.TickAsync(CancellationToken.None);
        Assert.Equal(StageStatus.Running, store.Single("ORD-1").Status);

        tracker.OnQuery = () => [];
        await o.TickAsync(CancellationToken.None);
        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        await o.WhenIdleAsync();

        var t = store.Single("ORD-1");
        Assert.Equal(StageStatus.Blocked, t.Status);
        Assert.Equal("s.md", t.SpecPath);
        Assert.Contains("plan run finished while ticket was Blocked at plan; result not applied", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Agent_failure_counts_attempt_and_retries()
    {
        Source("ORD-1", "Backlog");
        agent.Results.Enqueue(FakeAgentRunner.Fail("is_error: true"));
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.Pending, 1), (t.Status, t.Attempts));
        Assert.Contains("triage failed (attempt 1/3): is_error: true", store.EventMessages("ORD-1"));

        await TickIdle(o);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Restart_recovery_resets_running_agent_tickets()
    {
        await Seed("ORD-1", "plan", StageStatus.Running, "Planned");
        await Seed("ORD-2", "build", StageStatus.Running, "In Review");
        var o = Create();

        await o.RecoverAsync(CancellationToken.None);

        Assert.Equal((StageStatus.Pending, 1), (store.Single("ORD-1").Status, store.Single("ORD-1").Attempts));
        Assert.Equal((StageStatus.Running, 0), (store.Single("ORD-2").Status, store.Single("ORD-2").Attempts));

        await TickIdle(o); // the reset ticket runs again
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Paused_project_is_skipped_and_pause_is_persisted()
    {
        Source("ORD-1", "Backlog");
        registry.SetPaused("orders", true);
        var reloaded = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        Assert.True(reloaded.IsPaused("orders"));
        var o = Create();

        await TickIdle(o);
        Assert.Empty(store.Tickets);

        registry.SetPaused("orders", false);
        await TickIdle(o);
        Assert.Single(store.Tickets);
    }

    [Fact] public void Registry_without_projects_dir_has_no_projects()
    {
        Directory.Delete(Path.Combine(home, "projects"), true);

        var empty = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);

        Assert.Empty(empty.Projects);
        Assert.Throws<KeyNotFoundException>(() => empty.Get("orders"));
        Assert.Equal("orders", registry.Get("orders").Id);
    }

    [Fact] public async Task Service_recovers_then_ticks_repeatedly_and_survives_errors()
    {
        store.ResetError = new InvalidOperationException("db locked");
        var queries = 0;
        tracker.OnQuery = () => Interlocked.Increment(ref queries) % 2 == 0 ? throw new HttpRequestException("flaky") : [];
        var options = new OrchestratorOptions(TimeSpan.FromMilliseconds(20), 1);
        var o = new Orchestrator(registry, store, new FakeIntegrationFactory(new ProjectIntegrations(tracker, scm, builds)),
            new StageExecutor(git, agent, store, time), time, options, NullLogger<Orchestrator>.Instance);
        using var service = new OrchestratorService(o, options, NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref queries) < 4 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await service.StopAsync(CancellationToken.None);

        Assert.True(queries >= 4);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact] public async Task Full_pipeline_end_to_end_with_fakes()
    {
        Source("ORD-1", "Backlog");
        builds.Script("merge-1", BuildStatus.Running, BuildStatus.Succeeded);
        builds.Script("merge-2", BuildStatus.NotFound, BuildStatus.Running, BuildStatus.Succeeded);
        var o = Create();

        async Task Until(StageStatus status, string stageId)
        {
            for (var i = 0; i < 10; i++)
            {
                var t = await store.FindAsync("orders", "ORD-1", CancellationToken.None);
                if (t?.StageId == stageId && t.Status == status) return;
                await TickIdle(o);
            }
            var last = store.Single("ORD-1");
            Assert.Fail($"expected {stageId}/{status}, got {last.StageId}/{last.Status} ({last.BlockedReason})");
        }

        async Task Approve()
        {
            var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
            Assert.True(outcome.Changed, outcome.Message);
        }

        await Until(StageStatus.AwaitingApproval, "triage");
        await Approve();
        await Until(StageStatus.AwaitingApproval, "plan");
        await Approve();
        await Until(StageStatus.AwaitingApproval, "implement");
        await Approve();
        await Until(StageStatus.AwaitingApproval, "release");
        await Approve();
        await Until(StageStatus.Done, "done");

        var final = store.Single("ORD-1");
        Assert.Equal(StageStatus.Done, final.Status);
        Assert.Equal(2, scm.Opened.Count);
        Assert.Equal(("symphonist/ORD-1", "main"), (scm.Opened[0].Source, scm.Opened[0].Destination));
        Assert.Equal(("main", "production"), (scm.Opened[1].Source, scm.Opened[1].Destination));
        Assert.Equal([1L, 2L], scm.MergeCalls);
        Assert.Equal(("merge-1", "merge-2"), (final.MergeCommit, final.ReleaseMergeCommit));
        Assert.Equal(["Planned", "In Progress", "In Review", "Done"], tracker.Transitions.Select(t => t.Status));
        Assert.Equal("Done", tracker.Tickets.Single().Status);
        Assert.Equal(3, agent.Requests.Count);
        Assert.Equal(3, store.AgentRuns.Count);
        Assert.Equal("advanced to done", store.EventMessages("ORD-1")[^1]);
    }
}
