using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class BoardQueryTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeIntegrationFactory integrations = new(new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
    private readonly ProjectRegistry registry;
    private readonly BoardQuery query;
    private readonly Orchestrator orchestrator;

    public BoardQueryTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", TestHome.HostAgent));
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        var time = new FakeTimeProvider(Now);
        orchestrator = new Orchestrator(registry, store, integrations,
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), store, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);
        query = new BoardQuery(registry, store, orchestrator);
    }

    public void Dispose() => home.Dispose();

    private async Task<Ticket> Seed(string key, string stage, StageStatus status, string project = "orders",
        Action<Ticket>? with = null)
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = project, Key = key, Title = $"Title {key}", StageId = stage, Status = status,
            Attempts = 1, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now,
        };
        with?.Invoke(t);
        await store.SaveAsync(t, default);
        return t;
    }

    private static IEnumerable<string> Keys(BoardColumn c) => c.Cards.Select(x => x.Key);

    [Fact]
    public async Task All_projects_board_groups_by_stage_kind()
    {
        var board = await query.LoadBoardAsync(null, default);

        Assert.Equal(["Triage", "Plan", "Implement", "Done"], board.Columns.Select(c => c.Title));
        Assert.Equal(["Triage", "Plan", "Implement", "Done"], board.Columns.Select(c => c.Id));
        Assert.Equal(["Done"], board.Columns.Where(c => c.IsDone).Select(c => c.Id));
        Assert.Null(board.ProjectFilter);
    }

    [Fact]
    public async Task Project_filter_uses_its_pipeline_order()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        await Seed("BIL-1", "plan", StageStatus.Pending, "billing");

        var board = await query.LoadBoardAsync("orders", default);

        Assert.Equal("orders", board.ProjectFilter);
        Assert.Equal(["triage", "plan", "implement", "done"], board.Columns.Select(c => c.Id));
        Assert.Equal(["done"], board.Columns.Where(c => c.IsDone).Select(c => c.Id));
        Assert.Equal(["ORD-1"], board.Columns.SelectMany(Keys));
    }

    [Fact]
    public async Task Unknown_filter_shows_all()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        await Seed("BIL-1", "plan", StageStatus.Pending, "billing");

        var board = await query.LoadBoardAsync("nope", default);

        Assert.Null(board.ProjectFilter);
        Assert.Equal(["BIL-1", "ORD-1"], board.Columns.SelectMany(Keys).Order());
    }

    [Fact]
    public async Task Waiting_cards_sort_first_then_oldest()
    {
        DateTimeOffset At(int h) => DateTimeOffset.Parse($"2026-10-21T{h:00}:00:00Z");
        await Seed("PEND", "implement", StageStatus.Pending, with: t => t.StageStartedAt = At(10));
        await Seed("W11", "implement", StageStatus.AwaitingApproval, with: t => t.WaitingSince = At(11));
        await Seed("BLOCKED", "implement", StageStatus.Blocked);
        await Seed("W09", "implement", StageStatus.AwaitingApproval, with: t => t.WaitingSince = At(9));

        var board = await query.LoadBoardAsync(null, default);

        Assert.Equal(["W09", "W11", "BLOCKED", "PEND"], Keys(board.Columns.Single(c => c.Id == "Implement")));
    }

    [Fact]
    public async Task Counts_cover_visible_tickets()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("ORD-2", "implement", StageStatus.Running);
        await Seed("ORD-3", "implement", StageStatus.Blocked);
        await Seed("BIL-1", "plan", StageStatus.AwaitingApproval, "billing");

        Assert.Equal(new BoardCounts(2, 1, 1), (await query.LoadBoardAsync(null, default)).Counts);
        Assert.Equal(new BoardCounts(1, 0, 0), (await query.LoadBoardAsync("billing", default)).Counts);
    }

    [Fact]
    public async Task Done_column_keeps_twenty_most_recent()
    {
        for (var i = 0; i < 23; i++)
            await Seed($"ORD-{i:00}", "done", StageStatus.Done, with: t => t.UpdatedAt = Now.AddMinutes(i));

        var done = (await query.LoadBoardAsync(null, default)).Columns.Single(c => c.Id == "Done");

        Assert.Equal(20, done.Cards.Count);
        Assert.Equal(3, done.HiddenCount);
        Assert.Equal("ORD-22", done.Cards[0].Key);
        Assert.Equal("ORD-03", done.Cards[^1].Key);
    }

    [Fact]
    public async Task Card_has_links_cost_and_source()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Running, with: t => t.PullRequestId = 7);
        store.AgentRuns.Add(new AgentRunRecord(t.Id, "implement", Now, TimeSpan.FromMinutes(1), true, 0.42m, null, "log"));
        await Seed("ORD-2", "implement", StageStatus.Pending);

        var board = await query.LoadBoardAsync(null, default);
        var cards = board.Columns.Single(c => c.Id == "Implement").Cards;
        var card = cards.Single(c => c.Key == "ORD-1");

        Assert.Equal("https://bitbucket.org/acme/orders/pull-requests/7", card.PrUrl?.ToString());
        Assert.Equal("https://acme.atlassian.net/browse/ORD-1", card.IssueUrl?.ToString());
        Assert.Equal(0.42m, card.CostUsd);
        Assert.Equal("Jira", card.Source);
        Assert.Equal(Now, card.Since);
        Assert.False(card.WaitingOnYou);
        Assert.NotNull(card.MaxAttempts);
        Assert.True(card.Known);
        Assert.Null(cards.Single(c => c.Key == "ORD-2").CostUsd);
    }

    [Fact]
    public async Task Ticket_with_unknown_stage_goes_to_unknown_column()
    {
        await Seed("ORD-1", "review", StageStatus.Pending);

        var last = (await query.LoadBoardAsync(null, default)).Columns[^1];

        Assert.Equal((BoardQuery.UnknownColumnId, "Unknown stage"), (last.Id, last.Title));
        Assert.Equal(["ORD-1"], Keys(last));
        Assert.False(last.Cards[0].Known);
    }

    [Fact]
    public async Task Ticket_of_removed_project_is_still_shown()
    {
        await Seed("GONE-1", "plan", StageStatus.Pending, "gone");

        var board = await query.LoadBoardAsync(null, default);

        var last = board.Columns[^1];
        Assert.Equal(BoardQuery.UnknownColumnId, last.Id);
        var card = Assert.Single(last.Cards);
        Assert.Equal("Unknown", card.Source);
        Assert.Null(card.IssueUrl);
        Assert.Null(card.PrUrl);
        Assert.Null(card.MaxAttempts);
        Assert.False(card.Known);
    }

    [Fact]
    public async Task No_unknown_column_when_every_ticket_is_placed()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);

        Assert.DoesNotContain((await query.LoadBoardAsync(null, default)).Columns, c => c.Id == BoardQuery.UnknownColumnId);
    }

    [Fact]
    public async Task Project_problem_and_pause_are_reported()
    {
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set; run: symphonist secret set jira-acme");
        await orchestrator.TickAsync(CancellationToken.None);
        registry.SetPaused("orders", true);

        var projects = (await query.LoadBoardAsync(null, default)).Projects;

        var orders = projects.Single(p => p.Id == "orders");
        Assert.Contains("secret", orders.Problem);
        Assert.True(orders.Paused);
        Assert.Equal("Jira", orders.Source);
        var billing = projects.Single(p => p.Id == "billing");
        Assert.Equal(Isolation.Host, billing.Isolation);
        Assert.False(billing.Paused);
    }

    [Fact]
    public async Task Detail_lists_runs_and_events_newest_first()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, with: x => x.Branch = "feature/ORD-1");
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now.AddHours(-3), TimeSpan.FromMinutes(2), true, 0.5m, null, "a.log", "planned"), default);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "implement", Now.AddHours(-1), TimeSpan.FromMinutes(5), false, 1.25m, "boom", "b.log"), default);
        await store.AddEventAsync(t.Id, "plan", "first", Now.AddHours(-3), default);
        await store.AddEventAsync(t.Id, "implement", "second", Now.AddHours(-1), default);

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;

        Assert.Equal(["implement", "plan"], detail.Runs.Select(r => r.StageId));
        Assert.Equal(["second", "first"], detail.Events.Select(e => e.Message));
        Assert.Equal(1.75m, detail.Card.CostUsd);
        Assert.Equal("feature/ORD-1", detail.Branch);
    }

    [Fact]
    public async Task Send_back_targets_are_current_then_earlier()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;

        Assert.Equal(["implement", "plan", "triage"], detail.SendBackTargets);
    }

    [Theory]
    [InlineData(StageStatus.AwaitingApproval, true, true, false)]
    [InlineData(StageStatus.Blocked, false, true, true)]
    [InlineData(StageStatus.Approved, false, true, false)]
    [InlineData(StageStatus.Running, false, false, false)]
    public async Task Detail_actions_follow_status(StageStatus status, bool approve, bool sendBack, bool unblock)
    {
        await Seed("ORD-1", "implement", status);

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;

        Assert.Equal((approve, sendBack, unblock), (detail.CanApprove, detail.CanSendBack, detail.CanUnblock));
    }

    [Fact]
    public async Task Unknown_project_detail_is_read_only()
    {
        await Seed("GONE-1", "plan", StageStatus.AwaitingApproval, "gone");

        var detail = (await query.LoadTicketAsync("gone", "GONE-1", default))!;

        Assert.False(detail.CanApprove);
        Assert.False(detail.CanSendBack);
        Assert.False(detail.CanUnblock);
        Assert.Empty(detail.SendBackTargets);
    }

    [Fact]
    public async Task Unknown_stage_in_known_project_detail_offers_only_send_back_to_any_stage()
    {
        await Seed("ORD-1", "renamed", StageStatus.Blocked);
        await Seed("ORD-2", "renamed", StageStatus.Running);

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;
        var running = (await query.LoadTicketAsync("orders", "ORD-2", default))!;

        Assert.False(detail.CanApprove || detail.CanUnblock);
        Assert.True(detail.CanSendBack);
        Assert.Equal(["triage", "plan", "implement"], detail.SendBackTargets);
        Assert.False(running.CanSendBack);
    }

    private static GateVerdict Verdict(RiskLevel risk) => new(risk, "summary", ["why"], ["c"], []);

    [Fact]
    public async Task Card_carries_risk_and_reviewing()
    {
        home.WriteProject("auto.yaml", TestHome.ReviewedProjectYaml("auto", "active"));
        registry.Reload();
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, with: t => t.Risk = RiskLevel.High);
        await Seed("AUTO-1", "implement", StageStatus.AwaitingApproval, "auto", t => t.ReviewDue = true);
        await Seed("AUTO-2", "implement", StageStatus.AwaitingApproval, "auto"); // review finished: waits on you

        var board = await query.LoadBoardAsync(null, default);
        var cards = board.Columns.SelectMany(c => c.Cards).ToDictionary(c => c.Key);

        Assert.Equal(RiskLevel.High, cards["ORD-1"].Risk);
        Assert.Null(cards["AUTO-2"].Risk);
        Assert.True(cards["AUTO-1"].Reviewing);
        Assert.False(cards["AUTO-1"].WaitingOnYou);
        Assert.True(cards["ORD-1"].WaitingOnYou);
        Assert.False(cards["ORD-1"].Reviewing);
        Assert.True(cards["AUTO-2"].WaitingOnYou);
        Assert.Equal(2, board.Counts.WaitingOnYou);
        Assert.Equal(1, (await query.LoadBoardAsync("auto", default)).Counts.WaitingOnYou); // AUTO-1 is with the reviewer
    }

    [Fact]
    public async Task Review_owed_on_a_human_gate_is_not_reviewing()
    {
        home.WriteProject("auto.yaml", TestHome.ReviewedProjectYaml("auto", "shadow"));
        registry.Reload();
        await Seed("AUTO-1", "implement", StageStatus.AwaitingApproval, "auto", t => t.ReviewDue = true);

        var card = (await query.LoadBoardAsync(null, default)).Columns.SelectMany(c => c.Cards).Single();

        Assert.False(card.Reviewing); // shadow mode: the card waits on the developer from the start
        Assert.True(card.WaitingOnYou);
    }

    [Fact]
    public async Task Project_carries_review_mode_and_agreement()
    {
        home.WriteProject("auto.yaml", TestHome.ReviewedProjectYaml("auto", "shadow"));
        registry.Reload();
        var t = await Seed("AUTO-1", "implement", StageStatus.AwaitingApproval, "auto");
        foreach (var (round, decision) in new[] { (1, GateDecision.Approved), (2, GateDecision.SentBack) })
        {
            var since = Now.AddHours(round);
            await store.AddGateReviewAsync(new GateReview(t.Id, "auto", "implement", since, since, ReviewMode.Shadow,
                GatePolicy.AutoWhenLowRisk, Verdict(RiskLevel.Low)), default);
            await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "auto", "implement", since, decision, false, since), default);
        }

        var projects = (await query.LoadBoardAsync(null, default)).Projects;

        var auto = projects.Single(p => p.Id == "auto");
        Assert.Equal(ReviewMode.Shadow, auto.Review);
        var agreement = Assert.Single(auto.Agreement!);
        Assert.Equal(("implement", 2, 1, 1), (agreement.StageId, agreement.Decided, agreement.Agreed, agreement.FalseLows));
        Assert.Equal(ReviewMode.Off, projects.Single(p => p.Id == "orders").Review);
        Assert.Empty(projects.Single(p => p.Id == "orders").Agreement!);
    }

    [Fact]
    public async Task Detail_carries_gate_reviews_newest_first()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        foreach (var hours in new[] { 1, 3, 2 })
            await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Now.AddHours(hours), Now.AddHours(hours),
                ReviewMode.Shadow, GatePolicy.Human, Verdict(RiskLevel.High)), default);

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;

        Assert.Equal([Now.AddHours(3), Now.AddHours(2), Now.AddHours(1)], detail.Reviews.Select(r => r.At));
    }

    [Fact]
    public async Task Shell_lists_every_project_with_its_counts()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("ORD-2", "implement", StageStatus.Running);
        await Seed("ORD-3", "done", StageStatus.Done);
        await Seed("BIL-1", "triage", StageStatus.Blocked, "billing");

        var shell = await query.LoadShellAsync(default);

        Assert.Equal(["billing", "orders"], shell.Projects.Select(p => p.Id));
        var orders = shell.Projects.Single(p => p.Id == "orders");
        Assert.Equal((1, 2), (orders.WaitingOnYou, orders.Active));
        var billing = shell.Projects.Single(p => p.Id == "billing");
        Assert.Equal((0, 1), (billing.WaitingOnYou, billing.Active));
        Assert.Equal(new BoardCounts(1, 1, 1), shell.Totals);
    }

    [Fact]
    public async Task Shell_reports_pause_and_problem()
    {
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set; run: symphonist secret set jira-acme");
        await orchestrator.TickAsync(CancellationToken.None);
        registry.SetPaused("orders", true);

        var projects = (await query.LoadShellAsync(default)).Projects;

        var orders = projects.Single(p => p.Id == "orders");
        Assert.Contains("secret", orders.Problem);
        Assert.True(orders.Paused);
        Assert.False(projects.Single(p => p.Id == "billing").Paused);
    }

    [Fact]
    public async Task Card_carries_its_stage_position()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        await Seed("ORD-2", "implement", StageStatus.Running);
        await Seed("ORD-3", "done", StageStatus.Done);
        await Seed("ORD-4", "nope", StageStatus.Pending);

        var cards = (await query.LoadBoardAsync(null, default)).Columns.SelectMany(c => c.Cards).ToDictionary(c => c.Key);

        Assert.Equal((2, 4), (cards["ORD-1"].StageNumber, cards["ORD-1"].StageCount));
        Assert.Equal((3, 4), (cards["ORD-2"].StageNumber, cards["ORD-2"].StageCount));
        Assert.Equal((null, null), (cards["ORD-3"].StageNumber, cards["ORD-3"].StageCount));
        Assert.Equal((null, null), (cards["ORD-4"].StageNumber, cards["ORD-4"].StageCount));
    }

    [Fact]
    public async Task Columns_carry_their_stage_kind()
    {
        var filtered = await query.LoadBoardAsync("orders", default);
        await Seed("ORD-1", "nope", StageStatus.Pending); // gives the all-projects board an unknown column
        var all = await query.LoadBoardAsync(null, default);

        Assert.Equal([StageKind.Triage, StageKind.Plan, StageKind.Implement, StageKind.Done], filtered.Columns.Select(c => c.Kind));
        Assert.All(all.Columns.Where(c => c.Id != BoardQuery.UnknownColumnId), c => Assert.Equal(c.Id, c.Kind.ToString()));
        Assert.Null(all.Columns.Single(c => c.Id == BoardQuery.UnknownColumnId).Kind);
    }

    [Fact]
    public async Task Detail_carries_the_project_pipeline()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        await Seed("GONE-1", "plan", StageStatus.Pending, "gone");

        var detail = (await query.LoadTicketAsync("orders", "ORD-1", default))!;
        var gone = (await query.LoadTicketAsync("gone", "GONE-1", default))!;

        Assert.Equal(["triage", "plan", "implement", "done"], detail.Pipeline.Select(s => s.Id));
        Assert.Equal("Project orders", detail.ProjectName);
        Assert.Empty(gone.Pipeline);
        Assert.Equal("gone", gone.ProjectName);
    }

    [Fact]
    public async Task Missing_ticket_detail_is_null()
    {
        Assert.Null(await query.LoadTicketAsync("orders", "NOPE-1", default));
    }
}
