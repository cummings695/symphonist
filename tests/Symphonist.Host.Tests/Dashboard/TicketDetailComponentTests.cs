using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
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
public sealed class TicketDetailComponentTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly BoardNotifier notifier;
    private readonly FakeIntegrationFactory integrations = new(
        new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));

    public TicketDetailComponentTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        notifier = new BoardNotifier(time);
        var orchestrator = new Orchestrator(registry, store, integrations,
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), store, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);

        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton(orchestrator);
        ctx.Services.AddSingleton<BoardQuery>();
        ctx.Services.AddScoped<DashboardChrome>();
    }

    private DashboardChrome Chrome => ctx.Services.GetRequiredService<DashboardChrome>();

    public void Dispose()
    {
        ctx.Dispose();
        home.Dispose();
    }

    private async Task<Ticket> Seed(string key, string stage, StageStatus status, Action<Ticket>? with = null)
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = $"Title {key}", StageId = stage, Status = status,
            Attempts = 1, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now, WaitingSince = Now,
        };
        with?.Invoke(t);
        await store.SaveAsync(t, default);
        return t;
    }

    private IRenderedComponent<TicketDetail> Render(string key = "ORD-1", string project = "orders")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"/tickets/{project}/{key}");
        return ctx.Render<TicketDetail>(p => p.Add(c => c.Project, project).Add(c => c.Key, key));
    }

    private static IRenderedComponent<TicketDetail> Loaded(IRenderedComponent<TicketDetail> cut)
    {
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-ticket-loaded]")));
        return cut;
    }

    private string StatusLine(IRenderedComponent<TicketDetail> _) => Chrome.Message ?? "";

    /// <summary>The page's scoped stylesheet, read from the source tree.</summary>
    private static string PageCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Symphonist.slnx"))) dir = dir.Parent!;
        return File.ReadAllText(Path.Combine(dir.FullName, "src", "Symphonist.Host", "Components", "Pages", "TicketDetail.razor.css"));
    }

    /// <summary>The declarations of every rule whose selector list includes <paramref name="selector"/>.</summary>
    private static IEnumerable<string> RulesFor(string css, string selector) =>
        System.Text.RegularExpressions.Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}")
            .Where(m => m.Groups[1].Value.Split(',').Any(s => s.Trim() == selector))
            .Select(m => m.Groups[2].Value);

    private static IElement Button(IRenderedComponent<TicketDetail> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == text);

    [Fact]
    public async Task Send_back_requires_feedback()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());

        Assert.True(Button(cut, "Send back").HasAttribute("disabled"));
        cut.Find("textarea").Input("");
        Assert.True(Button(cut, "Send back").HasAttribute("disabled"));
        cut.Find("textarea").Input("   ");
        Assert.True(Button(cut, "Send back").HasAttribute("disabled"));
        cut.Find("textarea").Input("fix it");
        Assert.False(Button(cut, "Send back").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Send_back_to_selected_stage()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        Assert.Equal(["implement", "plan", "triage"], cut.FindAll("select option").Select(o => o.GetAttribute("value")));

        cut.Find("textarea").Input("use the v2 endpoint");
        cut.Find("select").Change("plan");
        Button(cut, "Send back").Click();

        cut.WaitForAssertion(() =>
        {
            var ticket = store.Single("ORD-1");
            Assert.Equal("plan", ticket.StageId);
            Assert.Equal("use the v2 endpoint", ticket.Feedback);
            Assert.Contains("ORD-1:", StatusLine(cut));
            Assert.Contains("use the v2 endpoint", cut.Find("[data-feedback]").TextContent);
        });
    }

    [Fact]
    public async Task Send_back_with_missing_secret_shows_the_outcome()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        integrations.Error = new InvalidOperationException("secret not set");

        cut.Find("textarea").Input("redo");
        Button(cut, "Send back").Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: sent back to implement", StatusLine(cut)));
        Assert.Contains("tracker sync skipped: secret not set", store.EventMessages("ORD-1"));
        Assert.Equal(("implement", StageStatus.Pending), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact]
    public async Task Send_back_stored_but_a_follow_up_step_failed_says_so()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        store.FailNextAddEvent = true;

        cut.Find("textarea").Input("redo");
        Button(cut, "Send back").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("ORD-1: sent back, but a follow-up step failed: database is locked", StatusLine(cut)));
    }

    [Fact]
    public async Task Approve_shows_outcome_and_reloads()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());

        Button(cut, "Approve").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(StageStatus.Approved, store.Single("ORD-1").Status);
            Assert.Contains("implement approved", StatusLine(cut));
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Approve");
        });
    }

    [Fact]
    public async Task Approve_on_stale_page_leaves_a_later_gate_alone()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        var moved = store.Single("ORD-1");
        moved.StageId = "implement";
        await store.SaveAsync(moved, default);

        Button(cut, "Approve").Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: plan is not awaiting approval", StatusLine(cut)));
        Assert.Equal(("implement", StageStatus.AwaitingApproval), (store.Single("ORD-1").StageId, store.Single("ORD-1").Status));
    }

    [Fact]
    public async Task Approve_stored_but_a_follow_up_step_failed_says_so()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        store.FailNextAddEvent = true;

        Button(cut, "Approve").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("ORD-1: approved, but a follow-up step failed: database is locked", StatusLine(cut)));
    }

    [Fact]
    public async Task Approve_with_missing_secret_shows_the_outcome()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        integrations.Error = new InvalidOperationException("secret not set");

        Button(cut, "Approve").Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: advanced to implement", StatusLine(cut)));
        Assert.Contains("tracker sync skipped: secret not set", store.EventMessages("ORD-1"));
    }

    [Fact]
    public async Task Approve_with_a_newer_round_waiting_changes_nothing()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        var again = store.Single("ORD-1");
        again.WaitingSince = Now.AddMinutes(30);
        await store.SaveAsync(again, default);

        Button(cut, "Approve").Click();

        cut.WaitForAssertion(() => Assert.Contains("ORD-1: implement is waiting on a newer round", StatusLine(cut)));
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact]
    public async Task Detail_shows_gate_reasoning_as_text()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.AwaitingApproval, x => x.Risk = RiskLevel.High);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "plan", Now.AddHours(-2), Now.AddHours(-2), ReviewMode.Shadow,
            GatePolicy.Human, new GateVerdict(RiskLevel.Low, "plan is small", ["tiny"], ["read the plan"], [])), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "plan", Now.AddHours(-2), GateDecision.Approved, false, Now.AddHours(-1)), default);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Now, Now, ReviewMode.Active,
            GatePolicy.AutoWhenLowRisk, new GateVerdict(RiskLevel.High, "<i>sum</i>", ["<b>x</b>", "second"], ["checked <u>a</u>", "checked b"], ["pricing"], "boom <s>")), default);

        var cut = Loaded(Render());

        var reviews = cut.FindAll("[data-gate-review]");
        Assert.Equal(2, reviews.Count);
        Assert.Contains("implement", reviews[0].TextContent); // newest first
        Assert.Equal("High risk", reviews[0].QuerySelector(".badge-risk-high")!.TextContent.Trim());
        Assert.Contains("undecided", reviews[0].TextContent);
        Assert.Contains("active", reviews[0].TextContent);
        Assert.Contains("<i>sum</i>", reviews[0].TextContent);
        Assert.Equal(["<b>x</b>", "second"], reviews[0].QuerySelectorAll("[data-reasons] li").Select(e => e.TextContent));
        Assert.Equal(["checked <u>a</u>", "checked b"], reviews[0].QuerySelectorAll("[data-checked] li").Select(e => e.TextContent));
        Assert.Equal(["pricing"], reviews[0].QuerySelectorAll("[data-areas] li").Select(e => e.TextContent));
        Assert.Contains("boom <s>", reviews[0].QuerySelector("[data-review-error]")!.TextContent);
        Assert.Contains("you approved", reviews[1].TextContent);
        Assert.Equal("Low risk", reviews[1].QuerySelector(".badge-risk-low")!.TextContent.Trim());
        Assert.Contains("read the plan", reviews[1].TextContent);
        Assert.Empty(cut.FindAll("[data-gate-review] b, [data-gate-review] i, [data-gate-review] u, [data-gate-review] s"));
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", cut.Markup);
    }

    [Fact]
    public async Task Detail_labels_every_outcome_and_the_empty_state()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending);
        Assert.Contains("No gate reviews yet.", Loaded(Render()).Markup);

        var t = await Seed("ORD-2", "implement", StageStatus.Pending);
        var verdict = new GateVerdict(RiskLevel.Low, "s", [], ["c"], []);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Now, Now, ReviewMode.Active, GatePolicy.AutoWhenLowRisk, verdict), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "implement", Now, GateDecision.Approved, true, Now), default);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Now.AddHours(1), Now.AddHours(1), ReviewMode.Shadow, GatePolicy.Human, verdict), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "implement", Now.AddHours(1), GateDecision.SentBack, false, Now.AddHours(1)), default);

        var cut = Loaded(Render("ORD-2"));

        Assert.Contains("you sent back", cut.FindAll("[data-gate-review]")[0].TextContent);
        Assert.Contains("auto-approved", cut.FindAll("[data-gate-review]")[1].TextContent);
        Assert.DoesNotContain("No gate reviews yet.", cut.Markup);
    }

    [Fact]
    public async Task Unblock_moves_ticket_to_pending()
    {
        await Seed("ORD-1", "implement", StageStatus.Blocked, t => t.BlockedReason = "implement failed 3 times: boom");
        var cut = Loaded(Render());
        Assert.Contains("implement failed 3 times: boom", cut.Find("[data-blocked-reason]").TextContent);

        Button(cut, "Unblock").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(StageStatus.Pending, store.Single("ORD-1").Status);
            Assert.Empty(cut.FindAll("[data-blocked-reason]"));
        });
    }

    [Fact]
    public async Task Run_summary_and_error_render_as_text()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Pending);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now.AddHours(-2), TimeSpan.FromSeconds(75), true, 0.5m, null, "logs/a.log", "<script>x</script>"), default);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "implement", Now.AddHours(-1), TimeSpan.FromSeconds(5), false, null, "<img src=x>", "logs/b.log"), default);

        var cut = Loaded(Render());

        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", cut.Markup);
        Assert.Contains("&lt;img src=x&gt;", cut.Markup);
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("img"));
        var rows = cut.FindAll("[data-run]");
        Assert.Equal(["failed", "succeeded"], rows.Select(r => r.QuerySelectorAll("td")[3].TextContent));
        Assert.Contains("logs/a.log", cut.Markup);
        Assert.Contains("1m 15s", rows[1].TextContent);
    }

    [Fact]
    public async Task Copy_path_calls_the_clipboard_module()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Pending);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(5), true, null, null, "logs/a.log"), default);
        var module = ctx.JSInterop.SetupModule("./js/clipboard.js");
        module.Setup<bool>("copyText", "logs/a.log").SetResult(true);
        var cut = Loaded(Render());

        Button(cut, "Copy path").Click();

        cut.WaitForAssertion(() => Assert.Contains("Copied the log path", StatusLine(cut)));
    }

    [Fact]
    public async Task Copy_path_failure_is_reported_not_thrown()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Pending);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(5), true, null, null, "logs/a.log"), default);
        var module = ctx.JSInterop.SetupModule("./js/clipboard.js");
        module.Setup<bool>("copyText", "logs/a.log").SetException(new JSException("denied"));
        var cut = Loaded(Render());

        Button(cut, "Copy path").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not copy the log path", StatusLine(cut)));
    }

    [Fact]
    public async Task Copy_path_timeout_is_reported_and_page_keeps_rendering()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Pending);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, "plan", Now, TimeSpan.FromSeconds(5), true, null, null, "logs/a.log"), default);
        var module = ctx.JSInterop.SetupModule("./js/clipboard.js");
        module.Setup<bool>("copyText", "logs/a.log").SetException(new TaskCanceledException());
        var cut = Loaded(Render());

        Button(cut, "Copy path").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not copy the log path", StatusLine(cut)));
        Assert.Contains("Title ORD-1", cut.Markup);
    }

    [Fact]
    public void Missing_ticket_shows_not_found()
    {
        var cut = Render("NOPE-1");

        cut.WaitForAssertion(() =>
            Assert.Contains("Ticket NOPE-1 was not found in project orders.", cut.Find(".not-found").TextContent));
        Assert.Equal("/?project=orders", cut.Find(".crumbs a").GetAttribute("href"));
    }

    [Fact]
    public async Task Breadcrumb_links_to_the_ticket_project()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending);

        var cut = Loaded(Render());

        var crumb = cut.Find(".crumbs a");
        Assert.Equal("/?project=orders", crumb.GetAttribute("href"));
        Assert.Equal("Project orders", crumb.TextContent.Trim());
        Assert.Equal("orders", Chrome.CurrentProject);
        Assert.Equal("ORD-1", Chrome.PageLabel);
    }

    [Fact]
    public async Task Stepper_marks_done_current_and_upcoming()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);

        var cut = Loaded(Render());

        IElement Step(string id) => cut.Find($".stepper [data-step='{id}']");
        Assert.Equal("Pipeline", cut.Find("ol.stepper").GetAttribute("aria-label"));
        Assert.True(Step("triage").ClassList.Contains("step-done"));
        Assert.True(Step("plan").ClassList.Contains("step-done"));
        var current = Step("implement");
        Assert.True(current.ClassList.Contains("step-current"));
        Assert.True(current.ClassList.Contains("step-waiting"));
        Assert.Equal("step", current.GetAttribute("aria-current"));
        Assert.Contains("gate · your approval", current.TextContent);
        Assert.True(Step("done").ClassList.Contains("step-upcoming"));
        Assert.DoesNotContain("gate", Step("done").TextContent);
        Assert.Single(cut.FindAll("[aria-current=step]"));
    }

    [Fact]
    public async Task Unknown_project_has_no_stepper_and_falls_back_to_the_id()
    {
        await Seed("GONE-1", "implement", StageStatus.Pending, t => t.ProjectId = "gone");

        var cut = Loaded(Render("GONE-1", "gone"));

        Assert.Empty(cut.FindAll(".stepper"));
        Assert.Equal("gone", cut.Find(".crumbs a").TextContent.Trim());
    }

    [Fact]
    public async Task Actions_and_links_sit_in_the_rail()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);

        var cut = Loaded(Render());

        Assert.NotNull(Button(cut, "Approve").Closest("aside.detail-rail"));
    }

    [Fact]
    public async Task Long_title_and_key_wrap()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending, t => t.Title = new string('x', 200));

        var cut = Loaded(Render());

        Assert.Equal(new string('x', 200), Assert.Single(cut.FindAll("h1")).TextContent.Trim());
        var css = PageCss();
        Assert.Contains(RulesFor(css, "h1"), r => r.Contains("overflow-wrap: anywhere"));
        Assert.Contains(RulesFor(css, ".crumbs"), r => r.Contains("overflow-wrap: anywhere"));
    }

    [Fact]
    public async Task Ticket_text_is_not_rendered_as_markup()
    {
        await Seed("ORD-1", "implement", StageStatus.Blocked, t =>
        {
            t.Title = "<b>x</b>";
            t.BlockedReason = "<i>y</i>";
            t.Feedback = "<u>z</u>";
        });

        var cut = Loaded(Render());

        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", cut.Markup);
        Assert.Empty(cut.FindAll("b, i, u"));
    }

    [Fact]
    public async Task Detail_refreshes_when_notified()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.Pending);
        var cut = Loaded(Render());

        await store.AddEventAsync(t.Id, "implement", "brand new event", Now, default);
        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        cut.WaitForAssertion(() => Assert.Contains("brand new event", cut.Markup));
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_ticket_and_shows_banner()
    {
        await Seed("ORD-1", "implement", StageStatus.Pending);
        var cut = Loaded(Render());
        store.FailNextFind = true;

        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the ticket: database is locked", StatusLine(cut)));
        Assert.Contains("Title ORD-1", cut.Markup);
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
    public async Task Command_finishing_after_the_page_is_gone_leaves_the_frame_alone()
    {
        await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        var cut = Loaded(Render());
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.HoldSaves = hold;
        Button(cut, "Approve").Click(); // in flight: the approval's save waits

        await ctx.DisposeComponentsAsync(); // navigated away: the next page has its own message, errors and label
        Chrome.ClearForNavigation();
        Chrome.SetPage(null, null);
        store.HoldSaves = null;
        hold.SetResult();

        await SettleAfter(() => store.Single("ORD-1").Status == StageStatus.Approved);
        Assert.Null(Chrome.Message);
        Assert.Null(Chrome.PageLabel);
    }

    [Fact]
    public void Not_found_page_uses_the_frame()
    {
        Chrome.SetPage("ORD-1", "orders");
        Chrome.ShowMessage("ORD-1: approved");

        var cut = ctx.Render<NotFound>();

        Assert.Equal("Not found", Chrome.PageLabel);
        Assert.Null(Chrome.CurrentProject);
        Assert.Null(Chrome.Message);
        Assert.Equal("Not found", Assert.Single(cut.FindAll("h1")).TextContent.Trim());
        Assert.Equal("/", cut.Find("a").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".back"));
    }

    [Fact]
    public async Task Spec_and_plan_paths_link_to_the_document_pages()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval, t =>
        {
            t.SpecPath = "docs/specs/2026-10-24-ORD-1-spec.md";
            t.PlanPath = "docs/plans/2026-10-24-ORD-1-plan.md";
        });
        var cut = Loaded(Render());

        var spec = cut.Find("a[data-spec-link]");
        Assert.Equal("/tickets/orders/ORD-1/docs/spec", spec.GetAttribute("href"));
        Assert.Equal("docs/specs/2026-10-24-ORD-1-spec.md", spec.TextContent.Trim());
        Assert.Null(spec.GetAttribute("target"));
        var plan = cut.Find("a[data-plan-link]");
        Assert.Equal("/tickets/orders/ORD-1/docs/plan", plan.GetAttribute("href"));
        Assert.Equal("docs/plans/2026-10-24-ORD-1-plan.md", plan.TextContent.Trim());
        Assert.Null(plan.GetAttribute("target"));
    }
}
