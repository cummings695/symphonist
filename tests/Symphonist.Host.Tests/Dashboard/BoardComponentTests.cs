using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Components.Pages;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class BoardComponentTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly BoardNotifier notifier;
    private readonly ProjectRegistry registry;
    private readonly FakeIntegrationFactory integrations = new(
        new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));

    public BoardComponentTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", TestHome.HostAgent));
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        notifier = new BoardNotifier(time);
        var orchestrator = new Orchestrator(registry, store, integrations,
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);

        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton(orchestrator);
        ctx.Services.AddSingleton<BoardQuery>();
    }

    public void Dispose()
    {
        ctx.Dispose();
        home.Dispose();
    }

    private async Task Seed(string key, string stage, StageStatus status, string project = "orders", string? title = null)
    {
        await store.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = project, Key = key, Title = title ?? $"Title {key}", StageId = stage,
            Status = status, Attempts = 1, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now, WaitingSince = Now,
        }, default);
    }

    private async Task SeedDefault()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("ORD-2", "implement", StageStatus.Running);
        await Seed("ORD-3", "implement", StageStatus.Blocked);
        await Seed("ORD-4", "triage", StageStatus.Pending);
        await Seed("BIL-1", "plan", StageStatus.AwaitingApproval, "billing");
    }

    private IRenderedComponent<Board> RenderBoard(string uri = "/")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var cut = ctx.Render<Board>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-board-loaded]")));
        return cut;
    }

    private static IElement Card(IRenderedComponent<Board> cut, string key) => cut.Find($"[data-key='{key}']");

    private static string StatusLine(IRenderedComponent<Board> cut) => cut.Find("[role=status]").TextContent;

    [Fact]
    public async Task Renders_columns_cards_and_counts()
    {
        await SeedDefault();

        var cut = RenderBoard();

        Assert.Contains("2 waiting on you · 1 running · 1 blocked", cut.Find(".counts").TextContent);
        Assert.Equal(["Triage", "Plan", "Implement", "Done"],
            cut.FindAll(".column-title").Select(e => e.TextContent.Trim()));
        Assert.Contains("Title ORD-1", Card(cut, "ORD-1").TextContent);
        Assert.Equal("/tickets/orders/ORD-1", Card(cut, "ORD-1").QuerySelector("a.card-key")?.GetAttribute("href"));
    }

    [Fact]
    public async Task Waiting_card_is_labelled_and_has_approve()
    {
        await SeedDefault();

        var cut = RenderBoard();

        var waiting = Card(cut, "ORD-1");
        Assert.Contains("Waiting on you", waiting.TextContent);
        Assert.Contains(waiting.QuerySelectorAll("button"), b => b.TextContent.Trim() == "Approve");
        var pending = Card(cut, "ORD-4");
        Assert.DoesNotContain("Waiting on you", pending.TextContent);
        Assert.Empty(pending.QuerySelectorAll("button"));
    }

    [Fact]
    public async Task Approve_calls_orchestrator_and_shows_outcome()
    {
        await Seed("ORD-5", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();

        Card(cut, "ORD-5").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(StageStatus.Approved, store.Single("ORD-5").Status);
            Assert.Contains("implement approved", StatusLine(cut));
            Assert.DoesNotContain("Waiting on you", Card(cut, "ORD-5").TextContent);
        });
    }

    [Fact]
    public async Task Approve_on_stale_card_shows_outcome_message()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        var stale = store.Single("ORD-1");
        stale.Status = StageStatus.Approved;
        await store.SaveAsync(stale, default);

        Card(cut, "ORD-1").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("implement is not awaiting approval", StatusLine(cut)));
    }

    [Fact]
    public async Task Approve_on_card_whose_ticket_reached_a_later_gate_leaves_that_gate_alone()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        var moved = store.Single("ORD-1"); // approved elsewhere; now waits at the next gate
        moved.StageId = "implement";
        await store.SaveAsync(moved, default);

        Card(cut, "ORD-1").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: plan is not awaiting approval", StatusLine(cut)));
        Assert.Equal(("implement", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact]
    public async Task Approve_stored_but_a_follow_up_step_failed_says_so()
    {
        await Seed("ORD-5", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        store.FailNextAddEvent = true; // the approval is saved, recording its event fails

        Card(cut, "ORD-5").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("ORD-5: approved, but a follow-up step failed: database is locked", StatusLine(cut)));
        Assert.Equal(StageStatus.Approved, store.Single("ORD-5").Status);
    }

    [Fact]
    public async Task Approve_with_missing_secret_shows_the_outcome()
    {
        await Seed("ORD-5", "plan", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set");

        Card(cut, "ORD-5").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-5: advanced to implement", StatusLine(cut)));
        Assert.Equal("implement", store.Single("ORD-5").StageId);
        Assert.Contains("tracker sync skipped: secret 'jira-acme' not set", store.EventMessages("ORD-5"));
    }

    [Fact]
    public async Task Board_refreshes_when_notified()
    {
        await SeedDefault();
        var cut = RenderBoard();
        await Seed("ORD-9", "plan", StageStatus.Pending);

        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        cut.WaitForAssertion(() => Assert.Contains("Title ORD-9", Card(cut, "ORD-9").TextContent));
    }

    [Fact]
    public async Task Refresh_failure_shows_banner_and_keeps_board()
    {
        await SeedDefault();
        var cut = RenderBoard();
        store.FailNextList = true;

        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the board: database is locked", StatusLine(cut)));
        Assert.Contains("Title ORD-1", Card(cut, "ORD-1").TextContent);

        notifier.Notify();
        time.Advance(BoardNotifier.Window);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Could not refresh", StatusLine(cut)));
    }

    [Fact]
    public async Task Filter_shows_one_project_pipeline()
    {
        await SeedDefault();

        var cut = RenderBoard("/?project=billing");

        Assert.Equal(["triage", "plan", "implement", "done"],
            cut.FindAll("[data-column]").Select(e => e.GetAttribute("data-column")));
        Assert.Empty(cut.FindAll("[data-project-card='orders']"));
        Assert.Single(cut.FindAll("[data-key='BIL-1']"));
        Assert.Equal(["billing"], cut.FindAll("[data-project]").Select(e => e.GetAttribute("data-project")));
    }

    [Fact]
    public async Task Filtered_board_links_carry_the_filter_to_the_detail_page()
    {
        await SeedDefault();

        Assert.Equal("/tickets/billing/BIL-1?from=billing",
            Card(RenderBoard("/?project=billing"), "BIL-1").QuerySelector("a.card-key")?.GetAttribute("href"));
    }

    [Fact]
    public void Paused_project_offers_resume()
    {
        registry.SetPaused("orders", true);
        var cut = RenderBoard();

        var row = cut.Find("[data-project='orders']");
        Assert.Contains("Paused", row.TextContent);
        var resume = row.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Resume");
        resume.Click();

        Assert.False(registry.IsPaused("orders"));
        cut.WaitForAssertion(() => Assert.Contains(cut.Find("[data-project='orders']").QuerySelectorAll("button"),
            b => b.TextContent.Trim() == "Pause"));
    }

    [Fact]
    public void Pause_that_cannot_be_saved_shows_the_error()
    {
        Directory.CreateDirectory(SymphonistPaths.Resolve("paused.json")); // a directory in its place: the write fails
        var cut = RenderBoard();

        cut.Find("[data-project='orders']").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Pause").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not pause Project orders:", StatusLine(cut)));
        Assert.NotEmpty(cut.FindAll("[data-board-loaded]"));
    }

    [Fact]
    public void Host_mode_project_is_marked()
    {
        var cut = RenderBoard();

        Assert.Contains("host mode", cut.Find("[data-project='billing']").TextContent);
        Assert.DoesNotContain("host mode", cut.Find("[data-project='orders']").TextContent);
    }

    [Fact]
    public async Task Ticket_text_is_not_rendered_as_markup()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending, title: "<b>x</b>");

        var cut = RenderBoard();

        Assert.Contains("&lt;b&gt;", cut.Markup);
        Assert.Empty(cut.FindAll("b"));
    }

    [Fact]
    public void No_projects_shows_setup_hint()
    {
        foreach (var f in Directory.GetFiles(home.Projects)) File.Delete(f);
        registry.Reload();

        var cut = RenderBoard();

        Assert.Contains("No projects configured", cut.Markup);
        Assert.Contains(SymphonistPaths.Resolve("projects"), cut.Find(".setup-hint").TextContent);
    }
}
