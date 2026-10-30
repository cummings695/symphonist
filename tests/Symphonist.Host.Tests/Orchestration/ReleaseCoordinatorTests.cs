using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Notifications;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Host.Tests.Orchestration;

public sealed class ReleaseCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z");

    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeTicketStore store = new();
    private readonly FakeGitWorkspace git = new();
    private readonly RecordingNotifier notifier = new();
    private readonly NotificationCenter center;
    private readonly ReleaseCoordinator releases;
    private readonly ProjectConfig project = Project();

    private sealed class RecordingNotifier : INotifier
    {
        public List<DesktopNotification> Sent { get; } = [];

        public Task ShowAsync(DesktopNotification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    public ReleaseCoordinatorTests()
    {
        center = new NotificationCenter(notifier, new DashboardAddress(5199), id => id, time, NullLogger<NotificationCenter>.Instance);
        releases = new ReleaseCoordinator(store, git, time, center);
    }

    public void Dispose() => center.Dispose();

    private static ProjectConfig Project(bool withRelease = true)
    {
        StageDefinition S(string id, StageKind kind, GatePolicy gate = GatePolicy.None, string? onEnter = null) =>
            new(id, kind, gate, [], onEnter, null, null);
        IEnumerable<StageDefinition> stages =
        [
            S("implement", StageKind.Implement, GatePolicy.Human, "In Progress"), S("build", StageKind.Build),
            S("uat", StageKind.Uat, GatePolicy.Human), S("release", StageKind.Release, GatePolicy.Human),
            S("deploy", StageKind.Deploy), S("done", StageKind.Done, onEnter: "Done"),
        ];
        if (!withRelease) stages = stages.Where(s => s.Kind is not (StageKind.Release or StageKind.Deploy));
        return new ProjectConfig("orders", "Orders",
            new JiraTrackerConfig("jira", "jira-token", new Uri("https://jira.example"), "me@example.com", "assignee = currentUser()"),
            new BitbucketRepoConfig("bitbucket", "/src/orders", "main", "production", "bb-token", "w", "orders", "me@example.com", "squash"),
            new BitbucketPipelinesConfig("bitbucket-pipelines", 45),
            new AgentSettings("claude-code", Isolation.Container, [], [], 30, null, null),
            3, [.. stages], new Dictionary<StageKind, string>(), "/config");
    }

    private async Task<Ticket> Seed(string key, string stageId, StageStatus status, string? mergeCommit, Action<Ticket>? with = null)
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = $"Title {key}", StageId = stageId, Status = status,
            Branch = $"symphonist/{key}", MergeCommit = mergeCommit, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now,
        };
        with?.Invoke(t);
        await store.SaveAsync(t, CancellationToken.None);
        return t;
    }

    private Task<Ticket> Ready(string key, string mergeCommit) => Seed(key, "release", StageStatus.Pending, mergeCommit);

    private const string Merge1 = "aaaaaaa1110000000000000000000000000000aa";
    private const string Merge2 = "bbbbbbb2220000000000000000000000000000bb";
    private const string Merge3 = "ddddddd4440000000000000000000000000000dd";
    private const string Outside = "ccccccc3330000000000000000000000000000cc";

    [Fact] public async Task Preview_lists_ready_tickets_also_shipped_and_outside_commits()
    {
        await Ready("ORD-1", Merge1);
        await Seed("ORD-2", "build", StageStatus.Running, Merge2[..7].ToUpperInvariant()); // a short hash, other case
        git.FirstParentCommits = [Merge2, Outside, Merge1];

        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        Assert.Null(preview.Unavailable);
        Assert.Equal("orders", preview.ProjectId);
        Assert.Equal(["ORD-1"], preview.Ready);
        Assert.Equal([new AlsoShipped("ORD-2", "build", StageStatus.Running, true)], preview.AlsoShipped);
        Assert.Equal(1, preview.OutsideCommits);
        Assert.Equal("main000", preview.MainHead);
        Assert.Equal(("_release", "main"), Assert.Single(git.Prepared));
        Assert.Equal("prod000..main000", Assert.Single(git.FirstParentCalls).Range);
    }

    [Fact] public async Task Reworked_ticket_is_also_shipped_but_not_finished()
    {
        await Seed("ORD-3", "implement", StageStatus.Pending, Merge3, t => t.Feedback = "the export misses a column");
        git.FirstParentCommits = [Merge3];

        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        Assert.Equal([new AlsoShipped("ORD-3", "implement", StageStatus.Pending, false)], preview.AlsoShipped);
        Assert.Empty(preview.Ready);
        Assert.Equal(0, preview.OutsideCommits);
    }

    [Fact] public async Task Done_and_legacy_tickets_ship_but_never_join()
    {
        await Seed("ORD-4", "done", StageStatus.Done, Merge1);
        await Seed("ORD-5", "release", StageStatus.AwaitingApproval, Merge2, t => t.ReleasePullRequestId = 9);
        git.FirstParentCommits = [Merge1, Merge2];

        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        Assert.Equal([new AlsoShipped("ORD-4", "done", StageStatus.Done, false),
            new AlsoShipped("ORD-5", "release", StageStatus.AwaitingApproval, false)], preview.AlsoShipped);
        Assert.Empty(preview.Ready);
    }

    [Fact] public async Task Nothing_to_release_is_unavailable()
    {
        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        Assert.Equal("nothing to release: production already contains main", preview.Unavailable);
        var started = await releases.StartAsync(project, preview, CancellationToken.None);
        Assert.False(started.Started);
        Assert.Empty(store.Tickets);
    }

    [Fact] public async Task No_release_stage_is_unavailable()
    {
        await Seed("ORD-1", "build", StageStatus.Pending, Merge1);
        git.FirstParentCommits = [Merge1];
        var p = Project(withRelease: false);

        var preview = await releases.PreviewAsync(p, CancellationToken.None);

        Assert.Equal("the pipeline has no release stage", preview.Unavailable);
        Assert.False((await releases.StartAsync(p, preview, CancellationToken.None)).Started);
        Assert.Empty(git.Prepared);
    }

    [Fact] public async Task Repository_errors_make_release_now_unavailable()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentError = new InvalidOperationException("fetch failed");

        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        Assert.Equal("could not read the repository: fetch failed", preview.Unavailable);
    }

    [Fact] public async Task Open_release_makes_release_now_unavailable()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);
        Assert.True((await releases.StartAsync(project, preview, CancellationToken.None)).Started);
        await Ready("ORD-2", Merge2);

        var again = await releases.PreviewAsync(project, CancellationToken.None);
        var second = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal("_release-1 is still open; newly ready tickets wait for the next release", again.Unavailable);
        Assert.Equal((false, "_release-1 is still open; newly ready tickets wait for the next release", (string?)null),
            (second.Started, second.Message, second.ReleaseKey));
        Assert.Single(store.Tickets, t => t.Kind == TicketKind.Release);
        Assert.Null(store.Single("ORD-2").ReleaseTicketId);
    }

    [Fact] public async Task Start_refuses_when_the_preview_changed()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);
        // Another ticket merged into main since the developer looked.
        await Seed("ORD-2", "build", StageStatus.Running, Merge2);
        git.FirstParentCommits = [Merge2, Merge1];
        git.RemoteCommits["refs/remotes/origin/main"] = "main001";

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal((false, "the release changed; review it again", (string?)null), (result.Started, result.Message, result.ReleaseKey));
        Assert.DoesNotContain(store.Tickets, t => t.Kind == TicketKind.Release);
        Assert.Empty(git.Pushes);
        Assert.Null(store.Single("ORD-1").ReleaseTicketId);
    }

    [Fact] public async Task Start_refuses_when_only_the_main_head_changed()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);
        git.RemoteCommits["refs/remotes/origin/main"] = "main001";

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal("the release changed; review it again", result.Message);
        Assert.Empty(git.Pushes);
    }

    [Fact] public async Task Start_refuses_a_release_branch_that_does_not_start_at_the_previewed_head()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);
        git.RemoteCommits["HEAD"] = "stale00"; // e.g. the branch appeared on origin between the check and the prepare

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("symphonist/release-1 is at stale00, not at main's main000; the release was not started", result.Message);
        Assert.Empty(git.Pushes);
        Assert.DoesNotContain(store.Tickets, t => t.Kind == TicketKind.Release);
        Assert.Null(store.Single("ORD-1").ReleaseTicketId);
    }

    [Fact] public async Task Start_skips_release_numbers_whose_branch_already_exists()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        // Left over from a forgotten release or an earlier attempt that pushed but failed after.
        git.RemoteCommits["refs/remotes/origin/symphonist/release-1"] = "stale00";
        git.RemoteCommits["refs/remotes/origin/symphonist/release-2"] = "stale01";
        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal((true, "_release-3"), (result.Started, result.ReleaseKey));
        Assert.Equal("symphonist/release-3", Assert.Single(git.Pushes).Branch);
        Assert.Equal("symphonist/release-3", store.Single("_release-3").Branch);
    }

    [Fact] public async Task Start_removes_a_stale_release_workspace_before_preparing_it()
    {
        await Ready("ORD-1", Merge1);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        await releases.StartAsync(project, preview, CancellationToken.None);

        var remove = git.Events.IndexOf("remove _release-1");
        Assert.True(remove >= 0, string.Join(", ", git.Events));
        Assert.True(remove < git.Events.IndexOf("prepare _release-1"), string.Join(", ", git.Events));
    }

    [Fact] public async Task Start_creates_release_ticket_branch_and_members()
    {
        await Ready("ORD-1", Merge1);
        await Seed("ORD-2", "build", StageStatus.Running, Merge2);
        await Seed("ORD-3", "implement", StageStatus.Pending, Merge3);
        git.FirstParentCommits = [Merge3, Merge2, Outside, Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal((true, "_release-1"), (result.Started, result.ReleaseKey));
        var release = store.Single("_release-1");
        Assert.Equal((TicketKind.Release, "release", StageStatus.Pending, "symphonist/release-1", "main000"),
            (release.Kind, release.StageId, release.Status, release.Branch, release.ReleaseBaseHead));
        Assert.Equal("Release 1: ORD-1, ORD-2", release.Title);
        Assert.Equal("""
            Tickets:
            - ORD-1: Title ORD-1

            Also shipped:
            - ORD-2 (still at build)
            - ORD-3 (still at implement)
            - 1 commit(s) from outside Symphonist
            """.ReplaceLineEndings("\n"), release.ReleaseSummary);
        Assert.Equal(("_release-1", "symphonist/release-1"), git.Prepared[^1]);
        Assert.Equal("symphonist/release-1", Assert.Single(git.Pushes).Branch);
        Assert.Equal("main000", git.RemoteCommits["refs/remotes/origin/symphonist/release-1"]);
        Assert.Equal(release.Id, store.Single("ORD-1").ReleaseTicketId);
        Assert.Equal(release.Id, store.Single("ORD-2").ReleaseTicketId);
        Assert.Null(store.Single("ORD-3").ReleaseTicketId); // being reworked: it ships, but finishes on its own
        Assert.Equal(["in release 1"], store.EventMessages("ORD-1"));
        Assert.Equal(["in release 1"], store.EventMessages("ORD-2"));
    }

    [Fact] public async Task Long_titles_are_cut_and_numbers_follow_earlier_releases()
    {
        for (var i = 1; i <= 30; i++) await Ready($"ORD-{100 + i}", $"{i:x7}" + new string('0', 33));
        var earlier = PipelineEngine.CreateRelease(project, 4, "Release 4", "symphonist/release-4", "", Now);
        earlier.Status = StageStatus.Done;
        await store.SaveAsync(earlier, CancellationToken.None);
        git.FirstParentCommits = [Merge1];
        var preview = await releases.PreviewAsync(project, CancellationToken.None);

        var result = await releases.StartAsync(project, preview, CancellationToken.None);

        Assert.Equal("_release-5", result.ReleaseKey);
        var title = store.Single("_release-5").Title;
        Assert.Equal(120, title.Length);
        Assert.StartsWith("Release 5: ORD-101, ORD-102", title);
        Assert.Equal("symphonist/release-5", store.Single("_release-5").Branch);
    }

    [Fact] public async Task Complete_advances_members_and_leaves_a_reworked_member_where_it_is()
    {
        var release = PipelineEngine.CreateRelease(project, 1, "Release 1", "symphonist/release-1", "", Now);
        release.Status = StageStatus.Done;
        release.ReleaseMergeCommit = "release-merge";
        await store.SaveAsync(release, CancellationToken.None);
        await Seed("ORD-1", "release", StageStatus.Pending, Merge1, t => t.ReleaseTicketId = release.Id);
        // Its card was moved back while it was at build: build finished and applied it, so it is being reworked.
        await Seed("ORD-2", "implement", StageStatus.Running, Merge2, t => t.ReleaseTicketId = release.Id);

        var outcomes = await releases.CompleteAsync(project, release, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        var one = outcomes.Single(o => o.Member.Key == "ORD-1");
        Assert.Equal(("deploy", StageStatus.Pending, "release-merge", (Guid?)null),
            (one.Member.StageId, one.Member.Status, one.Member.ReleaseMergeCommit, one.Member.ReleaseTicketId));
        var two = outcomes.Single(o => o.Member.Key == "ORD-2");
        Assert.Equal(("implement", StageStatus.Running, (Guid?)null), (two.Member.StageId, two.Member.Status, two.Member.ReleaseTicketId));
        Assert.Equal("_release-1 merged; ORD-2 stays at implement for its rework", two.Outcome.Message);
        Assert.Null(two.Outcome.SourceStatusToSet);
    }

    [Fact] public async Task Cancel_returns_members_to_ready()
    {
        var release = PipelineEngine.CreateRelease(project, 1, "Release 1", "symphonist/release-1", "", Now);
        release.Status = StageStatus.Done;
        await store.SaveAsync(release, CancellationToken.None);
        await Seed("ORD-1", "release", StageStatus.Pending, Merge1, t => t.ReleaseTicketId = release.Id);
        await Ready("ORD-9", Merge2); // not a member

        var outcomes = await releases.CancelAsync(project, release, CancellationToken.None);

        var (member, outcome) = Assert.Single(outcomes);
        Assert.Equal("ORD-1", member.Key);
        Assert.Null(member.ReleaseTicketId);
        Assert.Equal("_release-1 cancelled; ready to release again", outcome.Message);
        Assert.True(PipelineEngine.IsReadyToRelease(project, member));
    }

    [Fact] public async Task Ready_notification_once_per_empty_to_nonempty()
    {
        await releases.CheckReadyAsync(project, CancellationToken.None); // first check: records "none ready"
        await Ready("ORD-1", Merge1);
        await releases.CheckReadyAsync(project, CancellationToken.None); // empty -> one: notify
        await Ready("ORD-2", Merge2);
        await releases.CheckReadyAsync(project, CancellationToken.None); // one -> two: still non-empty, no repeat
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal("orders has work ready to release", sent.Title);
        Assert.Equal("orders · 1 ticket ready to release", sent.Body);

        // Both go into a batch: while it is open nothing counts as ready (Release now is unavailable).
        git.FirstParentCommits = [Merge1, Merge2];
        Assert.True((await releases.StartAsync(project, await releases.PreviewAsync(project, CancellationToken.None),
            CancellationToken.None)).Started);
        await releases.CheckReadyAsync(project, CancellationToken.None);
        await Ready("ORD-3", Merge3);
        await releases.CheckReadyAsync(project, CancellationToken.None);
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();
        Assert.Single(notifier.Sent);

        // The batch ends: the ready set is non-empty again.
        var release = store.Single("_release-1");
        release.Status = StageStatus.Done;
        await store.SaveAsync(release, CancellationToken.None);
        await releases.CheckReadyAsync(project, CancellationToken.None);
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact] public async Task First_check_after_start_only_records_the_state()
    {
        await Ready("ORD-1", Merge1);

        await releases.CheckReadyAsync(project, CancellationToken.None);
        await releases.CheckReadyAsync(project, CancellationToken.None);
        time.Advance(NotificationCenter.FoldWindow);
        await center.FlushAsync();

        Assert.Empty(notifier.Sent);
    }
}
