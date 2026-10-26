using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Components.Layout;
using Symphonist.Host.Components.Shared;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class MainLayoutTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly BoardNotifier notifier;
    private readonly ProjectRegistry registry;
    private readonly Orchestrator orchestrator;
    private readonly DashboardChrome chrome = new();
    private readonly BunitJSModuleInterop theme;
    private readonly BunitJSModuleInterop frame;
    private readonly FakeIntegrationFactory integrations = new(
        new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));

    public MainLayoutTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", TestHome.HostAgent));
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        notifier = new BoardNotifier(time);
        orchestrator = new Orchestrator(registry, store, integrations,
            new StageExecutor(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner> { [AgentTypes.ClaudeCode] = new FakeAgentRunner() }), store, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);

        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        theme = ctx.JSInterop.SetupModule("./js/theme.js");
        frame = ctx.JSInterop.SetupModule("./js/frame.js");
        frame.Setup<bool>("focus", _ => true).SetResult(true);
        frame.SetupVoid("scrollToTop", _ => true).SetVoidResult();
        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton(orchestrator);
        ctx.Services.AddSingleton<BoardQuery>();
        ctx.Services.AddScoped(_ => chrome);
    }

    public void Dispose()
    {
        ctx.Dispose();
        home.Dispose();
    }

    private async Task Seed(string key, string stage, StageStatus status, string project = "orders")
    {
        await store.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = project, Key = key, Title = $"Title {key}", StageId = stage,
            Status = status, Attempts = 1, CreatedAt = Now, UpdatedAt = Now, StageStartedAt = Now, WaitingSince = Now,
        }, default);
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        if (!themeSetUp)
        {
            theme.Setup<string>("current").SetResult("light");
            theme.Setup<bool>("set", _ => true).SetResult(true);
        }
        var cut = ctx.Render<MainLayout>(p => p.Add(l => l.Body, b => b.AddContent(0, "page body")));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-sidebar-project]")));
        return cut;
    }

    private bool themeSetUp;

    private static IElement Row(IRenderedComponent<MainLayout> cut, string id) => cut.Find($"[data-sidebar-project='{id}']");

    private static string StatusLine(IRenderedComponent<MainLayout> cut) => cut.Find("[role=status]").TextContent;

    [Fact]
    public async Task Title_bar_counts_cover_every_project()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("BIL-1", "implement", StageStatus.Running, "billing");
        await Seed("ORD-2", "implement", StageStatus.Blocked);
        chrome.SetPage(null, "orders");

        var cut = RenderLayout();

        Assert.Contains("1 waiting on you", cut.Find("[data-count=waiting]").TextContent);
        Assert.Contains("1 running", cut.Find("[data-count=running]").TextContent);
        Assert.Contains("1 blocked", cut.Find("[data-count=blocked]").TextContent);
    }

    [Fact]
    public void Sidebar_lists_projects_and_marks_the_current_one()
    {
        chrome.SetPage("ORD-1", "orders");

        var cut = RenderLayout();

        Assert.Equal(["", "billing", "orders"], cut.FindAll("[data-sidebar-project]").Select(e => e.GetAttribute("data-sidebar-project")));
        Assert.Equal("page", Row(cut, "orders").GetAttribute("aria-current"));
        Assert.Equal("/?project=orders", Row(cut, "orders").GetAttribute("href"));
        Assert.Null(Row(cut, "").GetAttribute("aria-current"));
        Assert.Equal("/", Row(cut, "").GetAttribute("href"));
    }

    [Fact]
    public async Task Sidebar_shows_waiting_else_active_counts()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("BIL-1", "implement", StageStatus.Running, "billing");
        await Seed("BIL-2", "implement", StageStatus.Running, "billing");

        var cut = RenderLayout();

        var orders = Row(cut, "orders").QuerySelector(".project-count")!.TextContent;
        Assert.Contains("1", orders);
        Assert.Contains("waiting on you", orders);
        var billing = Row(cut, "billing").QuerySelector(".project-count")!.TextContent;
        Assert.Contains("2", billing);
        Assert.Contains("active", billing);
        Assert.Contains("waiting on you", Row(cut, "").QuerySelector(".project-count")!.TextContent);
    }

    [Fact]
    public async Task Paused_and_problem_projects_are_marked()
    {
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set; run: symphonist secret set jira-acme");
        await orchestrator.TickAsync(CancellationToken.None);
        registry.SetPaused("orders", true);

        var cut = RenderLayout();

        Assert.Contains("Paused", Row(cut, "orders").TextContent);
        Assert.DoesNotContain("Paused", Row(cut, "billing").TextContent);
        var billing = Row(cut, "billing");
        Assert.Contains("has a problem", billing.TextContent);
        Assert.Contains("secret", billing.QuerySelector(".project-problem")!.GetAttribute("title"));
    }

    [Fact]
    public void No_projects_shows_an_empty_sidebar_and_zero_counts()
    {
        foreach (var f in Directory.GetFiles(home.Projects)) File.Delete(f);
        registry.Reload();

        var cut = RenderLayout();

        Assert.Contains("No projects configured", cut.Markup);
        Assert.Contains("0 waiting on you", cut.Find("[data-count=waiting]").TextContent);
    }

    [Fact]
    public void Poll_now_shows_its_message()
    {
        var cut = RenderLayout();

        Assert.Contains("status-line-empty", cut.Find("[role=status]").ClassList);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Poll now").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Poll requested", StatusLine(cut));
            Assert.DoesNotContain("status-line-empty", cut.Find("[role=status]").ClassList);
        });
    }

    [Fact]
    public void Shell_refresh_failure_is_shown_and_clears()
    {
        var cut = RenderLayout();
        store.FailNextList = true;

        notifier.Notify();
        time.Advance(BoardNotifier.Window);

        cut.WaitForAssertion(() => Assert.Contains("Could not refresh the projects: database is locked", StatusLine(cut)));
        Assert.NotEmpty(cut.FindAll("[data-sidebar-project='orders']"));
        Assert.Contains("page body", cut.Markup);

        notifier.Notify();
        time.Advance(BoardNotifier.Window);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Could not refresh", StatusLine(cut)));
    }

    [Fact]
    public void Long_project_name_stays_on_one_line()
    {
        var name = new string('x', 60);
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders").Replace("name: Project orders", $"name: {name}"));
        registry.Reload();

        var cut = RenderLayout();

        var element = Row(cut, "orders").QuerySelector(".project-name")!;
        Assert.Equal(name, element.TextContent.Trim());
    }

    [Fact]
    public void Phone_menu_opens_and_closes()
    {
        var cut = RenderLayout();
        var menu = cut.Find("button[aria-controls=sidebar]");
        Assert.Equal("false", menu.GetAttribute("aria-expanded"));
        Assert.Equal("Projects", menu.GetAttribute("aria-label") ?? menu.TextContent.Trim());

        menu.Click();

        Assert.Equal("true", cut.Find("button[aria-controls=sidebar]").GetAttribute("aria-expanded"));
        Assert.Contains("sidebar-open", cut.Find(".app").ClassList);

        cut.Find("button[aria-label='Close projects']").Click();

        Assert.Equal("false", cut.Find("button[aria-controls=sidebar]").GetAttribute("aria-expanded"));
        Assert.DoesNotContain("sidebar-open", cut.Find(".app").ClassList);
    }

    [Fact]
    public void Escape_closes_the_phone_menu()
    {
        var cut = RenderLayout();
        cut.Find("button[aria-controls=sidebar]").Click();

        cut.Find("#sidebar").KeyDown(key: "Escape");

        Assert.DoesNotContain("sidebar-open", cut.Find(".app").ClassList);
    }

    /// <summary>The selector lists the layout asked the frame module to focus, in call order.</summary>
    private string[][] FocusRequests() =>
        frame.Invocations["focus"].Select(i => ((IEnumerable<string>)i.Arguments[0]!).ToArray()).ToArray();

    [Fact]
    public void Opening_the_phone_menu_moves_focus_to_the_current_project()
    {
        chrome.SetPage(null, "orders");
        var cut = RenderLayout();

        cut.Find("button[aria-controls=sidebar]").Click();

        cut.WaitForAssertion(() => Assert.Equal("#sidebar [data-sidebar-project][aria-current=page]", Assert.Single(FocusRequests())[0]));
    }

    [Fact]
    public void Escape_closes_the_phone_menu_right_after_opening_and_returns_focus_to_the_button()
    {
        var cut = RenderLayout();
        var menu = cut.Find("button[aria-controls=sidebar]");
        menu.Click();

        cut.Find("button[aria-controls=sidebar]").KeyDown(key: "Escape"); // focus is still on the button

        Assert.DoesNotContain("sidebar-open", cut.Find(".app").ClassList);
        ctx.JSInterop.VerifyFocusAsyncInvoke();
    }

    [Fact]
    public void Keys_do_not_reach_the_server_while_the_phone_menu_is_closed()
    {
        var cut = RenderLayout();

        Assert.Throws<MissingEventHandlerException>(() => cut.Find("button[aria-controls=sidebar]").KeyDown(key: "Escape"));
        Assert.Throws<MissingEventHandlerException>(() => cut.Find("#sidebar").KeyDown(key: "a"));
    }

    [Fact]
    public void Picking_a_project_in_the_phone_menu_closes_it_and_focuses_the_page_heading()
    {
        var cut = RenderLayout();
        cut.Find("button[aria-controls=sidebar]").Click();

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/?project=orders"); // query only: no FocusOnNavigate

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("sidebar-open", cut.Find(".app").ClassList);
            Assert.Equal(["h1"], FocusRequests().Last());
        });
    }

    [Fact]
    public void Navigating_scrolls_the_page_back_to_the_top()
    {
        var cut = RenderLayout();
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/?project=orders");
        cut.WaitForAssertion(() => Assert.Single(frame.Invocations["scrollToTop"]));
        navigation.NavigateTo("/?project=billing");
        cut.WaitForAssertion(() => Assert.Equal(2, frame.Invocations["scrollToTop"].Count));

        Assert.Empty(FocusRequests()); // the menu was closed: focus is left to the page
    }

    [Fact]
    public void A_chosen_theme_also_sets_the_native_colour_scheme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Symphonist.slnx"))) dir = dir.Parent!;
        var css = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(dir.FullName, "src", "Symphonist.Host", "wwwroot", "app.css")), @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var theme in new[] { "light", "dark" })
        {
            var rules = System.Text.RegularExpressions.Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}")
                .Where(m => m.Groups[1].Value.Split(',').Any(s => s.Trim() == $":root[data-theme=\"{theme}\"]"))
                .Select(m => m.Groups[2].Value);
            Assert.Contains(rules, r => System.Text.RegularExpressions.Regex.IsMatch(r, $@"color-scheme:\s*{theme}\s*;"));
        }
    }

    [Fact]
    public void Tab_title_formats()
    {
        Assert.Equal("Symphonist", MainLayout.TabTitle(0, null));
        Assert.Equal("(2) Symphonist", MainLayout.TabTitle(2, null));
        Assert.Equal("ORD-1 · Symphonist", MainLayout.TabTitle(0, "ORD-1"));
        Assert.Equal("(2) ORD-1 · Symphonist", MainLayout.TabTitle(2, "ORD-1"));
    }

    [Fact]
    public void Project_colour_is_stable_and_from_the_palette()
    {
        Assert.Equal(ProjectSidebar.ColourOf("orders"), ProjectSidebar.ColourOf("orders"));
        Assert.StartsWith("var(--project-", ProjectSidebar.ColourOf("billing"));
    }

    [Fact]
    public void Theme_toggle_reads_and_sets_the_theme()
    {
        var cut = RenderLayout();

        cut.WaitForAssertion(() => Assert.Equal("Switch to dark mode", cut.Find("[data-theme-toggle]").GetAttribute("aria-label")));

        cut.Find("[data-theme-toggle]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Switch to light mode", cut.Find("[data-theme-toggle]").GetAttribute("aria-label")));
        var call = Assert.Single(theme.Invocations["set"]);
        Assert.Equal("dark", Assert.Single(call.Arguments));
    }

    [Fact]
    public void Theme_toggle_survives_a_failing_browser()
    {
        themeSetUp = true;
        theme.Setup<string>("current").SetException(new JSException("storage blocked"));
        theme.Setup<bool>("set", _ => true).SetException(new JSException("storage blocked"));

        var cut = RenderLayout();

        Assert.Equal("Switch light or dark mode", cut.Find("[data-theme-toggle]").GetAttribute("aria-label"));
        cut.Find("[data-theme-toggle]").Click();
        Assert.Contains("page body", cut.Markup);
        Assert.Equal("Switch light or dark mode", cut.Find("[data-theme-toggle]").GetAttribute("aria-label"));
    }
}
