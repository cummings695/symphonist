using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
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
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), store, time), time,
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
        Assert.Null(cards.Single(c => c.Key == "ORD-2").CostUsd);
    }

    [Fact]
    public async Task Ticket_with_unknown_stage_goes_to_unknown_column()
    {
        await Seed("ORD-1", "review", StageStatus.Pending);

        var last = (await query.LoadBoardAsync(null, default)).Columns[^1];

        Assert.Equal((BoardQuery.UnknownColumnId, "Unknown stage"), (last.Id, last.Title));
        Assert.Equal(["ORD-1"], Keys(last));
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
}
