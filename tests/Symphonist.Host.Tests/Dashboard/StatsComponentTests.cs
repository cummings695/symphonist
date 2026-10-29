using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Config;
using Symphonist.Core.Agents;
using Symphonist.Core.Integrations;
using Symphonist.Core.Storage;
using Symphonist.Host.Components.Pages;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class StatsComponentTests : IDisposable
{
    // Wednesday; this week began Monday 2026-10-19.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTimeProvider time = new(Now);

    public StatsComponentTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing"));
        var registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        var limits = new ProviderLimits(store, time);
        var integrations = new FakeIntegrationFactory(
            new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
        var orchestrator = new Orchestrator(registry, store, integrations,
            new StageExecutor(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner> { [AgentTypes.ClaudeCode] = new FakeAgentRunner() }), store, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), store, time), limits, time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);

        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(new BoardNotifier(time));
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton(orchestrator);
        ctx.Services.AddSingleton(limits);
        ctx.Services.AddSingleton<BoardQuery>();
        ctx.Services.AddScoped<DashboardChrome>();
    }

    public void Dispose()
    {
        ctx.Dispose();
        home.Dispose();
    }

    private IRenderedComponent<Stats> RenderStats(string uri = "/stats")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var cut = ctx.Render<Stats>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-stats-loaded]")));
        return cut;
    }

    private Task Touch(string project, string stage, TouchKind kind, DateTimeOffset at) =>
        store.AddTouchAsync(new TouchRecord(Guid.NewGuid(), project, stage, kind, at), default);

    [Fact]
    public async Task Stats_page_lists_stages_in_pipeline_order_with_trend()
    {
        await Touch("orders", "implement", TouchKind.SendBack, Now);                  // this week
        await Touch("orders", "implement", TouchKind.SendBack, Now.AddHours(-2));     // this week
        await Touch("orders", "implement", TouchKind.Unblock, Now.AddDays(-14));      // two weeks ago
        await Touch("orders", "implement", TouchKind.SendBack, Now.AddDays(-14));     // two weeks ago, too
        await Touch("orders", "implement", TouchKind.SendBack, Now.AddDays(-14));
        await Touch("orders", "implement", TouchKind.SendBack, Now.AddDays(-14));

        var cut = RenderStats();

        var orders = cut.Find("[data-stats-project='orders']");
        Assert.Equal(["triage", "plan", "implement"], orders.QuerySelectorAll("tbody tr").Select(r => r.GetAttribute("data-stage")));
        var implement = orders.QuerySelector("[data-stage='implement']")!;
        Assert.Equal(["implement", "5", "1", "0", "0 / 0", "▁▁▁▁▁█▁▅"],
            implement.QuerySelectorAll("td").Select(c => c.TextContent.Trim()));
        // Scaled to the busiest week: two weeks ago holds 4 touches (the full block), this week 2.
        Assert.Equal("▁▁▁▁▁▁▁▁", orders.QuerySelector("[data-stage='plan'] [data-trend]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Stats_page_filters_by_project()
    {
        await Touch("billing", "plan", TouchKind.SendBack, Now);

        Assert.Equal(["billing", "orders"], RenderStats().FindAll("[data-stats-project]").Select(e => e.GetAttribute("data-stats-project")));
        var filtered = RenderStats("/stats?project=billing");

        Assert.Equal(["billing"], filtered.FindAll("[data-stats-project]").Select(e => e.GetAttribute("data-stats-project")));
        Assert.Equal("1", filtered.Find("[data-stage='plan'] td:nth-child(2)").TextContent.Trim());
    }
}
