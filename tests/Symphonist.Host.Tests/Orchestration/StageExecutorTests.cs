using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations.Git;

namespace Symphonist.Host.Tests.Orchestration;

/// <summary>Tests that set the process-global SYMPHONIST_HOME run in this collection, never in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SymphonistHomeCollection
{
    public const string Name = "SYMPHONIST_HOME";
}

[Collection(SymphonistHomeCollection.Name)]
public sealed class StageExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");

    private readonly string home = Path.Combine(Path.GetTempPath(), "symphonist-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeGitWorkspace git = new();
    private readonly FakeAgentRunner agent = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTracker tracker = new();
    private readonly FakeSourceControl scm = new();
    private readonly FakeBuildWatcher builds = new();
    private readonly ProjectConfig project = Project();
    private readonly ProjectIntegrations io;
    private readonly StageExecutor executor;

    private readonly SourceTicket source = new("ORD-1", "Add export", "Export orders to CSV", "Selected",
        [
            new SourceComment("Ann", false, Now.AddHours(-2), "first"),
            new SourceComment("Bob", true, Now.AddHours(-1), "second"),
        ],
        new Uri("https://jira.example/browse/ORD-1"));

    public StageExecutorTests()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", home);
        io = new ProjectIntegrations(tracker, scm, builds);
        executor = new StageExecutor(git, agent, store, time);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private static ProjectConfig Project()
    {
        StageDefinition S(string id, StageKind kind) => new(id, kind, GatePolicy.None, [], null, null, null);
        return new ProjectConfig("orders", "Orders",
            new TrackerConfig("jira", new Uri("https://jira.example"), "me@example.com", "jira-token", "assignee = currentUser()"),
            new RepoConfig("bitbucket", "w", "orders", "/src/orders", "main", "production", "me@example.com", "bb-token", "squash"),
            new BuildConfig("bitbucket-pipelines", 45),
            new AgentSettings("claude-code", Isolation.Container, [], [], 30, null, null),
            3,
            [
                S("triage", StageKind.Triage), S("plan", StageKind.Plan), S("implement", StageKind.Implement),
                S("build", StageKind.Build), S("uat", StageKind.Uat), S("release", StageKind.Release),
                S("deploy", StageKind.Deploy), S("done", StageKind.Done),
            ],
            new Dictionary<StageKind, string>(), "/config");
    }

    private static Ticket At(string stageId) => new()
    {
        Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "Add export", StageId = stageId,
        Status = StageStatus.Running, Branch = "symphonist/ORD-1", StageStartedAt = Now,
    };

    [Fact] public void Agent_stages_are_triage_plan_implement()
    {
        Assert.True(StageExecutor.IsAgentStage(StageKind.Triage));
        Assert.True(StageExecutor.IsAgentStage(StageKind.Plan));
        Assert.True(StageExecutor.IsAgentStage(StageKind.Implement));
        Assert.False(StageExecutor.IsAgentStage(StageKind.Build));
        Assert.False(StageExecutor.IsAgentStage(StageKind.Release));
    }

    [Fact] public async Task Triage_comments_agent_markdown_and_renders_ticket_values()
    {
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(summary: "s", comment: "## Triage notes")));
        var t = At("triage");

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal([("ORD-1", "## Triage notes")], tracker.Comments);
        Assert.Equal([("ORD-1", "symphonist/ORD-1")], git.Prepared);
        Assert.Empty(git.Commits);
        var prompt = Assert.Single(agent.Prompts);
        Assert.Contains("ORD-1", prompt);
        Assert.Contains("Export orders to CSV", prompt);
        Assert.Contains("https://jira.example/browse/ORD-1", prompt);
        Assert.Contains("Ann: first\nBob: second", prompt.ReplaceLineEndings("\n"));
        Assert.Equal(git.WorkspacePath, agent.Requests[0].WorkingDirectory);
    }

    [Fact] public async Task Triage_falls_back_to_summary_comment()
    {
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(summary: "just a summary")));

        await executor.ExecuteAsync(project, io, At("triage"), source, CancellationToken.None);

        Assert.Equal("just a summary", Assert.Single(tracker.Comments).Markdown);
    }

    [Fact] public async Task Missing_source_ticket_renders_empty_values()
    {
        var r = await executor.ExecuteAsync(project, io, At("triage"), null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.DoesNotContain("{{", Assert.Single(agent.Prompts));
    }

    [Fact] public async Task Agent_run_is_recorded_with_raw_log()
    {
        var t = At("triage");

        await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        var run = Assert.Single(store.AgentRuns);
        Assert.Equal(t.Id, run.TicketId);
        Assert.Equal("triage", run.StageId);
        Assert.Equal(Now, run.StartedAt);
        Assert.True(run.Succeeded);
        Assert.Equal(0.25m, run.CostUsd);
        Assert.Equal(Path.Combine(home, "logs", "orders", "ORD-1", "triage-20261021T100000Z.json"), run.RawLogPath);
        Assert.Equal("""{"raw":true}""", await File.ReadAllTextAsync(run.RawLogPath));
    }

    [Fact] public async Task Plan_commits_pushes_and_comments_links()
    {
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(summary: "Planned it",
            specPath: "docs/specs/a.md", planPath: "docs/plans/a.md")));
        var t = At("plan");

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal([(git.WorkspacePath, "ORD-1: spec and plan")], git.Commits);
        Assert.Equal([(git.WorkspacePath, "symphonist/ORD-1")], git.Pushes);
        Assert.Equal("docs/specs/a.md", t.SpecPath);
        Assert.Equal("docs/plans/a.md", t.PlanPath);
        var comment = Assert.Single(tracker.Comments).Markdown;
        Assert.StartsWith("Planned it", comment);
        Assert.Contains("Spec: https://scm.example/src/symphonist/ORD-1/docs/specs/a.md", comment);
        Assert.Contains("Plan: https://scm.example/src/symphonist/ORD-1/docs/plans/a.md", comment);
    }

    [Fact] public async Task Plan_without_changes_fails()
    {
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));
        git.OnCommit = _ => false;

        var r = await executor.ExecuteAsync(project, io, At("plan"), source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "agent made no changes"), r);
        Assert.Empty(git.Pushes);
        Assert.Empty(tracker.Comments);
    }

    [Fact] public async Task Implement_opens_pr_once_and_reuses_on_rerun()
    {
        agent.OnRun = _ => FakeAgentRunner.Ok(FakeAgentRunner.Output(prTitle: "Export orders", prDescription: "Adds CSV export"));
        var t = At("implement");
        t.SpecPath = "docs/specs/a.md";
        t.PlanPath = "docs/plans/a.md";

        var first = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);
        var second = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, first.Status);
        Assert.Equal(ExecutionStatus.Completed, second.Status);
        var opened = Assert.Single(scm.Opened);
        Assert.Equal(("symphonist/ORD-1", "main", "ORD-1: Export orders"), (opened.Source, opened.Destination, opened.Title));
        Assert.Equal("Adds CSV export\n\nTicket: https://jira.example/browse/ORD-1", opened.Description);
        Assert.Equal(1, t.PullRequestId);
        Assert.Equal(["ORD-1: Export orders", "ORD-1: Export orders"], git.Commits.Select(c => c.Message));
        Assert.Equal(2, git.Pushes.Count);
        Assert.Equal("PR ready for review: https://scm.example/pr/1", tracker.Comments[0].Markdown);
        Assert.Equal("PR updated", tracker.Comments[1].Markdown);
        Assert.Contains("docs/specs/a.md", agent.Prompts[0]);
        Assert.Contains("docs/plans/a.md", agent.Prompts[0]);
    }

    [Fact] public async Task Implement_reuses_open_pr_found_on_host()
    {
        scm.OnFindOpen = (_, _) => FakeSourceControl.Pr(7);
        var t = At("implement");

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Empty(scm.Opened);
        Assert.Equal([("symphonist/ORD-1", "main")], scm.FindCalls);
        Assert.Equal(7, t.PullRequestId);
        Assert.Equal("PR ready for review: https://scm.example/pr/7", Assert.Single(tracker.Comments).Markdown);
    }

    [Fact] public async Task Implement_commit_falls_back_to_ticket_title()
    {
        await executor.ExecuteAsync(project, io, At("implement"), null, CancellationToken.None);

        Assert.Equal("ORD-1: Add export", Assert.Single(git.Commits).Message);
        Assert.Equal("ORD-1: Add export", Assert.Single(scm.Opened).Title);
    }

    [Fact] public async Task Implement_without_changes_fails()
    {
        git.OnCommit = _ => false;
        var t = At("implement");

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "agent made no changes"), r);
        Assert.Empty(scm.Opened);
        Assert.Null(t.PullRequestId);
    }

    [Fact] public async Task Feedback_reaches_prompt()
    {
        var t = At("plan");
        t.Feedback = "use X";
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));

        await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Contains("use X", Assert.Single(agent.Prompts));
    }

    [Fact] public async Task Agent_failure_returns_failed_with_error()
    {
        agent.Results.Enqueue(FakeAgentRunner.Fail("agent timed out after 30 minutes"));
        var t = At("plan");

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "agent timed out after 30 minutes"), r);
        Assert.Empty(git.Commits);
        Assert.Empty(tracker.Comments);
        var run = Assert.Single(store.AgentRuns);
        Assert.False(run.Succeeded);
        Assert.Equal("agent timed out after 30 minutes", run.Error);
        Assert.Equal(StageStatus.Running, t.Status);
        Assert.Equal(0, t.Attempts);
    }

    [Fact] public async Task Git_failure_returns_failed_with_message()
    {
        git.PushError = new GitCommandException("git push failed: rejected");
        agent.Results.Enqueue(FakeAgentRunner.Ok(FakeAgentRunner.Output(specPath: "s.md", planPath: "p.md")));

        var r = await executor.ExecuteAsync(project, io, At("plan"), source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "git push failed: rejected"), r);
    }

    [Fact] public async Task Http_failure_returns_failed_with_message()
    {
        var t = At("implement");
        t.PullRequestId = 1;

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal("pull request 1 not found", r.Error);
    }

    [Fact] public async Task Build_without_merge_commit_fails()
    {
        var r = await executor.ExecuteAsync(project, io, At("build"), source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "no merge commit"), r);
    }

    [Theory]
    [InlineData(BuildStatus.Succeeded, ExecutionStatus.Completed, null)]
    [InlineData(BuildStatus.Failed, ExecutionStatus.Failed, "build failed")]
    [InlineData(BuildStatus.Running, ExecutionStatus.InProgress, null)]
    public async Task Build_maps_pipeline_status(BuildStatus status, ExecutionStatus expected, string? error)
    {
        var t = At("build");
        t.MergeCommit = "abc123";
        builds.Statuses["abc123"] = status;

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(expected, error), r);
        Assert.Equal(["abc123"], builds.Calls);
    }

    [Fact] public async Task Build_not_found_times_out()
    {
        var t = At("build");
        t.MergeCommit = "abc123";

        t.StageStartedAt = Now.AddMinutes(-10);
        var waiting = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);
        t.StageStartedAt = Now.AddMinutes(-46);
        var timedOut = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.InProgress, waiting.Status);
        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "no pipeline ran for abc123"), timedOut);
    }

    [Fact] public async Task Deploy_watches_release_merge_commit()
    {
        var t = At("deploy");
        t.MergeCommit = "feature";
        t.ReleaseMergeCommit = "rel456";
        builds.Statuses["rel456"] = BuildStatus.Succeeded;

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(["rel456"], builds.Calls);
    }

    [Fact] public async Task Deploy_without_release_merge_commit_fails()
    {
        var t = At("deploy");
        t.MergeCommit = "feature";

        var r = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(new ExecutionResult(ExecutionStatus.Failed, "no merge commit"), r);
    }

    [Theory]
    [InlineData("uat")]
    [InlineData("done")]
    public async Task Passive_stages_complete(string stageId)
    {
        var r = await executor.ExecuteAsync(project, io, At(stageId), source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Empty(agent.Requests);
    }

    [Fact] public async Task Release_opens_pr_main_to_production_once()
    {
        var t = At("release");

        var first = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);
        var second = await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, first.Status);
        Assert.Equal(ExecutionStatus.Completed, second.Status);
        var opened = Assert.Single(scm.Opened);
        Assert.Equal(("main", "production", "Release ORD-1"), (opened.Source, opened.Destination, opened.Title));
        Assert.Equal(1, t.ReleasePullRequestId);
        Assert.Contains("https://scm.example/pr/1", Assert.Single(tracker.Comments).Markdown);
    }

    [Fact] public async Task Release_reuses_open_pr_found_on_host()
    {
        scm.OnFindOpen = (s, d) => s == "main" && d == "production" ? FakeSourceControl.Pr(9) : null;
        var t = At("release");

        await executor.ExecuteAsync(project, io, t, source, CancellationToken.None);

        Assert.Empty(scm.Opened);
        Assert.Equal(9, t.ReleasePullRequestId);
    }

    [Fact] public async Task Merge_sets_merge_commit_and_completes()
    {
        var t = At("implement");
        t.PullRequestId = 3;
        scm.PullRequests[3] = FakeSourceControl.Pr(3);
        scm.MergeResults.Enqueue(new MergeResult(true, "m3", null));

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal("m3", t.MergeCommit);
        Assert.Equal([3L], scm.MergeCalls);
    }

    [Fact] public async Task Merge_refused_waits()
    {
        var t = At("implement");
        t.PullRequestId = 3;
        scm.PullRequests[3] = FakeSourceControl.Pr(3);
        scm.MergeResults.Enqueue(new MergeResult(false, null, "Requires 2 approvals"));

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.InProgress, r.Status);
        Assert.Contains("Requires 2 approvals", r.Event);
        Assert.Equal("waiting to merge: Requires 2 approvals", r.Event);
        Assert.Null(t.MergeCommit);
    }

    [Fact] public async Task Merge_already_done_by_hand_completes()
    {
        var t = At("implement");
        t.PullRequestId = 3;
        scm.PullRequests[3] = FakeSourceControl.Pr(3, PullRequestState.Merged, "byhand");

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal("byhand", t.MergeCommit);
        Assert.Empty(scm.MergeCalls);
    }

    [Fact] public async Task Release_merge_sets_release_merge_commit()
    {
        var t = At("release");
        t.MergeCommit = "feature";
        t.ReleasePullRequestId = 5;
        scm.PullRequests[5] = FakeSourceControl.Pr(5);
        scm.MergeResults.Enqueue(new MergeResult(true, "r5", null));

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal("r5", t.ReleaseMergeCommit);
        Assert.Equal("feature", t.MergeCommit);
    }

    [Fact] public async Task Merge_declined_pr_fails()
    {
        var t = At("implement");
        t.PullRequestId = 3;
        scm.PullRequests[3] = FakeSourceControl.Pr(3, PullRequestState.Declined);

        var r = await executor.PostApprovalAsync(project, io, t, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Empty(scm.MergeCalls);
    }

    [Fact] public async Task Post_approval_of_other_stages_completes()
    {
        var r = await executor.PostApprovalAsync(project, io, At("plan"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, r.Status);
    }
}
