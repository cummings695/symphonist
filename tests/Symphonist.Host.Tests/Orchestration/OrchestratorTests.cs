using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
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
        review: { mode: off }
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

    private static AgentRunResult StageResult => FakeAgentRunner.Ok(FakeAgentRunner.Output(
        summary: "done", specPath: "docs/spec.md", planPath: "docs/plan.md", prTitle: "Export orders"));

    public OrchestratorTests()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", home);
        Directory.CreateDirectory(Path.Combine(home, "projects"));
        File.WriteAllText(Path.Combine(home, "projects", "orders.yaml"), OrdersYaml);
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        agent.OnRun = _ => StageResult;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private FakeIntegrationFactory? factory;

    /// <param name="tickAfterCommands">Off by default here, so tests observe a command's own effect.</param>
    private Orchestrator Create(int maxRuns = 1, bool tickAfterCommands = false) =>
        new(registry, store, factory ??= new FakeIntegrationFactory(new ProjectIntegrations(tracker, scm, builds)),
            new StageExecutor(git, agent, store, time), Reviewer(), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), maxRuns, tickAfterCommands), NullLogger<Orchestrator>.Instance);

    private GateReviewer Reviewer() =>
        new(git, new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = agent }), store, time);

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

    [Fact] public async Task Failed_move_is_retried_after_secret_is_fixed()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();
        factory!.Error = new InvalidOperationException("secret 'jira-acme' not set");

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.Equal("In Progress", store.Single("ORD-1").TrackerRetryStatus);

        factory.Error = null; // the developer fixed the secret
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal([("ORD-1", "In Progress")], tracker.Transitions);
        Assert.Equal("In Progress", t.LastSourceStatus);
        Assert.Null(t.TrackerRetryStatus);
        Assert.Contains("tracker moved to 'In Progress' on retry", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Corrected_secret_is_picked_up_by_the_next_tick()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.TransitionError = new HttpRequestException("401 Unauthorized"); // present but wrong token
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.Equal("In Progress", store.Single("ORD-1").TrackerRetryStatus);

        var fixedTracker = new FakeTracker(); // the factory builds new clients for the corrected token
        fixedTracker.Tickets.AddRange(tracker.Tickets);
        factory!.Integrations = new ProjectIntegrations(fixedTracker, scm, builds);
        await o.TickAsync(CancellationToken.None);

        Assert.Equal([("ORD-1", "In Progress")], fixedTracker.Transitions);
        Assert.Null(store.Single("ORD-1").TrackerRetryStatus);
    }

    [Fact] public async Task Thrown_move_is_retried_once_per_tick_without_event_flood()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.OnTransition = (_, _) => tracker.Transitions.Count <= 4 ? throw new HttpRequestException("503") : true;
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None); // attempt 1
        for (var i = 0; i < 3; i++) await o.TickAsync(CancellationToken.None); // attempts 2-4

        Assert.Equal(4, tracker.Transitions.Count);
        Assert.Single(store.EventMessages("ORD-1"), m => m == "tracker transition to 'In Progress' failed: 503");
        Assert.Equal("In Progress", store.Single("ORD-1").TrackerRetryStatus);

        await o.TickAsync(CancellationToken.None); // attempt 5 succeeds

        var t = store.Single("ORD-1");
        Assert.Equal(5, tracker.Transitions.Count);
        Assert.Equal("In Progress", t.LastSourceStatus);
        Assert.Null(t.TrackerRetryStatus);
        Assert.Equal("tracker moved to 'In Progress' on retry", store.EventMessages("ORD-1")[^1]);
    }

    [Fact] public async Task Retry_failure_is_recorded_once_even_when_the_error_text_varies()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var attempts = 0;
        tracker.OnTransition = (_, _) => throw new HttpRequestException($"400 request {++attempts}");
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None); // attempt 1
        for (var i = 0; i < 4; i++) await o.TickAsync(CancellationToken.None); // attempts 2-5

        Assert.Equal(5, attempts);
        Assert.Single(store.EventMessages("ORD-1"),
            m => m.StartsWith("tracker transition to 'In Progress' failed", StringComparison.Ordinal));
    }

    [Fact] public async Task Developer_move_cancels_retry()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.TransitionError = new HttpRequestException("503");
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.Equal("In Progress", store.Single("ORD-1").TrackerRetryStatus);

        tracker.TransitionError = null;
        tracker.Move("ORD-1", "Backlog"); // the developer moved the card meanwhile
        await o.TickAsync(CancellationToken.None);
        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Null(t.TrackerRetryStatus);
        Assert.Equal("Backlog", t.LastSourceStatus);
        Assert.DoesNotContain(("ORD-1", "In Progress"), tracker.Transitions);
    }

    [Fact] public async Task Failed_final_move_of_a_done_ticket_is_retried()
    {
        await Seed("ORD-1", "done", StageStatus.Done, "In Review", t => t.TrackerRetryStatus = "Done");
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal([("ORD-1", "Done")], tracker.Transitions);
        Assert.Equal("Done", t.LastSourceStatus);
        Assert.Null(t.TrackerRetryStatus);
        Assert.Equal("tracker moved to 'Done' on retry", store.EventMessages("ORD-1")[^1]);
    }

    [Fact] public async Task Developer_move_of_a_done_card_cancels_its_retry()
    {
        await Seed("ORD-1", "done", StageStatus.Done, "In Review", t => t.TrackerRetryStatus = "Done");
        tracker.Move("ORD-1", "Released");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Empty(tracker.Transitions);
        Assert.Equal("Released", t.LastSourceStatus);
        Assert.Null(t.TrackerRetryStatus);
    }

    [Fact] public async Task Stage_change_without_a_move_cancels_the_retry()
    {
        // The move into plan failed, so the card is still in Backlog with a retry to Planned pending.
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Backlog", t => t.TrackerRetryStatus = "Planned");
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.SendBackAsync("orders", "ORD-1", "rethink", "triage", CancellationToken.None); // triage sets no status
        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("triage", t.StageId);
        Assert.Null(t.TrackerRetryStatus);
        Assert.Empty(tracker.Transitions);
    }

    [Fact] public async Task No_transition_is_not_retried()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.OnTransition = (_, _) => false;
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.Null(store.Single("ORD-1").TrackerRetryStatus);
        await o.TickAsync(CancellationToken.None);
        Assert.Single(tracker.Transitions);

        // A retried move that finds no transition is cleared, not retried again.
        await Seed("ORD-2", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.TransitionError = new HttpRequestException("503");
        await o.ApproveAsync("orders", "ORD-2", CancellationToken.None);
        Assert.Equal("In Progress", store.Single("ORD-2").TrackerRetryStatus);

        tracker.TransitionError = null;
        await o.TickAsync(CancellationToken.None);
        await o.TickAsync(CancellationToken.None);

        Assert.Null(store.Single("ORD-2").TrackerRetryStatus);
        Assert.Single(tracker.Transitions, x => x.Key == "ORD-2");
        Assert.Equal("no transition to 'In Progress' in tracker", store.EventMessages("ORD-2")[^1]);
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

    [Fact] public async Task Integration_and_query_errors_are_reported_as_project_problem_until_a_tick_succeeds()
    {
        Source("ORD-1", "Backlog");
        var o = Create();
        Assert.Null(o.ProblemOf("orders"));

        factory!.Error = new InvalidOperationException("secret 'jira-acme' not set; run: symphonist secret set jira-acme");
        await TickIdle(o);
        Assert.Equal("secret 'jira-acme' not set; run: symphonist secret set jira-acme", o.ProblemOf("orders"));
        Assert.Empty(store.Tickets);

        factory.Error = null;
        tracker.OnQuery = () => throw new HttpRequestException("401 Unauthorized");
        await TickIdle(o);
        Assert.Equal("tracker query failed: 401 Unauthorized", o.ProblemOf("orders"));

        tracker.OnQuery = null;
        await TickIdle(o);
        Assert.Null(o.ProblemOf("orders"));
        Assert.Single(store.Tickets);
    }

    [Fact] public async Task Problems_changed_raised_on_set_and_clear_only()
    {
        Source("ORD-1", "Backlog");
        var o = Create();
        var raised = 0;
        o.ProblemsChanged += () => raised++;

        factory!.Error = new InvalidOperationException("secret 'jira-acme' not set");
        await TickIdle(o);
        Assert.Equal(1, raised);

        await TickIdle(o); // same problem again
        Assert.Equal(1, raised);

        factory.Error = null;
        await TickIdle(o);
        Assert.Equal(2, raised);

        await TickIdle(o); // nothing to clear
        Assert.Equal(2, raised);
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

    [Fact] public async Task Changing_command_triggers_a_background_tick()
    {
        Assert.True(new OrchestratorOptions().TickAfterCommands);
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create(tickAfterCommands: true);

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        await o.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(agent.Requests); // the implement run started without waiting for the poll interval
        Assert.Equal(("implement", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact] public async Task Approve_with_missing_secret_takes_effect_records_skipped_sync_and_ticks()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create(tickAfterCommands: true);
        factory!.Error = new InvalidOperationException("secret 'jira-acme' not set");

        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        await o.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(outcome.Changed);
        Assert.Equal(("implement", StageStatus.Pending), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
        Assert.Equal(["advanced to implement", "tracker sync skipped: secret 'jira-acme' not set"],
            store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions);
        Assert.Equal("secret 'jira-acme' not set", o.ProblemOf("orders")); // the requested tick ran
    }

    [Fact] public async Task Approve_of_a_stage_the_ticket_has_left_changes_nothing()
    {
        await Seed("ORD-1", "release", StageStatus.AwaitingApproval, "In Review");
        var o = Create();

        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None, expectedStageId: "implement");

        Assert.False(outcome.Changed);
        Assert.Equal("implement is not awaiting approval", outcome.Message);
        Assert.Equal(("release", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
        Assert.Empty(store.Events);

        Assert.True((await o.ApproveAsync("orders", "ORD-1", CancellationToken.None, expectedStageId: "release")).Changed);
    }

    [Fact] public async Task Unchanged_command_does_not_tick()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending, "Planned");
        var queries = 0;
        tracker.OnQuery = () => { Interlocked.Increment(ref queries); return tracker.Tickets.ToList(); };
        var o = Create(tickAfterCommands: true);

        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        await o.WhenIdleAsync();

        Assert.False(outcome.Changed);
        Assert.Equal(0, queries);
    }

    [Fact] public async Task Commands_on_unknown_ticket_throw()
    {
        var o = Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.ApproveAsync("orders", "NOPE-1", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.UnblockAsync("orders", "NOPE-1", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => o.SendBackAsync("nope", "ORD-1", "x", null, CancellationToken.None));
    }

    [Fact] public async Task Forget_removes_the_ticket()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();

        Assert.Equal(ForgetResult.Forgotten, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));
        Assert.Empty(store.Tickets);
        Assert.Equal(ForgetResult.NotFound, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));
    }

    [Fact] public async Task Forget_refused_while_running()
    {
        Source("ORD-1", "Backlog");
        var o = Create();
        var release = new TaskCompletionSource<AgentRunResult>();
        agent.OnRunAsync = _ => release.Task;
        await o.TickAsync(CancellationToken.None); // starts the triage run, which blocks

        Assert.Equal(ForgetResult.Running, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));
        Assert.Single(store.Tickets);

        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output()));
        await o.WhenIdleAsync();
        Assert.Equal(ForgetResult.Forgotten, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));
    }

    [Fact] public async Task Forgotten_ticket_is_picked_up_again_if_still_matching()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        tracker.Tickets.Clear();
        Source("ORD-1", "Backlog");
        var oldId = store.Single("ORD-1").Id;
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.ForgetAsync("orders", "ORD-1", CancellationToken.None);
        await o.TickAsync(CancellationToken.None);

        var again = store.Single("ORD-1");
        Assert.NotEqual(oldId, again.Id);
        Assert.Equal("triage", again.StageId);
        Assert.Contains(store.EventMessages("ORD-1"), m => m.StartsWith("picked up at triage"));
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

    [Fact] public async Task Unblocked_build_gets_a_fresh_timeout_instead_of_failing_at_once()
    {
        await Seed("ORD-1", "build", StageStatus.Blocked, "In Review", t =>
        {
            t.MergeCommit = "merge-1";
            t.StageStartedAt = Now.AddHours(-3); // the build timeout (45 min) expired long ago
        });
        var o = Create();

        await o.UnblockAsync("orders", "ORD-1", CancellationToken.None);
        await TickIdle(o); // no pipeline yet, but the clock restarted at unblock

        Assert.Equal((StageStatus.Running, 0), (store.Single("ORD-1").Status, store.Single("ORD-1").Attempts));
    }

    [Fact] public async Task Source_move_back_sends_back_with_latest_own_comment_as_feedback()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, "In Review");
        tracker.Tickets.Clear();
        Source("ORD-1", "Planned",
            new SourceComment("Me", true, Now.AddMinutes(-5), "before the gate"),
            new SourceComment("Me", true, Now.AddMinutes(10), "rethink the API"),
            new SourceComment("Me", true, Now.AddMinutes(5), "older note"),
            new SourceComment("Ann", false, Now.AddMinutes(20), "not mine"),
            new SourceComment("Me", true, Now.AddMinutes(30), TrackerComments.Sign("PR ready for review: https://scm.example/pr/1")));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("plan", t.StageId);
        Assert.Equal("rethink the API", t.Feedback);
        Assert.Contains("sent back to plan", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Blocked_ticket_dragged_back_without_developer_comment_has_no_feedback()
    {
        await Seed("ORD-1", "implement", StageStatus.Blocked, "In Review");
        tracker.Tickets.Clear();
        Source("ORD-1", "Planned",
            new SourceComment("Me", true, Now.AddMinutes(-30), "an old note from before this stage"),
            new SourceComment("Me", true, Now.AddMinutes(5), TrackerComments.Sign("PR ready for review: https://scm.example/pr/1")));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal("plan", t.StageId);
        Assert.Null(t.Feedback);
    }

    private void Comments(string key, params SourceComment[] comments)
    {
        var index = tracker.Tickets.FindIndex(t => t.Key == key);
        tracker.Tickets[index] = tracker.Tickets[index] with { Comments = comments };
    }

    [Fact] public async Task Claude_comment_at_gate_reruns_stage_with_feedback()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude answers: A, B"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal(("triage", StageStatus.Running, "answers: A, B"), (t.StageId, t.Status, t.Feedback));
        Assert.Equal(Now.AddMinutes(5), t.ClaudeCommentsHandledAt);
        Assert.Contains("sent back to triage", store.EventMessages("ORD-1"));
        Assert.Contains("@claude comment: rerunning triage", store.EventMessages("ORD-1"));
        Assert.Contains("answers: A, B", Assert.Single(agent.Prompts)); // the rerun starts in the same tick
    }

    [Fact] public async Task Several_claude_comments_make_one_rerun()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        Comments("ORD-1",
            new SourceComment("Me", true, Now.AddMinutes(6), "@claude second"),
            new SourceComment("Me", true, Now.AddMinutes(5), "@claude first"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("triage", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Contains("first\n\nsecond", Assert.Single(agent.Prompts)); // the rerun uses up the feedback

        await TickIdle(o); // the same comments again
        Assert.Single(agent.Requests);
        Assert.Single(store.EventMessages("ORD-1"), m => m == "@claude comment: rerunning triage");
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Claude_comment_during_run_applies_at_the_gate()
    {
        Source("ORD-1", "Planned");
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => agent.Requests.Count == 1
            ? release.Task
            : Task.FromResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        var o = Create();
        await o.TickAsync(CancellationToken.None);
        Assert.Equal(StageStatus.Running, store.Single("ORD-1").Status);

        time.Advance(TimeSpan.FromMinutes(2));
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(1), "@claude also cover refunds"));
        await o.TickAsync(CancellationToken.None);
        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.Running, null), (t.Status, t.Feedback));

        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        await o.WhenIdleAsync();
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);

        time.Advance(TimeSpan.FromMinutes(2));
        await TickIdle(o);

        t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Equal(2, agent.Requests.Count);
        Assert.Contains("also cover refunds", agent.Prompts[1]);
        Assert.Contains("@claude comment: rerunning plan", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Claude_comment_from_earlier_stage_is_ignored()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(-5), "@claude answers: A, B"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Null(t.Feedback);
        Assert.Empty(store.EventMessages("ORD-1"));
        Assert.Empty(agent.Requests);
    }

    [Fact] public async Task Claude_comment_on_blocked_ticket_reruns()
    {
        await Seed("ORD-1", "triage", StageStatus.Blocked, "Backlog", t => (t.Attempts, t.BlockedReason) = (3, "boom"));
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude try the v2 API"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal(("triage", StageStatus.Running, 0), (t.StageId, t.Status, t.Attempts));
        Assert.Equal("try the v2 API", t.Feedback);
        Assert.Null(t.BlockedReason);
        Assert.Contains("@claude comment: rerunning triage", store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Claude_comment_without_details_reruns_with_placeholder_feedback()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        Assert.Equal("(no details given)", store.Single("ORD-1").Feedback);
    }

    [Fact] public async Task Plain_and_symphonist_comments_do_not_trigger()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        Comments("ORD-1",
            new SourceComment("Me", true, Now.AddMinutes(5), "thanks!"),
            new SourceComment("Me", true, Now.AddMinutes(6), TrackerComments.Sign("@claude questions for you")),
            new SourceComment("Ann", false, Now.AddMinutes(7), "@claude use v3"));
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("triage", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Null(t.Feedback);
        Assert.Empty(store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Status_move_wins_over_claude_comment_in_same_tick()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude answers: A, B"));
        tracker.Move("ORD-1", "Planned");
        time.Advance(TimeSpan.FromMinutes(10));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);
        await o.TickAsync(CancellationToken.None); // the comment predates plan, so it never reruns plan either

        var t = store.Single("ORD-1");
        Assert.Equal("plan", t.StageId);
        Assert.Contains("advanced to plan", store.EventMessages("ORD-1"));
        Assert.DoesNotContain(store.EventMessages("ORD-1"), m => m.StartsWith("@claude", StringComparison.Ordinal));
    }

    [Fact] public async Task Claude_comment_rerun_replaces_a_pending_retry_in_the_same_tick()
    {
        // The move into plan failed earlier, so the card is still in Backlog with a retry to Planned pending.
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Backlog", t => t.TrackerRetryStatus = "Planned");
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude also cover refunds"));
        time.Advance(TimeSpan.FromMinutes(10));
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;
        var o = Create();

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal([("ORD-1", "Planned")], tracker.Transitions); // the rerun's own move, attempted once
        Assert.Null(t.TrackerRetryStatus);
        Assert.Equal(["@claude comment: rerunning plan", "sent back to plan"], store.EventMessages("ORD-1")); // no retry
    }

    [Fact] public async Task Claude_comment_dated_after_host_clock_reruns_once()
    {
        await Seed("ORD-1", "triage", StageStatus.AwaitingApproval, "Backlog");
        // Jira's clock is ahead: the comment is later than the rerun's StageStartedAt, so only
        // ClaudeCommentsHandledAt keeps it from triggering again.
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude answers: A, B"));
        var o = Create();

        await TickIdle(o);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
        await TickIdle(o);

        Assert.Single(agent.Requests);
        Assert.Single(store.EventMessages("ORD-1"), m => m == "@claude comment: rerunning triage");
    }

    [Fact] public async Task Running_agent_ticket_without_a_run_is_retried()
    {
        await Seed("ORD-1", "plan", StageStatus.Running, "Planned");
        var o = Create();

        await o.TickAsync(CancellationToken.None);

        Assert.Equal((StageStatus.Pending, 1), (store.Single("ORD-1").Status, store.Single("ORD-1").Attempts));
        Assert.Contains("run lost; retrying", store.EventMessages("ORD-1"));

        await TickIdle(o);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Backward_source_move_while_running_is_applied_when_the_stage_finishes()
    {
        await Seed("ORD-1", "build", StageStatus.Running, "In Review", t => t.MergeCommit = "merge-1");
        builds.Statuses["merge-1"] = BuildStatus.Running;
        tracker.Tickets.Clear();
        Source("ORD-1", "Planned", new SourceComment("Me", true, Now.AddMinutes(5), "replan: wrong approach"));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("build", StageStatus.Running, "Planned"), (t.StageId, t.Status, t.PendingSourceStatus));
        Assert.Equal("Planned", t.LastSourceStatus);
        Assert.Contains("source moved to 'Planned' while build is Running; will apply when the stage finishes",
            store.EventMessages("ORD-1"));

        builds.Statuses["merge-1"] = BuildStatus.Succeeded;
        await o.TickAsync(CancellationToken.None);

        t = store.Single("ORD-1");
        Assert.Equal(("plan", "replan: wrong approach"), (t.StageId, t.Feedback));
        Assert.Null(t.PendingSourceStatus);
        Assert.Contains("applying source move to 'Planned': sent back to plan", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions); // the card is already in Planned
    }

    [Fact] public async Task Approval_move_during_agent_run_approves_when_the_run_finishes()
    {
        Source("ORD-1", "Planned");
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<AgentRunResult>();
        agent.OnRunAsync = _ => agent.Requests.Count == 1 ? release.Task : never.Task; // only the plan run finishes
        var o = Create();
        await o.TickAsync(CancellationToken.None);
        Assert.Equal(StageStatus.Running, store.Single("ORD-1").Status);

        tracker.Move("ORD-1", "In Progress");
        await o.TickAsync(CancellationToken.None);
        Assert.Contains("source moved to 'In Progress' while plan is Running; will apply when the stage finishes",
            store.EventMessages("ORD-1"));

        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        await o.WhenIdleAsync();

        var t = store.Single("ORD-1");
        Assert.Equal(("implement", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Contains("applying source move to 'In Progress': advanced to implement", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions);
        var d = Assert.Single(store.GateDecisions); // the developer's decision, with no review of its round
        Assert.Equal(("plan", GateDecision.Approved, false), (d.StageId, d.Decision, d.Auto));
    }

    [Fact] public async Task Approval_stored_during_runs_that_end_blocked_does_not_approve_after_unblock()
    {
        Source("ORD-1", "Planned");
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => agent.Requests.Count == 1 ? release.Task : Task.FromResult(FakeAgentRunner.Fail("boom"));
        var o = Create();
        await o.TickAsync(CancellationToken.None);         // first plan run starts and waits
        tracker.Move("ORD-1", "In Progress");
        await o.TickAsync(CancellationToken.None);         // approval stored
        release.SetResult(FakeAgentRunner.Fail("boom"));    // attempt 1 fails
        await o.WhenIdleAsync();
        await TickIdle(o);                                  // attempt 2 fails
        await TickIdle(o);                                  // attempt 3 fails -> Blocked
        Assert.Equal(StageStatus.Blocked, store.Single("ORD-1").Status);

        tracker.Move("ORD-1", "Planned");                   // developer takes the approval back
        await TickIdle(o);
        await o.UnblockAsync("orders", "ORD-1", CancellationToken.None);
        agent.OnRunAsync = null;                            // the next run succeeds
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Null(t.PendingSourceStatus);
    }

    [Fact] public async Task Restart_recovery_keeps_a_deferred_approval()
    {
        await Seed("ORD-1", "plan", StageStatus.Running, "In Progress", t => t.PendingSourceStatus = "In Progress");
        var o = Create();
        agent.OnRunAsync = r => agent.Requests.Count == 1
            ? Task.FromResult(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")))
            : new TaskCompletionSource<AgentRunResult>().Task;

        await o.RecoverAsync(CancellationToken.None);
        Assert.Equal("In Progress", store.Single("ORD-1").PendingSourceStatus);
        await o.TickAsync(CancellationToken.None);
        await o.WhenIdleAsync();

        Assert.Equal(("implement", StageStatus.Pending), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact] public async Task Status_at_pickup_is_not_an_approval()
    {
        Source("ORD-1", "Planned");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        Assert.Equal(("plan", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact] public async Task Card_moved_back_to_In_Progress_at_implement_gate_reruns_implement_with_feedback()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, "In Review");
        tracker.Tickets.Clear();
        Source("ORD-1", "In Progress", new SourceComment("Me", true, Now.AddMinutes(5), "add tests"));
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task;

        await o.TickAsync(CancellationToken.None);

        var t = store.Single("ORD-1");
        Assert.Equal(("implement", "add tests"), (t.StageId, t.Feedback));
        Assert.Contains("sent back to implement", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Transitions);
    }

    [Fact] public async Task Reopened_done_card_is_recorded()
    {
        await Seed("ORD-1", "done", StageStatus.Done, "Done");
        tracker.Move("ORD-1", "In Progress");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.Done, "In Progress"), (t.Status, t.LastSourceStatus));
        Assert.Equal(["card moved to 'In Progress' after the ticket was done; Symphonist does not reopen done tickets"],
            store.EventMessages("ORD-1"));
    }

    [Fact] public async Task Release_with_nothing_to_release_waits_at_gate_then_deploy_watches_production_head()
    {
        await Seed("ORD-1", "release", StageStatus.Pending, "In Review", t => t.MergeCommit = "merge-1");
        scm.OpenError = new NoChangesToPullException("There are no changes to be pulled");
        scm.BranchHeads["production"] = "prod-head";
        scm.OnBranchContains = (branch, other) => (branch, other) == ("production", "main");
        builds.Statuses["prod-head"] = BuildStatus.Succeeded;
        var o = Create();

        await TickIdle(o);
        Assert.Equal(("release", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
        Assert.Contains("already in production: production already contains main (at prod-head)", store.EventMessages("ORD-1"));

        await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        await TickIdle(o); // post-approval: nothing to merge
        await TickIdle(o); // deploy sees the production head's pipeline

        Assert.Equal(StageStatus.Done, store.Single("ORD-1").Status);
        Assert.Equal([("prod-head", BuildPurpose.Deploy)], builds.Calls);
        Assert.Empty(scm.MergeCalls);
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
            new StageExecutor(git, agent, store, time), Reviewer(), time, options, NullLogger<Orchestrator>.Instance);
        using var service = new OrchestratorService(o, options, NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref queries) < 4 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await service.StopAsync(CancellationToken.None);

        Assert.True(queries >= 4);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact] public async Task Service_stops_cleanly_when_a_tick_fails_after_shutdown_began()
    {
        var queries = 0;
        var querying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        tracker.OnQuery = () =>
        {
            if (Interlocked.Increment(ref queries) < 2) return [];
            querying.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10)); // the second tick's query is in progress when shutdown begins
            throw new HttpRequestException("connection reset by shutdown");
        };
        var options = new OrchestratorOptions(TimeSpan.FromMilliseconds(20), 1);
        var o = new Orchestrator(registry, store, new FakeIntegrationFactory(new ProjectIntegrations(tracker, scm, builds)),
            new StageExecutor(git, agent, store, time), Reviewer(), time, options, NullLogger<Orchestrator>.Instance);
        using var service = new OrchestratorService(o, options, NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await querying.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = service.StopAsync(CancellationToken.None); // cancels the stopping token before awaiting
        release.Set();
        await stopping;

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully, service.ExecuteTask.Exception?.ToString());
    }

    [Fact] public async Task Service_stop_cancels_in_flight_runs_and_waits_for_their_cleanup()
    {
        Source("ORD-1", "Planned");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = false;
        agent.OnRunCancellableAsync = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return FakeAgentRunner.Fail("not cancelled");
            }
            finally
            {
                await Task.Delay(50, CancellationToken.None); // like ClaudeCodeRunner's docker rm -f
                Volatile.Write(ref cleanedUp, true);
            }
        };
        var options = new OrchestratorOptions(TimeSpan.FromMinutes(10), 1);
        var o = new Orchestrator(registry, store, new FakeIntegrationFactory(new ProjectIntegrations(tracker, scm, builds)),
            new StageExecutor(git, agent, store, time), Reviewer(), time, options, NullLogger<Orchestrator>.Instance);
        using var service = new OrchestratorService(o, options, NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref cleanedUp), "StopAsync returned before the agent run was cancelled and cleaned up");
        Assert.Equal(StageStatus.Running, store.Single("ORD-1").Status); // reset to Pending by recovery at next start
        await o.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
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

    // ---- Gate reviews ----

    /// <summary>Rewrites the project with a review mode, a risk area, and the given stage gates.</summary>
    private void Configure(string mode, params (string Stage, string Gate)[] gates)
    {
        var yaml = OrdersYaml.Replace("review: { mode: off }",
            $"review: {{ mode: {mode} }}\nriskAreas:\n  - {{ name: pricing, description: Prices and tax, paths: [\"src/Pricing/**\"] }}");
        foreach (var (stage, gate) in gates)
            yaml = Regex.Replace(yaml, $@"(\{{ id: {stage},\s+kind: \w+,\s+gate: )\w+", "${1}" + gate);
        File.WriteAllText(Path.Combine(home, "projects", "orders.yaml"), yaml);
        registry.Reload();
        Assert.Equal(mode, registry.Get("orders").Review.Mode.ToString(), ignoreCase: true);
    }

    /// <summary>Review runs answer with <paramref name="review"/>; stage runs succeed as usual.</summary>
    private void AnswerReviews(Func<AgentRunResult> review) =>
        agent.OnRun = r => FakeAgentRunner.IsReview(r) ? review() : StageResult;

    private int ReviewRequests => agent.Requests.Count(FakeAgentRunner.IsReview);

    private static AgentRunResult Low() =>
        FakeAgentRunner.Review("low", "small change", checkedItems: ["spec matches the ticket"]);

    private IReadOnlyList<string> AutoApprovalComments =>
        tracker.Comments.Select(c => c.Markdown).Where(m => m.StartsWith("Auto-approved", StringComparison.Ordinal)).ToList();

    private Task SeedWaiting(string key, string stageId, string sourceStatus, Action<Ticket>? with = null) =>
        Seed(key, stageId, StageStatus.AwaitingApproval, sourceStatus, t =>
        {
            t.ReviewDue = true;
            t.SpecPath = "docs/spec.md";
            t.PlanPath = "docs/plan.md";
            with?.Invoke(t);
        });

    [Fact] public async Task Low_risk_auto_gate_is_approved_with_comment()
    {
        Configure("active", ("plan", "auto"));
        AnswerReviews(Low);
        var autoCommentsAtMove = -1;
        tracker.OnTransition = (_, _) => { autoCommentsAtMove = AutoApprovalComments.Count; return true; };
        Source("ORD-1", "Planned");
        var o = Create();

        await TickIdle(o); // plan run: waits at the gate with a review owed
        var waiting = store.Single("ORD-1");
        Assert.Equal((StageStatus.AwaitingApproval, true), (waiting.Status, waiting.ReviewDue));
        await TickIdle(o); // review run: low

        var t = store.Single("ORD-1");
        // The plan's low verdict is recorded; implement has no badge until its own review.
        Assert.Equal(("implement", StageStatus.Pending, (RiskLevel?)null), (t.StageId, t.Status, t.Risk));
        Assert.Equal(RiskLevel.Low, Assert.Single(store.GateReviews).Verdict.Risk);
        var review = Assert.Single(agent.Requests, FakeAgentRunner.IsReview);
        Assert.True(review.ReadOnlyWorkspace);
        var comment = tracker.Comments[^1]; // after the plan stage's own comment
        Assert.Equal("ORD-1", comment.Key);
        Assert.StartsWith("Auto-approved plan: low risk.", comment.Markdown);
        Assert.Contains("- spec matches the ticket", comment.Markdown);
        Assert.True(TrackerComments.IsSymphonist(comment.Markdown));
        Assert.Single(AutoApprovalComments);
        Assert.Equal([("ORD-1", "In Progress")], tracker.Transitions);
        Assert.Equal(1, autoCommentsAtMove); // posted before the approval moved the card
        var decision = Assert.Single(store.GateDecisions);
        Assert.Equal(("plan", waiting.WaitingSince!.Value, GateDecision.Approved, true),
            (decision.StageId, decision.WaitingSince, decision.Decision, decision.Auto));
        var stored = Assert.Single(store.GateReviews);
        Assert.Equal((ReviewMode.Active, GatePolicy.AutoWhenLowRisk, RiskLevel.Low, waiting.WaitingSince.Value),
            (stored.Mode, stored.Gate, stored.Verdict.Risk, stored.WaitingSince));
        Assert.Equal(["picked up at plan", "plan awaiting approval", "reviewing plan", "auto-approved plan: low risk"],
            store.EventMessages("ORD-1"));
    }

    [Fact] public async Task High_risk_auto_gate_waits_on_you()
    {
        Configure("active", ("plan", "auto"));
        AnswerReviews(() => FakeAgentRunner.Review("high", "touches tax", reasons: ["changes the tax rule"]));
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval, RiskLevel.High, false), (t.StageId, t.Status, t.Risk, t.ReviewDue));
        Assert.Contains("plan needs you: high risk — changes the tax rule", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Comments);
        Assert.Empty(store.GateDecisions);
        Assert.Equal(1, ReviewRequests);
    }

    [Fact] public async Task Shadow_mode_records_verdict_and_never_approves()
    {
        Configure("shadow", ("plan", "auto"));
        AnswerReviews(Low);
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.Low), (t.Status, t.Risk));
        Assert.Contains("reviewer (shadow): low risk — small change", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Comments);

        Assert.True((await o.ApproveAsync("orders", "ORD-1", CancellationToken.None)).Changed);

        var review = (await store.GateReviewsAsync(t.Id, CancellationToken.None))[0];
        Assert.Equal((GateDecision.Approved, false), (review.Decision, review.AutoDecided));
        var agreement = Assert.Single(await store.ReviewAgreementAsync("orders", CancellationToken.None));
        Assert.Equal(("plan", 1, 1), (agreement.StageId, agreement.Decided, agreement.Agreed));
    }

    [Fact] public async Task Human_decision_during_review_wins_and_still_counts()
    {
        Configure("active", ("plan", "auto"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => { started.TrySetResult(); return release.Task; };
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();
        await o.TickAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the review is running, blocked

        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None);
        Assert.True(outcome.Changed);
        Assert.Equal(("implement", StageStatus.Pending), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));

        release.SetResult(Low());
        await o.WhenIdleAsync();

        var t = store.Single("ORD-1");
        Assert.Equal(("implement", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Contains("review of plan finished after the gate was decided; not applied", store.EventMessages("ORD-1"));
        Assert.Empty(tracker.Comments);
        var review = Assert.Single(await store.GateReviewsAsync(t.Id, CancellationToken.None));
        Assert.Equal((RiskLevel.Low, GateDecision.Approved, false), (review.Verdict.Risk, review.Decision, review.AutoDecided));
        Assert.Equal(1, Assert.Single(await store.ReviewAgreementAsync("orders", CancellationToken.None)).Agreed);
    }

    [Fact] public async Task Review_failure_fails_closed_without_retry()
    {
        Configure("active", ("plan", "auto"));
        AnswerReviews(() => FakeAgentRunner.Fail("timed out"));
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.High, false), (t.Status, t.Risk, t.ReviewDue));
        Assert.Equal(["reviewing plan", "reviewer failed: timed out; waiting on you"], store.EventMessages("ORD-1"));
        Assert.Equal("timed out", Assert.Single(store.GateReviews).Verdict.Error);

        await TickIdle(o);
        await TickIdle(o);
        await TickIdle(o);
        Assert.Equal(1, ReviewRequests);
        Assert.Empty(tracker.Comments);
    }

    [Fact] public async Task Auto_approval_held_when_comment_fails()
    {
        Configure("active", ("plan", "auto"));
        AnswerReviews(Low);
        tracker.CommentError = new HttpRequestException("jira is down");
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", StageStatus.AwaitingApproval, RiskLevel.Low, false), (t.StageId, t.Status, t.Risk, t.ReviewDue));
        Assert.Contains("auto-approval held: could not post comment: jira is down; waiting on you", store.EventMessages("ORD-1"));
        Assert.Empty(store.GateDecisions);
        Assert.Empty(tracker.Transitions);
    }

    [Theory]
    [InlineData("head000", true)]
    [InlineData("pushed-later", false)]
    [InlineData(null, false)] // the head cannot be looked up
    public async Task Implement_gate_auto_approves_only_the_reviewed_head(string? currentHead, bool approved)
    {
        Configure("active", ("implement", "auto"));
        AnswerReviews(Low);
        git.ChangedFiles = ["src/Export/Csv.cs"];
        if (currentHead is not null) scm.BranchHeads["symphonist/ORD-1"] = currentHead;
        scm.AddOpen(7, "symphonist/ORD-1");
        await SeedWaiting("ORD-1", "implement", "In Review", t => t.PullRequestId = 7);
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(approved ? StageStatus.Approved : StageStatus.AwaitingApproval, t.Status);
        Assert.Equal(approved ? RiskLevel.Low : RiskLevel.High, t.Risk); // a low verdict on an old commit is not low
        Assert.Equal(approved ? 1 : 0, AutoApprovalComments.Count);
        Assert.Equal(approved ? 1 : 0, store.GateDecisions.Count);
        var reasons = Assert.Single(store.GateReviews).Verdict.Reasons;
        if (approved)
        {
            Assert.Equal(("symphonist/ORD-1", "head000", null), (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead));
            await TickIdle(o); // the head is unchanged: merges as before
            Assert.Equal([7L], scm.MergeCalls);
            Assert.Equal("build", store.Single("ORD-1").StageId);
        }
        if (currentHead == "pushed-later")
        {
            Assert.Equal("reviewed head000 but symphonist/ORD-1 is now pushed-later", reasons[0]);
            Assert.Equal(["reviewing implement", "implement needs you: high risk — reviewed head000 but symphonist/ORD-1 is now pushed-later"],
                store.EventMessages("ORD-1"));
        }
        if (currentHead is null)
        {
            Assert.StartsWith("could not confirm the reviewed commit of symphonist/ORD-1", reasons[0]);
            Assert.Equal(2, store.EventMessages("ORD-1").Count);
            Assert.StartsWith("implement needs you: high risk — could not confirm the reviewed commit of symphonist/ORD-1",
                store.EventMessages("ORD-1")[^1]);
        }
    }

    [Theory]
    [InlineData("pushed-later", "merge held: symphonist/ORD-1 moved since auto-approval (head000 → pushed-later)")]
    [InlineData(null, "merge held: could not check symphonist/ORD-1: branch symphonist/ORD-1 not found")]
    public async Task Auto_approved_implement_waits_again_when_the_branch_moves_before_the_merge(string? newHead, string message)
    {
        Configure("active", ("implement", "auto"));
        AnswerReviews(Low);
        git.ChangedFiles = ["src/Export/Csv.cs"];
        scm.BranchHeads["symphonist/ORD-1"] = "head000";
        await SeedWaiting("ORD-1", "implement", "In Review", t => t.PullRequestId = 7);
        scm.AddOpen(7, "symphonist/ORD-1");
        var o = Create();
        await TickIdle(o);
        var approved = store.Single("ORD-1");
        Assert.Equal(StageStatus.Approved, approved.Status);

        time.Advance(TimeSpan.FromMinutes(1));
        if (newHead is null) scm.BranchHeads.Remove("symphonist/ORD-1");
        else scm.BranchHeads["symphonist/ORD-1"] = newHead;
        await TickIdle(o); // the merge tick

        var t = store.Single("ORD-1");
        Assert.Empty(scm.MergeCalls);
        Assert.Equal(("implement", StageStatus.AwaitingApproval, true), (t.StageId, t.Status, t.ReviewDue));
        Assert.NotEqual(approved.WaitingSince, t.WaitingSince); // a new round
        Assert.Null(t.ApprovedHead);
        Assert.Equal(message, store.EventMessages("ORD-1")[^1]);
    }

    [Theory]
    [InlineData("head000", "mallory/orders", "merge held: pull request 7 is not from acme/orders (source: mallory/orders)")]
    [InlineData("head000", null, "merge held: pull request 7 is not from acme/orders (source: unknown)")]
    [InlineData("evil000", FakeSourceControl.Repository, "merge held: pull request 7 would merge evil000, not the reviewed head000")]
    [InlineData(null, FakeSourceControl.Repository, "merge held: pull request 7 would merge an unknown commit, not the reviewed head000")]
    public async Task Auto_approved_merge_waits_again_when_the_pull_request_would_not_merge_the_reviewed_commit(
        string? sourceCommit, string? repository, string message)
    {
        // E.g. a fork's pull request with the same branch names: our branch is unchanged, but it is not what would merge.
        await AutoApproveAndMoveHeads("implement", move: false);
        scm.SourceBranches.Remove(7);
        scm.PullRequests[7] = FakeSourceControl.Pr(7, sourceCommit: sourceCommit, repository: repository);
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Empty(scm.MergeCalls);
        Assert.Equal(("implement", StageStatus.AwaitingApproval, true, null), (t.StageId, t.Status, t.ReviewDue, t.ApprovedHead));
        Assert.Equal(message, store.EventMessages("ORD-1")[^1]);
    }

    [Theory]
    [InlineData("head000", "mallory/orders", "pull request 7 is not from acme/orders (source: mallory/orders)")]
    [InlineData("evil000", FakeSourceControl.Repository, "pull request 7 would merge evil000, not the reviewed head000")]
    public async Task Low_verdict_is_high_when_the_pull_request_would_not_merge_the_reviewed_commit(string sourceCommit,
        string repository, string reason)
    {
        Configure("active", ("implement", "auto"));
        AnswerReviews(Low);
        git.ChangedFiles = ["src/Export/Csv.cs"];
        scm.BranchHeads["symphonist/ORD-1"] = "head000";
        scm.PullRequests[7] = FakeSourceControl.Pr(7, sourceCommit: sourceCommit, repository: repository);
        await SeedWaiting("ORD-1", "implement", "In Review", t => t.PullRequestId = 7);
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.High), (t.Status, t.Risk));
        Assert.Equal(reason, Assert.Single(store.GateReviews).Verdict.Reasons[0]);
        Assert.Empty(AutoApprovalComments);
        Assert.Empty(store.GateDecisions);
    }

    [Fact] public async Task Low_verdict_is_high_when_the_pull_request_cannot_be_read()
    {
        Configure("active", ("implement", "auto"));
        AnswerReviews(Low);
        git.ChangedFiles = ["src/Export/Csv.cs"];
        scm.BranchHeads["symphonist/ORD-1"] = "head000";
        await SeedWaiting("ORD-1", "implement", "In Review", t => t.PullRequestId = 7); // PR 7 unknown to the host
        var o = Create();

        await TickIdle(o);

        Assert.Equal(RiskLevel.High, store.Single("ORD-1").Risk);
        Assert.StartsWith("could not confirm what pull request 7 would merge", Assert.Single(store.GateReviews).Verdict.Reasons[0]);
        Assert.Empty(AutoApprovalComments);
    }

    [Theory]
    [InlineData("main", "main001", "merge held: main moved since auto-approval (main000 → main001)")]
    [InlineData("production", "prod001", "merge held: production moved since auto-approval (prod000 → prod001)")]
    public async Task Auto_approved_release_waits_again_when_main_or_production_moves_before_the_merge(string branch,
        string newHead, string message)
    {
        Configure("active", ("release", "auto"));
        AnswerReviews(Low);
        await Seed("ORD-1", "release", StageStatus.Pending, "In Review", t => t.MergeCommit = "build-1");
        scm.BranchHeads["main"] = "main000";
        scm.BranchHeads["production"] = "prod000";
        var o = Create();
        await TickIdle(o); // release PR opened
        await TickIdle(o); // review: low -> approved
        var approved = store.Single("ORD-1");
        Assert.Equal(StageStatus.Approved, approved.Status);
        Assert.Equal(("main", "main000", "prod000"), (approved.ApprovedBranch, approved.ApprovedHead, approved.ApprovedBaseHead));

        scm.BranchHeads[branch] = newHead; // e.g. another ticket's merge into main
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Empty(scm.MergeCalls);
        Assert.Equal(("release", StageStatus.AwaitingApproval, true), (t.StageId, t.Status, t.ReviewDue));
        Assert.NotEqual(approved.WaitingSince, t.WaitingSince);
        Assert.Equal(message, store.EventMessages("ORD-1")[^1]);

        await TickIdle(o); // the new round is reviewed against the new heads
        Assert.Equal(2, ReviewRequests);
    }

    [Fact] public async Task Third_merge_hold_waits_on_the_developer_without_another_review()
    {
        // A busy main: every auto-approved release is overtaken by another merge before its own.
        Configure("active", ("release", "auto"));
        AnswerReviews(Low);
        await Seed("ORD-1", "release", StageStatus.Pending, "In Review", t => t.MergeCommit = "build-1");
        scm.BranchHeads["main"] = "main000";
        scm.BranchHeads["production"] = "prod000";
        var o = Create();
        await TickIdle(o); // release PR opened
        await TickIdle(o); // review: low -> approved

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);
            scm.BranchHeads["main"] = git.RemoteCommits["refs/remotes/origin/main"] = $"main00{i}";
            await TickIdle(o); // held
            await TickIdle(o); // reviewed again (and auto-approved), unless the hold went to the developer
        }

        await TickIdle(o);
        var t = store.Single("ORD-1");
        Assert.Empty(scm.MergeCalls);
        Assert.Equal((StageStatus.AwaitingApproval, false, 3, null), (t.Status, t.ReviewDue, t.MergeHolds, t.Risk));
        Assert.Equal(3, ReviewRequests);
        Assert.Equal(3, AutoApprovalComments.Count);
        Assert.Equal("merge held: main moved since auto-approval (main002 → main003); main keeps moving; waiting on you",
            store.EventMessages("ORD-1")[^1]);

        Assert.True((await o.ApproveAsync("orders", "ORD-1", CancellationToken.None)).Changed); // the developer decides
        await TickIdle(o);
        Assert.Single(scm.MergeCalls);
        Assert.Equal(("deploy", 0), (store.Single("ORD-1").StageId, store.Single("ORD-1").MergeHolds));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("implement")]
    public async Task Auto_approved_merge_already_done_on_the_host_completes_without_a_head_check(string stageId)
    {
        await AutoApproveAndMoveHeads(stageId);
        var pr = stageId == "release" ? store.Single("ORD-1").ReleasePullRequestId!.Value : 7;
        scm.PullRequests[pr] = FakeSourceControl.Pr(pr, PullRequestState.Merged, "merged-by-hand"); // and the heads moved
        var o = Create();

        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Empty(scm.MergeCalls);
        Assert.Equal(stageId == "release" ? "deploy" : "build", t.StageId);
        Assert.DoesNotContain(store.EventMessages("ORD-1"), m => m.StartsWith("merge held", StringComparison.Ordinal));
    }

    [Fact] public async Task Accepted_merge_that_never_lands_is_not_merged_unchecked_after_a_push()
    {
        await AutoApproveAndMoveHeads("implement", move: false);
        scm.MergeResults.Enqueue(new MergeResult(false, null, "merge in progress", Accepted: true));
        var o = Create();

        await TickIdle(o); // the merge is requested and accepted (202), but it never lands
        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.Approved, "head000", Now), (t.Status, t.ApprovedHead, t.MergeAcceptedAt));

        scm.BranchHeads["symphonist/ORD-1"] = "pushed"; // an unreviewed commit
        time.Advance(TimeSpan.FromMinutes(5));
        await TickIdle(o);
        await TickIdle(o);
        Assert.Equal([7L], scm.MergeCalls); // not requested again within the wait
        Assert.Single(store.EventMessages("ORD-1"), m => m == "waiting for the host to finish merging");
        Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);

        time.Advance(TimeSpan.FromMinutes(6));
        await TickIdle(o);

        t = store.Single("ORD-1");
        Assert.Equal([7L], scm.MergeCalls);
        Assert.Equal((StageStatus.AwaitingApproval, null, null), (t.Status, t.ApprovedHead, t.MergeAcceptedAt));
        Assert.Equal("merge held: symphonist/ORD-1 moved since auto-approval (head000 → pushed)", store.EventMessages("ORD-1")[^1]);
    }

    [Fact] public async Task Accepted_merge_that_lands_completes_without_a_head_check()
    {
        await AutoApproveAndMoveHeads("release", move: false);
        var pr = store.Single("ORD-1").ReleasePullRequestId!.Value;
        scm.MergeResults.Enqueue(new MergeResult(false, null, "merge in progress", Accepted: true));
        var o = Create();
        await TickIdle(o); // accepted (202)

        scm.PullRequests[pr] = FakeSourceControl.Pr(pr, PullRequestState.Merged, "merged-by-hand");
        scm.BranchHeads["production"] = "prod-merged"; // moved by the merge itself
        await TickIdle(o);

        Assert.Equal([pr], scm.MergeCalls);
        Assert.Equal("deploy", store.Single("ORD-1").StageId);
        Assert.DoesNotContain(store.EventMessages("ORD-1"), m => m.StartsWith("merge held", StringComparison.Ordinal));
    }

    [Fact] public async Task Accepted_merge_still_open_after_the_wait_is_requested_again_through_the_head_check()
    {
        await AutoApproveAndMoveHeads("implement", move: false);
        scm.MergeResults.Enqueue(new MergeResult(false, null, "merge in progress", Accepted: true));
        var o = Create();
        await TickIdle(o); // accepted (202), never lands

        time.Advance(MergeAcceptedWaitPlus);
        await TickIdle(o); // heads unchanged: the check passes and the merge is requested once more

        Assert.Equal([7L, 7L], scm.MergeCalls);
        Assert.Equal("build", store.Single("ORD-1").StageId);
    }

    private static TimeSpan MergeAcceptedWaitPlus => Orchestrator.MergeAcceptedWait + TimeSpan.FromSeconds(1);

    /// <summary>An auto-approved round at <paramref name="stageId"/> (Approved, heads pinned); then the heads move.</summary>
    private async Task AutoApproveAndMoveHeads(string stageId, bool move = true)
    {
        Configure("active", (stageId, "auto"));
        AnswerReviews(Low);
        git.ChangedFiles = ["src/Export/Csv.cs"];
        scm.BranchHeads["symphonist/ORD-1"] = "head000";
        scm.BranchHeads["main"] = "main000";
        scm.BranchHeads["production"] = "prod000";
        builds.Statuses["merged-by-hand"] = BuildStatus.Succeeded;
        var pr = await scm.OpenPullRequestAsync("main", "production", "Release ORD-1", "", CancellationToken.None);
        scm.Opened.Clear();
        await SeedWaiting("ORD-1", stageId, "In Review", t =>
        {
            t.PullRequestId = 7;
            t.MergeCommit = "build-1";
            if (stageId == "release") t.ReleasePullRequestId = pr.Id;
        });
        scm.AddOpen(7, "symphonist/ORD-1");
        var o = Create();
        await TickIdle(o);
        var approved = store.Single("ORD-1");
        Assert.Equal((StageStatus.Approved, true), (approved.Status, approved.ApprovedHead is not null));
        if (!move) return;
        scm.BranchHeads["symphonist/ORD-1"] = "head-merged";
        scm.BranchHeads["main"] = "main-merged";
        scm.BranchHeads["production"] = "prod-merged";
    }

    [Fact] public async Task Human_approved_merge_checks_no_heads()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, "In Review", t => t.PullRequestId = 7);
        scm.PullRequests[7] = FakeSourceControl.Pr(7); // no branch heads known: any head check would fail
        var o = Create();

        Assert.True((await o.ApproveAsync("orders", "ORD-1", CancellationToken.None)).Changed);
        await TickIdle(o);

        Assert.Equal([7L], scm.MergeCalls);
        Assert.Equal("build", store.Single("ORD-1").StageId);
    }

    [Fact] public async Task Review_uses_a_concurrency_slot()
    {
        Configure("active", ("plan", "auto"));
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = r => FakeAgentRunner.IsReview(r) ? release.Task : Task.FromResult(StageResult);
        await SeedWaiting("ORD-1", "plan", "Planned");
        Source("ORD-2", "Planned");
        var o = Create(maxRuns: 1);

        await o.TickAsync(CancellationToken.None);
        await o.TickAsync(CancellationToken.None);

        Assert.Equal(StageStatus.Pending, store.Single("ORD-2").Status);
        Assert.True(agent.Requests.All(FakeAgentRunner.IsReview));
        Assert.Single(store.EventMessages("ORD-1"), m => m == "reviewing plan");

        release.SetResult(FakeAgentRunner.Review("high", "unsure"));
        await o.WhenIdleAsync();
        await TickIdle(o);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-2").Status);
    }

    [Fact] public async Task Forget_refused_while_reviewing()
    {
        Configure("active", ("plan", "auto"));
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunAsync = _ => release.Task;
        await SeedWaiting("ORD-1", "plan", "Planned");
        var o = Create();
        await o.TickAsync(CancellationToken.None);

        Assert.Equal(ForgetResult.Running, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));

        release.SetResult(Low());
        await o.WhenIdleAsync();
        Assert.Equal(ForgetResult.Forgotten, await o.ForgetAsync("orders", "ORD-1", CancellationToken.None));
    }

    [Fact] public async Task Review_interrupted_by_restart_runs_again()
    {
        Configure("active", ("plan", "auto"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        agent.OnRunCancellableAsync = async (_, ct) =>
        {
            if (Interlocked.Increment(ref runs) > 1) return Low();
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Low();
        };
        await SeedWaiting("ORD-1", "plan", "Planned");
        var first = Create();
        await first.TickAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.CancelRuns(); // host shutdown: not a verdict
        await first.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(store.GateReviews);
        Assert.Equal((StageStatus.AwaitingApproval, true), (store.Single("ORD-1").Status, store.Single("ORD-1").ReviewDue));

        var restarted = Create();
        await restarted.RecoverAsync(CancellationToken.None);
        await TickIdle(restarted);

        Assert.Equal(2, ReviewRequests);
        Assert.Single(store.GateReviews);
        Assert.Equal("implement", store.Single("ORD-1").StageId);
    }

    [Fact] public async Task Send_back_records_gate_decision()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, "In Review");
        var o = Create();

        Assert.True((await o.SendBackAsync("orders", "ORD-1", "rename it", null, CancellationToken.None)).Changed);

        var d = Assert.Single(store.GateDecisions);
        Assert.Equal((store.Single("ORD-1").Id, "orders", "implement", Now, GateDecision.SentBack, false),
            (d.TicketId, d.ProjectId, d.StageId, d.WaitingSince, d.Decision, d.Auto));
    }

    [Fact] public async Task Approve_with_stale_waiting_since_changes_nothing()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();

        var outcome = await o.ApproveAsync("orders", "ORD-1", CancellationToken.None, "plan", Now.AddMinutes(-30));

        Assert.False(outcome.Changed);
        Assert.Equal("plan is waiting on a newer round; reload to see it", outcome.Message);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
        Assert.Empty(store.GateDecisions);

        Assert.True((await o.ApproveAsync("orders", "ORD-1", CancellationToken.None, "plan", Now)).Changed);
        var d = Assert.Single(store.GateDecisions);
        Assert.Equal((GateDecision.Approved, false), (d.Decision, d.Auto));
    }

    [Fact] public async Task Claude_comment_rerun_at_auto_triage_is_reviewed_again()
    {
        Configure("active", ("triage", "auto"));
        var verdicts = new Queue<AgentRunResult>([FakeAgentRunner.Review("high", "unclear", reasons: ["which format?"]), Low()]);
        AnswerReviews(verdicts.Dequeue);
        Source("ORD-1", "Backlog");
        var o = Create();

        await TickIdle(o); // triage run
        await TickIdle(o); // review: high
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.High), (store.Single("ORD-1").Status, store.Single("ORD-1").Risk));

        time.Advance(TimeSpan.FromMinutes(10));
        Comments("ORD-1", new SourceComment("Me", true, Now.AddMinutes(5), "@claude answers: CSV"));
        await TickIdle(o); // rerun triage with the answers
        await TickIdle(o); // review: low

        var t = store.Single("ORD-1");
        Assert.Equal(("plan", (RiskLevel?)null), (t.StageId, t.Risk));
        Assert.Equal(RiskLevel.Low, store.GateReviews[^1].Verdict.Risk);
        Assert.Equal(2, ReviewRequests);
        Assert.StartsWith("Auto-approved triage: low risk.", Assert.Single(AutoApprovalComments));
        Assert.Equal([(GateDecision.SentBack, false), (GateDecision.Approved, true)],
            store.GateDecisions.Select(d => (d.Decision, d.Auto)));
        Assert.NotEqual(store.GateDecisions[0].WaitingSince, store.GateDecisions[1].WaitingSince);
    }

    [Fact] public async Task Release_gate_auto_approves_low_risk_production_change()
    {
        Configure("active", ("release", "auto"));
        AnswerReviews(() => FakeAgentRunner.Review("low", "one safe change", checkedItems: ["diff", "build passed"]));
        await Seed("ORD-1", "release", StageStatus.Pending, "In Review", t => t.MergeCommit = "build-1");
        scm.BranchHeads["main"] = "main000";
        scm.BranchHeads["production"] = "prod000";
        builds.Statuses["merge-1"] = BuildStatus.Succeeded;
        var o = Create();

        await TickIdle(o); // release PR opened, waiting at the gate
        Assert.Equal((StageStatus.AwaitingApproval, true), (store.Single("ORD-1").Status, store.Single("ORD-1").ReviewDue));
        await TickIdle(o); // review: low -> approved
        Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);
        await TickIdle(o); // merge
        await TickIdle(o); // deploy

        var t = store.Single("ORD-1");
        Assert.Equal(StageStatus.Done, t.Status);
        Assert.Equal([1L], scm.MergeCalls);
        Assert.StartsWith("Auto-approved release: low risk.", Assert.Single(AutoApprovalComments));
        Assert.True(Assert.Single(store.GateDecisions).Auto);
    }

    [Fact] public async Task Human_release_gate_is_reviewed_but_never_auto_approved()
    {
        Configure("active");
        AnswerReviews(Low);
        scm.BranchHeads["main"] = "main000";
        await SeedWaiting("ORD-1", "release", "In Review", t => t.ReleasePullRequestId = 3);
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.Low, false), (t.Status, t.Risk, t.ReviewDue));
        Assert.Contains("reviewer: low risk — small change", store.EventMessages("ORD-1"));
        Assert.Equal(GatePolicy.Human, Assert.Single(store.GateReviews).Gate);
        Assert.Empty(tracker.Comments);
        Assert.Empty(store.GateDecisions);
        Assert.Empty(scm.MergeCalls);
    }

    [Fact] public async Task Review_off_runs_no_reviewer()
    {
        Source("ORD-1", "Planned");
        var o = Create();

        await TickIdle(o);
        await TickIdle(o);

        Assert.False(store.Single("ORD-1").ReviewDue);
        Assert.Equal(0, ReviewRequests);
        Assert.Empty(store.GateReviews);
    }

    private const string PlanLine =
        "  - { id: plan,      kind: plan,      gate: human, entryStatuses: [Planned], onEnterStatus: Planned, approvedWhenStatus: In Progress }\n";

    /// <summary>A hand edit that drops the plan stage, then a reload.</summary>
    private void RemovePlanStage()
    {
        var yaml = OrdersYaml.ReplaceLineEndings("\n");
        Assert.Contains(PlanLine, yaml);
        File.WriteAllText(Path.Combine(home, "projects", "orders.yaml"), yaml.Replace(PlanLine, ""));
        Assert.Empty(registry.Reload().Errors);
    }

    [Fact] public async Task Ticket_at_removed_stage_is_blocked_and_can_be_sent_back()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        var o = Create();
        RemovePlanStage();

        await TickIdle(o);
        await TickIdle(o);

        var t = store.Single("ORD-1");
        Assert.Equal(StageStatus.Blocked, t.Status);
        Assert.Equal("plan", t.StageId);
        Assert.Contains("no longer in the pipeline", t.BlockedReason);
        Assert.Single(store.EventMessages("ORD-1"), m => m.Contains("no longer in the pipeline"));
        const string wayOut = "stage 'plan' is no longer in the pipeline; send it back to an existing stage (--to)";
        Assert.Equal(wayOut, (await o.ApproveAsync("orders", "ORD-1", CancellationToken.None)).Message);
        var noTarget = await o.SendBackAsync("orders", "ORD-1", "again", null, CancellationToken.None);
        Assert.Equal((false, wayOut), (noTarget.Changed, noTarget.Message));

        var outcome = await o.SendBackAsync("orders", "ORD-1", "plan it again", "triage", CancellationToken.None);

        Assert.True(outcome.Changed, outcome.Message);
        t = store.Single("ORD-1");
        Assert.Equal(("triage", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Equal("plan it again", t.Feedback);
    }

    [Fact] public async Task Card_move_of_ticket_at_removed_stage_does_not_stop_the_tick()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, "Planned");
        Source("ORD-2", "Backlog");
        var o = Create();
        agent.OnRunAsync = _ => new TaskCompletionSource<AgentRunResult>().Task; // keep ORD-2's run in flight
        RemovePlanStage();
        tracker.Move("ORD-1", "Backlog");

        await o.TickAsync(CancellationToken.None);

        Assert.Equal(StageStatus.Blocked, store.Single("ORD-1").Status);
        Assert.Equal(("triage", StageStatus.Running), (store.Single("ORD-2").StageId, store.Single("ORD-2").Status));
    }

    [Fact] public async Task Run_in_flight_when_its_stage_is_removed_finishes_then_is_blocked()
    {
        Source("ORD-1", "Planned");
        var run = new TaskCompletionSource<AgentRunResult>();
        agent.OnRunAsync = _ => run.Task;
        var o = Create();
        await o.TickAsync(CancellationToken.None);
        Assert.Equal(("plan", StageStatus.Running), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));

        RemovePlanStage();
        await o.TickAsync(CancellationToken.None); // in flight: left alone
        Assert.Equal(StageStatus.Running, store.Single("ORD-1").Status);
        run.SetResult(StageResult);
        await o.WhenIdleAsync(); // applied with the configuration the run started with
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));

        await TickIdle(o);

        Assert.Equal(StageStatus.Blocked, store.Single("ORD-1").Status);
    }

    [Fact] public async Task Reloaded_project_is_ticked_without_restart()
    {
        var o = Create();
        await TickIdle(o);
        Assert.Equal(1, tracker.QueryCount);

        File.WriteAllText(Path.Combine(home, "projects", "billing.yaml"), OrdersYaml.Replace("id: orders", "id: billing"));
        var reload = await o.ReloadProjectsAsync(CancellationToken.None);
        await TickIdle(o);

        Assert.Equal(["billing", "orders"], reload.ProjectIds.Order(StringComparer.Ordinal));
        Assert.Equal(3, tracker.QueryCount);
    }

    [Fact] public async Task Reload_waits_for_a_running_tick()
    {
        var querying = new TaskCompletionSource();
        using var release = new ManualResetEventSlim();
        tracker.OnQuery = () =>
        {
            querying.TrySetResult();
            release.Wait();
            return tracker.Tickets.ToList();
        };
        var o = Create();
        var tick = Task.Run(() => o.TickAsync(CancellationToken.None));
        await querying.Task;

        var reload = o.ReloadProjectsAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(reload.IsCompleted);

        release.Set();
        await tick;
        await reload;
    }
}
