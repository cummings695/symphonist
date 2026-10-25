using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Components.Pages;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class TicketDocumentComponentTests : IDisposable
{
    private const string Secret = "TOP-SECRET-CONTENT";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeIntegrationFactory integrations = new(
        new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
    private readonly string outside;

    public TicketDocumentComponentTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        outside = home.Path + "-outside";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), Secret);
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(home.Path, "secret.md"), Secret);
        File.WriteAllText(Path.Combine(home.Path, "work", "secret.md"), Secret);

        ctx.Services.AddSingleton(new ProjectRegistry(NullLogger<ProjectRegistry>.Instance));
        ctx.Services.AddSingleton<ITicketStore>(store);
        ctx.Services.AddSingleton<IIntegrationFactory>(integrations);
        ctx.Services.AddSingleton(new WorkspaceFiles(Path.Combine(home.Path, "work")));
        ctx.Services.AddScoped<DashboardChrome>();
    }

    private string Workspace => Path.Combine(home.Path, "work", "orders", "ORD-1");

    public void Dispose()
    {
        ctx.Dispose();
        foreach (var dir in new[] { home.Path, outside }.Where(Directory.Exists))
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                         .Where(e => e.LinkTarget is not null).ToList())
                entry.Delete();
        home.Dispose();
        if (Directory.Exists(outside)) Directory.Delete(outside, true);
    }

    private async Task Seed(string? spec = "docs/specs/2026-10-24-ORD-1-spec.md", string? plan = "docs/plans/2026-10-24-ORD-1-plan.md",
        string? branch = "symphonist/ORD-1")
    {
        await store.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "Title ORD-1", StageId = "plan",
            Status = StageStatus.AwaitingApproval, Attempts = 1, CreatedAt = Now, UpdatedAt = Now, WaitingSince = Now,
            SpecPath = spec, PlanPath = plan, Branch = branch,
        }, default);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IRenderedComponent<TicketDocument> Render(string kind, string key = "ORD-1", string project = "orders")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"/tickets/{project}/{key}/docs/{kind}");
        var cut = ctx.Render<TicketDocument>(p => p.Add(c => c.Project, project).Add(c => c.Key, key).Add(c => c.Kind, kind));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-doc-loaded]")));
        return cut;
    }

    private static string Problem(IRenderedComponent<TicketDocument> cut) => cut.Find("[data-doc-problem]").TextContent;

    private static void AssertNoSecret(IRenderedComponent<TicketDocument> cut)
    {
        Assert.Empty(cut.FindAll("[data-doc-body]"));
        Assert.DoesNotContain(Secret, cut.Markup);
    }

    [Fact]
    public async Task Renders_the_plan_from_the_workspace()
    {
        await Seed();
        Write("docs/plans/2026-10-24-ORD-1-plan.md", "# The plan\n\n- step one\n- step two\n\n<script>alert(1)</script>\n");

        var cut = Render("plan");

        Assert.Equal("Plan: 2026-10-24-ORD-1-plan.md", cut.Find("h1").TextContent);
        Assert.Equal("docs/plans/2026-10-24-ORD-1-plan.md", cut.Find("[data-doc-path]").TextContent);
        var body = cut.Find("[data-doc-body]");
        Assert.Equal("The plan", body.QuerySelector("h1")!.TextContent);
        Assert.Equal(["step one", "step two"], body.QuerySelectorAll("li").Select(l => l.TextContent));
        Assert.Empty(body.QuerySelectorAll("script"));
        Assert.Empty(cut.FindAll("[data-doc-problem]"));

        var bitbucket = cut.Find("[data-doc-source]");
        Assert.Equal("https://scm.example/src/symphonist/ORD-1/docs/plans/2026-10-24-ORD-1-plan.md", bitbucket.GetAttribute("href"));
        Assert.Equal("_blank", bitbucket.GetAttribute("target"));
        Assert.Contains("noopener", bitbucket.GetAttribute("rel"));

        Assert.Contains(cut.FindAll(".crumbs a"), a => a.GetAttribute("href") == "/tickets/orders/ORD-1");
    }

    [Fact]
    public async Task Renders_the_spec_from_its_own_path()
    {
        await Seed();
        Write("docs/specs/2026-10-24-ORD-1-spec.md", "## Spec heading\n");
        Write("docs/plans/2026-10-24-ORD-1-plan.md", "## Plan heading\n");

        var cut = Render("spec");

        Assert.Equal("Spec: 2026-10-24-ORD-1-spec.md", cut.Find("h1").TextContent);
        Assert.Equal("Spec heading", cut.Find("[data-doc-body] h2").TextContent);
    }

    [Theory]
    [InlineData("../../secret.md")]
    [InlineData("../../../secret.md")]
    [InlineData(@"..\..\..\secret.md")]
    public async Task Traversal_path_is_refused(string stored)
    {
        await Seed(plan: stored);
        var cut = Render("plan");
        Assert.Contains("not a safe path", Problem(cut));
        AssertNoSecret(cut);
    }

    [Fact]
    public async Task Rooted_path_is_refused()
    {
        await Seed(plan: Path.Combine(outside, "secret.md"));
        var cut = Render("plan");
        Assert.Contains("not a safe path", Problem(cut));
        AssertNoSecret(cut);
    }

    [SymlinkFact]
    public async Task Symlinked_file_is_refused()
    {
        await Seed(plan: "docs/plan.md");
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        File.CreateSymbolicLink(Path.Combine(Workspace, "docs", "plan.md"), Path.Combine(outside, "secret.md"));

        var cut = Render("plan");
        Assert.Contains("symbolic link", Problem(cut));
        AssertNoSecret(cut);
    }

    [Fact]
    public async Task Linked_directory_is_refused()
    {
        await Seed(plan: "docs/secret.md");
        WorkspaceLinks.LinkDirectory(Path.Combine(Workspace, "docs"), outside);

        var cut = Render("plan");
        Assert.Contains("symbolic link", Problem(cut));
        AssertNoSecret(cut);
    }

    [Fact]
    public async Task Oversized_file_is_refused()
    {
        await Seed(plan: "docs/plan.md");
        Write("docs/plan.md", "# Big\n" + new string('x', WorkspaceFiles.MaxBytes));

        var cut = Render("plan");
        Assert.Contains("too large to show", Problem(cut));
        Assert.Empty(cut.FindAll("[data-doc-body]"));
    }

    [Fact]
    public async Task Missing_file_says_so()
    {
        await Seed(plan: "docs/plan.md");
        var cut = Render("plan");
        Assert.Contains("not in the ticket's workspace", Problem(cut));
        Assert.Equal("docs/plan.md", cut.Find("[data-doc-path]").TextContent);
        Assert.NotNull(cut.Find("[data-doc-source]")); // still reachable on Bitbucket
    }

    [Fact]
    public async Task Missing_workspace_says_so()
    {
        await Seed(plan: "docs/plan.md");
        Directory.Delete(Workspace);
        var cut = Render("plan");
        Assert.Contains("workspace is not on this machine", Problem(cut));
    }

    [Fact]
    public async Task No_stored_path_says_so()
    {
        await Seed(plan: null);
        var cut = Render("plan");
        Assert.Contains("No plan", Problem(cut));
        Assert.Empty(cut.FindAll("[data-doc-source]"));
    }

    [Fact]
    public async Task Without_integrations_there_is_no_bitbucket_link()
    {
        await Seed(plan: "docs/plan.md");
        Write("docs/plan.md", "# Plan\n");
        integrations.Error = new InvalidOperationException("missing secret");

        var cut = Render("plan");
        Assert.Equal("Plan", cut.Find("[data-doc-body] h1").TextContent);
        Assert.Empty(cut.FindAll("[data-doc-source]"));
    }

    [Theory]
    [InlineData("readme")]
    [InlineData("Spec")]
    [InlineData("..")]
    public async Task Unknown_kind_is_not_found(string kind)
    {
        await Seed();
        var cut = Render(kind);
        Assert.Equal("Not found", cut.Find("h1").TextContent);
        Assert.Empty(cut.FindAll("[data-doc-body]"));
    }

    [Fact]
    public void Unknown_ticket_is_not_found()
    {
        var cut = Render("plan", key: "NOPE-1");
        Assert.Equal("Not found", cut.Find("h1").TextContent);
    }
}
