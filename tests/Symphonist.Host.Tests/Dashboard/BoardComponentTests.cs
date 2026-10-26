using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
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
            new StageExecutor(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner> { [AgentTypes.ClaudeCode] = new FakeAgentRunner() }), store, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);

        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton(orchestrator);
        ctx.Services.AddSingleton<BoardQuery>();
        ctx.Services.AddScoped<DashboardChrome>();
    }

    public void Dispose()
    {
        ctx.Dispose();
        home.Dispose();
    }

    private async Task<Ticket> Seed(string key, string stage, StageStatus status, string project = "orders", string? title = null,
        Action<Ticket>? with = null)
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = project, Key = key, Title = title ?? $"Title {key}", StageId = stage,
            Status = status, Attempts = 1, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now, WaitingSince = Now,
        };
        with?.Invoke(t);
        await store.SaveAsync(t, default);
        return t;
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

    /// <summary>The status line now belongs to the layout: what the board reported to it.</summary>
    private string StatusLine() => ctx.Services.GetRequiredService<DashboardChrome>().Message ?? "";

    [Fact]
    public async Task Renders_columns_and_cards()
    {
        await SeedDefault();

        var cut = RenderBoard();

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
    public async Task Waiting_card_in_unknown_stage_has_no_approve()
    {
        await Seed("ORD-1", "review", StageStatus.AwaitingApproval);
        await Seed("GONE-1", "plan", StageStatus.AwaitingApproval, "gone");

        var cut = RenderBoard();

        foreach (var key in new[] { "ORD-1", "GONE-1" })
        {
            Assert.Contains("Waiting on you", Card(cut, key).TextContent);
            Assert.Empty(Card(cut, key).QuerySelectorAll("button"));
        }
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
            Assert.Contains("implement approved", StatusLine());
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

        cut.WaitForAssertion(() => Assert.Contains("implement is not awaiting approval", StatusLine()));
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

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: plan is not awaiting approval", StatusLine()));
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
            Assert.Contains("ORD-5: approved, but a follow-up step failed: database is locked", StatusLine()));
        Assert.Equal(StageStatus.Approved, store.Single("ORD-5").Status);
    }

    [Fact]
    public async Task Approve_with_missing_secret_shows_the_outcome()
    {
        await Seed("ORD-5", "plan", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set");

        Card(cut, "ORD-5").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-5: advanced to implement", StatusLine()));
        Assert.Equal("implement", store.Single("ORD-5").StageId);
        Assert.Contains("tracker sync skipped: secret 'jira-acme' not set", store.EventMessages("ORD-5"));
    }

    [Fact]
    public async Task Approve_with_a_newer_round_waiting_changes_nothing()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        var again = store.Single("ORD-1"); // sent back and waiting again at the same gate: a newer round
        again.WaitingSince = Now.AddMinutes(30);
        await store.SaveAsync(again, default);

        Card(cut, "ORD-1").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: implement is waiting on a newer round", StatusLine()));
        Assert.Equal((StageStatus.AwaitingApproval, Now.AddMinutes(30)),
            (store.Single("ORD-1").Status, store.Single("ORD-1").WaitingSince));
    }

    [Fact]
    public async Task Approve_sends_the_round_it_showed()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();

        Card(cut, "ORD-1").QuerySelector("button")!.Click(); // still the round the card showed

        cut.WaitForAssertion(() => Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status));
    }

    [Fact]
    public async Task Risk_badge_is_labelled()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, with: t => t.Risk = RiskLevel.High);
        await Seed("ORD-2", "plan", StageStatus.AwaitingApproval, with: t => t.Risk = RiskLevel.Low);
        await Seed("ORD-3", "plan", StageStatus.Pending);

        var cut = RenderBoard();

        Assert.Equal("High risk", Card(cut, "ORD-1").QuerySelector(".badge-risk-high")!.TextContent.Trim());
        Assert.Equal("Low risk", Card(cut, "ORD-2").QuerySelector(".badge-risk-low")!.TextContent.Trim());
        Assert.Empty(Card(cut, "ORD-3").QuerySelectorAll("[class*=badge-risk]"));
    }

    [Fact]
    public async Task Reviewing_card_has_no_approve_button()
    {
        home.WriteProject("auto.yaml", TestHome.ReviewedProjectYaml("auto", "active"));
        registry.Reload();
        await Seed("AUTO-1", "implement", StageStatus.AwaitingApproval, "auto", with: t => t.ReviewDue = true);

        var cut = RenderBoard();

        var card = Card(cut, "AUTO-1");
        Assert.Contains("Reviewing", card.QuerySelector(".status-label")!.TextContent);
        Assert.DoesNotContain("Waiting on you", card.TextContent);
        Assert.Empty(card.QuerySelectorAll("button"));
        Assert.DoesNotContain("card-awaitingapproval", card.ClassList);
    }

    [Fact]
    public async Task Project_bar_shows_agreement_numbers()
    {
        home.WriteProject("auto.yaml", TestHome.ReviewedProjectYaml("auto", "shadow"));
        registry.Reload();
        var t = await Seed("AUTO-1", "implement", StageStatus.AwaitingApproval, "auto");
        var verdict = new GateVerdict(RiskLevel.Low, "s", [], ["c"], []);
        foreach (var (hours, decision) in new[] { (1, GateDecision.Approved), (2, GateDecision.SentBack) })
        {
            var since = Now.AddHours(hours);
            await store.AddGateReviewAsync(new GateReview(t.Id, "auto", "implement", since, since, ReviewMode.Shadow,
                GatePolicy.AutoWhenLowRisk, verdict), default);
            await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "auto", "implement", since, decision, false, since), default);
        }
        var autoAt = Now.AddHours(3);
        await store.AddGateReviewAsync(new GateReview(t.Id, "auto", "implement", autoAt, autoAt, ReviewMode.Active,
            GatePolicy.AutoWhenLowRisk, verdict), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "auto", "implement", autoAt, GateDecision.Approved, true, autoAt), default);

        var cut = RenderBoard("/?project=auto");

        var text = cut.Find("[data-project='auto']").TextContent;
        Assert.Contains("Reviewer: shadow", text);
        Assert.Contains("implement: agrees 1/2", text);
        Assert.Contains("1 low-risk call(s) sent back", text);
        Assert.Contains("1 auto-approved", text);
        var orders = RenderBoard("/?project=orders").Find("[data-project='orders']").TextContent;
        Assert.Contains("Reviewer: off", orders);
        Assert.DoesNotContain("agrees", orders);
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

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the board: database is locked", StatusLine()));
        Assert.Contains("Title ORD-1", Card(cut, "ORD-1").TextContent);

        notifier.Notify();
        time.Advance(BoardNotifier.Window);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Could not refresh", StatusLine()));
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
    public async Task Card_links_to_the_ticket_page()
    {
        await SeedDefault();

        Assert.Equal("/tickets/billing/BIL-1",
            Card(RenderBoard("/?project=billing"), "BIL-1").QuerySelector("a.card-key")?.GetAttribute("href"));
    }

    [Fact]
    public async Task Card_shows_stage_progress()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        time.Advance(TimeSpan.FromMinutes(14));

        var card = Card(RenderBoard(), "ORD-1");

        var progress = card.QuerySelector("[data-stage-progress]")!.TextContent;
        Assert.Contains("Stage 2 of 4", progress);
        Assert.Contains("waiting 14m", progress);
        Assert.Contains("width: 50%", card.QuerySelector(".progress-fill")!.GetAttribute("style"));
    }

    [Fact]
    public async Task Unknown_stage_card_has_no_progress_but_keeps_elapsed()
    {
        await Seed("ORD-1", "nope", StageStatus.Running);

        var card = Card(RenderBoard(), "ORD-1");

        Assert.Empty(card.QuerySelectorAll("[data-stage-progress]"));
        Assert.Contains("running", card.TextContent);
    }

    [Fact]
    public async Task Long_title_wraps_inside_the_card()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending, title: new string('x', 200));

        Assert.NotNull(Card(RenderBoard(), "ORD-1").QuerySelector(".card-title"));
    }

    [Fact]
    public void Paused_project_offers_resume()
    {
        registry.SetPaused("orders", true);
        var cut = RenderBoard("/?project=orders");

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
        var cut = RenderBoard("/?project=orders");

        cut.Find("[data-project='orders']").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Pause").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not pause Project orders:", StatusLine()));
        Assert.NotEmpty(cut.FindAll("[data-board-loaded]"));
    }

    [Fact]
    public void Host_mode_project_is_marked()
    {
        Assert.Contains("host mode", RenderBoard("/?project=billing").Find("[data-project='billing']").TextContent);
        Assert.DoesNotContain("host mode", RenderBoard("/?project=orders").Find("[data-project='orders']").TextContent);
    }

    [Fact]
    public async Task All_projects_view_has_a_heading_and_problem_notices()
    {
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set");
        await ctx.Services.GetRequiredService<Orchestrator>().TickAsync(CancellationToken.None);

        var cut = RenderBoard();

        Assert.Equal("All projects", Assert.Single(cut.FindAll("h1")).TextContent.Trim());
        var problem = cut.Find("[data-problem='billing']");
        Assert.Contains("Project billing:", problem.TextContent);
        Assert.Contains("secret 'jira-acme' not set", problem.TextContent);
        Assert.Empty(cut.FindAll("[data-project]"));
    }

    [Fact]
    public void Filtered_view_heading_is_the_project_name()
    {
        var cut = RenderBoard("/?project=orders");

        var h1 = Assert.Single(cut.FindAll("h1")); // one element in every state, so navigation focus survives loads
        Assert.Equal("Project orders", h1.TextContent.Trim());
        Assert.Null(h1.Closest("[data-project]"));
        Assert.NotNull(cut.Find("[data-project='orders']"));
        Assert.Equal("orders", ctx.Services.GetRequiredService<DashboardChrome>().CurrentProject);
    }

    [Fact]
    public void Columns_carry_lane_classes()
    {
        var cut = RenderBoard("/?project=orders");

        Assert.Contains("lane-plan", cut.Find("[data-column='plan']").ClassList);
    }

    [Fact]
    public void Board_has_no_project_dropdown()
    {
        var cut = RenderBoard();

        Assert.Empty(cut.FindAll("select"));
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
        Assert.Equal("All projects", cut.Find("h1").TextContent.Trim()); // the focus target stays
    }

    [Fact]
    public void Load_errors_show_as_banner_text()
    {
        home.WriteProject("bad.yaml", "id: <b>x</b>\n");
        registry.Reload();

        var cut = RenderBoard();

        var banner = cut.Find("[data-load-error='bad.yaml']");
        Assert.StartsWith("bad.yaml skipped: ", banner.TextContent.Trim());
        Assert.Contains("invalid id '<b>x</b>'", banner.TextContent);
        Assert.Empty(banner.QuerySelectorAll("b"));
    }

    [Fact]
    public void Reload_button_reloads_projects_and_reports_skipped_files()
    {
        var cut = RenderBoard();
        Assert.Empty(cut.FindAll("[data-load-error]"));
        home.WriteProject("shop.yaml", TestHome.ProjectYaml("shop"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reload projects").Click();

        cut.WaitForAssertion(() => Assert.Equal("Reloaded 3 project(s)", StatusLine()));
        Assert.Contains("shop", registry.Projects.Select(p => p.Id));

        home.WriteProject("bad.yaml", "id: bad\n");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reload projects").Click();

        cut.WaitForAssertion(() => Assert.Equal("1 project file(s) skipped", StatusLine()));
        cut.WaitForAssertion(() => Assert.Contains("missing required", cut.Find("[data-load-error='bad.yaml']").TextContent));
    }

    [Fact]
    public void Board_links_to_add_project_and_project_setup()
    {
        var cut = RenderBoard();
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/projects/new" && a.TextContent.Trim() == "Add project");
        Assert.Empty(cut.FindAll("a[aria-label^='Set up']"));

        cut = RenderBoard("/?project=orders");
        var setup = cut.Find("a[aria-label='Set up Project orders']");
        Assert.Equal("/projects/orders/setup", setup.GetAttribute("href"));
        Assert.Equal("Setup", setup.TextContent.Trim());
    }

    /// <summary>The browser side of folding: at phone width, with <paramref name="stored"/> in localStorage.</summary>
    private BunitJSModuleInterop SetupFolds(Dictionary<string, bool>? stored = null)
    {
        var module = ctx.JSInterop.SetupModule("./js/board-folds.js");
        module.Setup<Dictionary<string, bool>?>("load", _ => true).SetResult(stored);
        module.Setup<bool>("watchPhone", _ => true).SetResult(true);
        module.Setup<bool>("save", _ => true).SetResult(true);
        return module;
    }

    private static IElement Column(IRenderedComponent<Board> cut, string id) => cut.Find($"[data-column='{id}']");

    private static IElement Toggle(IRenderedComponent<Board> cut, string id) =>
        cut.Find($"[data-column='{id}'] .column-header button");

    [Fact]
    public async Task Tapping_a_stage_header_folds_and_unfolds_it()
    {
        await SeedDefault();
        var module = SetupFolds();
        var cut = RenderBoard();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["watchPhone"]));

        Assert.Equal("true", Toggle(cut, "Plan").GetAttribute("aria-expanded"));
        Assert.Equal("cards-Plan", Toggle(cut, "Plan").GetAttribute("aria-controls"));
        Assert.NotNull(cut.Find("#cards-Plan"));

        Toggle(cut, "Plan").Click();
        Assert.Equal("false", Toggle(cut, "Plan").GetAttribute("aria-expanded"));
        Assert.Contains("collapsed", Column(cut, "Plan").ClassList);

        Toggle(cut, "Plan").Click();
        Assert.Equal("true", Toggle(cut, "Plan").GetAttribute("aria-expanded"));
        Assert.DoesNotContain("collapsed", Column(cut, "Plan").ClassList);
        Assert.Equal(2, module.Invocations["save"].Count);
        Assert.Equal(new Dictionary<string, bool> { ["Plan"] = false },
            module.Invocations["save"][^1].Arguments[0] as Dictionary<string, bool>);
    }

    [Fact]
    public async Task Done_stage_starts_folded_and_empty_stages_are_not_toggles()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        await Seed("ORD-9", "done", StageStatus.Done);

        var cut = RenderBoard();

        Assert.Contains("collapsed", Column(cut, "Done").ClassList);
        Assert.Equal("false", Toggle(cut, "Done").GetAttribute("aria-expanded"));
        Assert.DoesNotContain("collapsed", Column(cut, "Plan").ClassList);
        Assert.Empty(cut.FindAll("[data-column='Triage'] button"));
    }

    [Fact]
    public async Task Folded_stage_shows_how_many_wait_on_you()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("ORD-2", "plan", StageStatus.Pending);
        SetupFolds();
        var cut = RenderBoard();
        Assert.Empty(cut.FindAll("[data-column='Plan'] .column-waiting"));

        Toggle(cut, "Plan").Click();

        var badge = cut.Find("[data-column='Plan'] .column-waiting");
        Assert.Equal("1 waiting", badge.TextContent.Trim());
        Assert.Contains("count-waiting", badge.ClassList);
    }

    [Fact]
    public async Task Stored_folds_apply_after_first_render()
    {
        await SeedDefault();
        await Seed("ORD-9", "done", StageStatus.Done);
        SetupFolds(new Dictionary<string, bool> { ["Plan"] = true, ["Done"] = false });

        var cut = RenderBoard();

        cut.WaitForAssertion(() => Assert.Contains("collapsed", Column(cut, "Plan").ClassList));
        Assert.DoesNotContain("collapsed", Column(cut, "Done").ClassList);
        Assert.Equal("true", Toggle(cut, "Done").GetAttribute("aria-expanded"));
        Assert.Equal("2 waiting", cut.Find("[data-column='Plan'] .column-waiting").TextContent.Trim());
    }

    [Fact]
    public async Task Desktop_width_folds_every_stage_including_empty_ones()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        var module = ctx.JSInterop.SetupModule("./js/board-folds.js");
        module.Setup<Dictionary<string, bool>?>("load", _ => true).SetResult(null);
        module.Setup<bool>("watchPhone", _ => true).SetResult(false);
        module.Setup<bool>("save", _ => true).SetResult(true);

        var cut = RenderBoard();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-column='Triage'] .column-header button")));
        Assert.Equal("Plan", cut.Find("[data-column='Plan'] .column-title").TextContent.Trim());

        Assert.Contains("collapsed", Column(cut, "Triage").ClassList); // empty: folded by default
        Assert.DoesNotContain("collapsed", Column(cut, "Plan").ClassList);

        Toggle(cut, "Triage").Click(); // opening an empty stage lasts only for this page
        Assert.DoesNotContain("collapsed", Column(cut, "Triage").ClassList);
        Assert.Empty(module.Invocations["save"]);

        Toggle(cut, "Plan").Click();
        Assert.Contains("collapsed", Column(cut, "Plan").ClassList);
        Assert.Equal(new Dictionary<string, bool> { ["Plan"] = true },
            module.Invocations["save"][^1].Arguments[0] as Dictionary<string, bool>);
    }

    [Fact]
    public async Task Empty_stages_fold_whatever_was_stored_until_they_have_tickets()
    {
        await Seed("ORD-1", "plan", StageStatus.Pending);
        var module = ctx.JSInterop.SetupModule("./js/board-folds.js");
        module.Setup<Dictionary<string, bool>?>("load", _ => true)
            .SetResult(new Dictionary<string, bool> { ["Triage"] = false, ["Implement"] = true, ["Done"] = false });
        module.Setup<bool>("watchPhone", _ => true).SetResult(false);
        module.Setup<bool>("save", _ => true).SetResult(true);

        var cut = RenderBoard();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["load"]));

        Assert.Contains("collapsed", Column(cut, "Triage").ClassList); // stored open, but empty
        Assert.Contains("collapsed", Column(cut, "Implement").ClassList);
        Assert.DoesNotContain("collapsed", Column(cut, "Done").ClassList); // Done always follows the choice, even empty

        await Seed("ORD-2", "triage", StageStatus.Pending);
        await Seed("ORD-3", "implement", StageStatus.Pending);
        notifier.Notify();
        time.Advance(BoardNotifier.Window);
        cut.WaitForAssertion(() => Assert.DoesNotContain("collapsed", Column(cut, "Triage").ClassList));
        Assert.Contains("collapsed", Column(cut, "Implement").ClassList); // back to the stored choice
    }

    [Fact]
    public async Task Folding_survives_a_failing_browser()
    {
        await SeedDefault();
        await Seed("ORD-9", "done", StageStatus.Done);
        var module = ctx.JSInterop.SetupModule("./js/board-folds.js");
        module.Setup<Dictionary<string, bool>?>("load", _ => true).SetException(new JSException("storage is blocked"));

        var cut = RenderBoard();

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["load"]));
        Assert.Contains("Title ORD-1", Card(cut, "ORD-1").TextContent);
        Assert.Contains("collapsed", Column(cut, "Done").ClassList);
        Toggle(cut, "Plan").Click(); // nothing to store into: still folds
        Assert.Contains("collapsed", Column(cut, "Plan").ClassList);
    }

    /// <summary>Waits for <paramref name="done"/>, then lets every continuation queued on the renderer run.</summary>
    private async Task SettleAfter(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done());
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(10);
            await ctx.Renderer.Dispatcher.InvokeAsync(() => { });
        }
    }

    [Fact]
    public async Task Approve_finishing_after_the_board_is_gone_leaves_the_frame_alone()
    {
        await Seed("ORD-5", "implement", StageStatus.AwaitingApproval);
        var cut = RenderBoard();
        var chrome = ctx.Services.GetRequiredService<DashboardChrome>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.HoldSaves = hold;
        Card(cut, "ORD-5").QuerySelector("button")!.Click(); // in flight: the approval's save waits

        await ctx.DisposeComponentsAsync(); // navigated away: the next page has its own message and errors
        chrome.ClearForNavigation();
        store.HoldSaves = null;
        hold.SetResult();

        await SettleAfter(() => store.Single("ORD-5").Status == StageStatus.Approved);
        Assert.Null(chrome.Message);
    }

    [Fact]
    public void Arriving_from_a_ticket_marks_the_board_before_it_loads()
    {
        var chrome = ctx.Services.GetRequiredService<DashboardChrome>();
        chrome.SetPage("ORD-1", "orders"); // the ticket page's label and highlight
        store.FailNextList = true;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/?project=billing");

        var cut = ctx.Render<Board>();

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the board", StatusLine()));
        Assert.Null(chrome.PageLabel);
        Assert.Equal("billing", chrome.CurrentProject);
    }

    [Fact]
    public void Switching_projects_keeps_the_old_project_bar_off_the_new_heading()
    {
        var cut = RenderBoard("/?project=orders");
        Assert.NotNull(cut.Find("[data-project='orders']"));
        store.FailNextList = true; // the switch's load fails: the orders board stays

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/?project=billing");

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the board", StatusLine()));
        Assert.Equal("Project billing", cut.Find("h1").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-project]"));
        Assert.Equal("billing", ctx.Services.GetRequiredService<DashboardChrome>().CurrentProject);
    }

    [Fact]
    public async Task Card_shows_gathering_evidence_and_still_waits_on_you()
    {
        await Seed("ORD-9", "implement", StageStatus.AwaitingApproval, with: t => { t.EvidenceDue = true; t.PullRequestId = 7; });

        var cut = RenderBoard();

        var card = Card(cut, "ORD-9");
        Assert.Equal("Gathering evidence", card.QuerySelector("[data-card-evidence]")!.TextContent.Trim());
        Assert.DoesNotContain("Waiting on you", card.TextContent);
        Assert.Contains(card.QuerySelectorAll("button"), b => b.TextContent.Trim() == "Approve");
        Assert.Empty(Card(cut, "ORD-9").QuerySelectorAll("[data-evidence-issues]"));
    }

    [Fact]
    public async Task Card_shows_evidence_issues_marker()
    {
        var bad = await Seed("ORD-8", "implement", StageStatus.AwaitingApproval);
        var good = await Seed("ORD-7", "implement", StageStatus.AwaitingApproval);
        await store.AddEvidenceAsync(new EvidenceRecord(bad.Id, "orders", "implement", Now, Now, "abc", null, true, null, [], true), default);
        await store.AddEvidenceAsync(new EvidenceRecord(good.Id, "orders", "implement", Now, Now, "abc", null, false, null, [], true), default);

        var cut = RenderBoard();

        Assert.Equal("Evidence: issues found", Card(cut, "ORD-8").QuerySelector("[data-evidence-issues]")!.TextContent.Trim());
        Assert.Empty(Card(cut, "ORD-7").QuerySelectorAll("[data-evidence-issues]"));
        Assert.Contains("Waiting on you", Card(cut, "ORD-8").QuerySelector(".status-label")!.TextContent);
    }
}
