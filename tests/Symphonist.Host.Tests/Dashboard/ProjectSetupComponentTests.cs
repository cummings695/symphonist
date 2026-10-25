using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Evidence;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Components.Pages;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Host.Tests.Onboarding;
using Symphonist.Integrations;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class ProjectSetupComponentTests : IDisposable
{
    private static readonly ProviderRegistry Providers = BuiltInProviders.Registry;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z");

    private readonly TestHome home = new();
    private readonly BunitContext ctx = new();
    private readonly FakeTicketStore tickets = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeProjectDiscovery discovery = new();
    private readonly FakeSecretStore secrets = new();
    private readonly FakeGitWorkspace git = new() { OriginUrl = "git@example.org:acme/orders.git" };
    private readonly FakeAgentRunner agent = new();
    private readonly FakeHostLifetime lifetime = new();
    private readonly ProjectRegistry registry;
    private readonly Orchestrator orchestrator;
    private readonly OnboardingService service;

    public ProjectSetupComponentTests()
    {
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        var factory = new FakeIntegrationFactory(new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
        orchestrator = new Orchestrator(registry, tickets, factory,
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), tickets, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), tickets, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), tickets, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);
        var agents = new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = agent });
        var docker = new DockerLauncher(new FakeProcessRunner(), Path.Combine(home.Path, "agent-context"), _ => null);
        service = new OnboardingService(registry, Providers, new SetupChecks(discovery, Providers, secrets, git),
            new OnboardingProposer(git, agents, time),
            new ContainerCheck(docker, new IsolationLauncher(new HostLauncher(), docker), git, agents, time),
            new ProjectFileStore(registry, tickets, orchestrator, Providers), secrets, lifetime, time);
        discovery.Repository = new RepositoryDiscovery("acme/orders", "main", true);

        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton(Providers);
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton<ITicketStore>(tickets);
        ctx.Services.AddScoped<DashboardChrome>();
    }

    public void Dispose()
    {
        ctx.Dispose();
        service.Dispose();
        orchestrator.WhenIdleAsync().GetAwaiter().GetResult(); // the tick a save requests
        lifetime.Dispose();
        home.Dispose();
    }

    private string PathOf(string fileName) => Path.Combine(home.Projects, fileName);

    /// <summary>Writes project <paramref name="id"/> (repository acme/{id}) and reloads; returns the text.</summary>
    private string WriteProject(string id, string tracker = "jira", string repo = "bitbucket")
    {
        var form = ProjectFileStoreTests.Form(id, tracker, repo);
        var keys = Providers.Repo(repo).FullNameKeys;
        form.Repo.Values[keys[0]] = "acme";
        form.Repo.Values[keys[1]] = id;
        var text = "# hand-written header\n" + ProjectYamlWriter.NewFile(form, Providers);
        home.WriteProject($"{id}.yaml", text);
        registry.Reload();
        return text;
    }

    private IRenderedComponent<ProjectSetup> Render(string? id = null) =>
        ctx.Render<ProjectSetup>(p => p.Add(c => c.Id, id));

    /// <summary>Opens an existing project's setup with every secret it names set.</summary>
    private IRenderedComponent<ProjectSetup> RenderWithSecrets(string id)
    {
        var cut = Render(id);
        foreach (var name in service.SecretNamesInUse(service.Current!)) secrets.Set(name, "stored-token");
        return cut;
    }

    private static AgentRunResult Proposal(object structured) =>
        new(true, null, 0.4m, TimeSpan.FromSeconds(5), null, """{"raw":"onboarding"}""",
            JsonSerializer.SerializeToElement(structured));

    private static IElement Button(IRenderedComponent<ProjectSetup> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    /// <summary>The input whose label reads <paramref name="label"/>, with or without " (required)", inside <paramref name="scope"/>.</summary>
    private static IElement Labelled(IRenderedComponent<ProjectSetup> cut, string label, string scope = "")
    {
        var element = cut.FindAll($"{scope} label".Trim())
            .First(l => l.TextContent.Trim() is var t && (t == label || t == $"{label} (required)"));
        return cut.Find($"#{element.GetAttribute("for")}");
    }

    private static IReadOnlyList<string> Labels(IRenderedComponent<ProjectSetup> cut, string scope) =>
        cut.FindAll($"{scope} label").Select(l => l.TextContent.Trim().Replace(" (required)", "")).ToList();

    private static async Task CheckConnections(IRenderedComponent<ProjectSetup> cut)
    {
        await Button(cut, "Check connections").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-check-list='connections'] li")));
    }

    private async Task Propose(IRenderedComponent<ProjectSetup> cut, object structured)
    {
        await CheckConnections(cut);
        agent.Results.Enqueue(Proposal(structured));
        await Button(cut, "Propose mapping and risk areas").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-proposal]")));
    }

    /// <summary>Fills a new project "orders" (Jira + Bitbucket) with every required value.</summary>
    private static void FillNewProject(IRenderedComponent<ProjectSetup> cut)
    {
        cut.Find("#project-id").Change("orders");
        cut.Find("#tracker-site").Change("https://acme.atlassian.net");
        cut.Find("#tracker-email").Change("dev@example.com");
        cut.Find("#tracker-jql").Change("assignee = currentUser()");
        cut.Find("#repo-workspace").Change("acme");
        cut.Find("#repo-slug").Change("orders");
        cut.Find("#repo-localPath").Change("../acme-orders");
        cut.Find("#repo-email").Change("dev@example.com");
    }

    [Fact]
    public void New_project_page_shows_default_pipeline_and_sections()
    {
        var cut = Render();

        Assert.Equal("Add project", cut.Find("h1").TextContent.Trim());
        Assert.Equal(7, cut.FindAll("[data-stage-row]").Count);
        var headings = cut.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        foreach (var heading in new[] { "Connections", "Pipeline and state mapping", "Risk areas", "Review and guardrails", "Agent and container", "Templates" })
            Assert.Contains(heading, headings);
        Assert.Equal(["triage", "plan", "implement", "build", "release", "deploy", "done"],
            cut.FindAll("[data-stage-row] input[id$='-id']").Select(i => i.GetAttribute("value")));
    }

    [Fact]
    public void Provider_fields_render_from_field_lists()
    {
        var cut = Render();

        var jira = Labels(cut, "[data-section='tracker']");
        foreach (var label in new[] { "Site URL", "Account email", "Project key", "JQL", "Token secret name" }) Assert.Contains(label, jira);
        Assert.Contains("(required)", cut.Find("[data-section='tracker'] label[for='tracker-site']").TextContent);
        Assert.DoesNotContain("(required)", cut.Find("[data-section='tracker'] label[for='tracker-projectKey']").TextContent);

        cut.Find("#tracker-type").Change("github-projects");

        var github = Labels(cut, "[data-section='tracker']");
        foreach (var label in new[] { "Owner", "Project number", "Label", "Status field", "Statuses" }) Assert.Contains(label, github);
        Assert.DoesNotContain("Site URL", github);
        Assert.Equal("numeric", cut.Find("#tracker-project").GetAttribute("inputmode"));
        Assert.Equal("Status", cut.Find("#tracker-statusField").GetAttribute("placeholder"));
        Assert.Equal("textarea", cut.Find("#tracker-statuses").TagName.ToLowerInvariant());
        Assert.Equal("github-projects", service.Current!.Form.Tracker.Type);
    }

    [Fact]
    public void Build_choices_follow_repo_type()
    {
        var cut = Render();
        Assert.Equal(["bitbucket-pipelines"], cut.FindAll("#build-type option").Select(o => o.GetAttribute("value")));

        cut.Find("#repo-type").Change("github");

        Assert.Equal(["github-actions"], cut.FindAll("#build-type option").Select(o => o.GetAttribute("value")));
        Assert.Equal("github-actions", service.Current!.Form.Build.Type);
        Assert.Contains("GitHub Actions", cut.Find("#build-type").TextContent);
    }

    [Theory]
    [InlineData("jira", "bitbucket", "repo-slug")]
    [InlineData("github-projects", "github", "repo-repo")]
    public void Existing_project_setup_loads_values_and_id_is_read_only(string tracker, string repo, string nameInput)
    {
        WriteProject("orders", tracker, repo);

        var cut = Render("orders");

        var id = cut.Find("#project-id");
        Assert.Equal("orders", id.GetAttribute("value"));
        Assert.True(id.HasAttribute("readonly"));
        Assert.Equal("orders", cut.Find($"#{nameInput}").GetAttribute("value"));
        Assert.EndsWith("acme-orders", cut.Find("#repo-localPath").GetAttribute("value")); // outside the projects folder: absolute
        Assert.Contains(cut.FindAll("#tracker-type option"), o => o.GetAttribute("value") == tracker && o.HasAttribute("selected"));
        Assert.Equal(7, cut.FindAll("[data-stage-row]").Count);
    }

    [Fact]
    public void Unknown_project_shows_message()
    {
        var cut = Render("nope");

        Assert.Contains("No project 'nope'", cut.Markup);
        Assert.Empty(cut.FindAll("[data-stage-row]"));
    }

    [Fact]
    public async Task Check_steps_show_status_words()
    {
        WriteProject("orders");
        var cut = RenderWithSecrets("orders");
        discovery.RepositoryError = new HttpRequestException("404 (Not Found)");

        await CheckConnections(cut);

        var steps = cut.FindAll("[data-check-list='connections'] li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Contains("Tracker sign-in: Passed — Jira: signed in as Dev", steps);
        Assert.Contains(steps, s => s.StartsWith("Repository: Failed — ", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.StartsWith("Production branch: Skipped — ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proposal_text_renders_as_text()
    {
        WriteProject("orders");
        var cut = RenderWithSecrets("orders");

        await Propose(cut, new
        {
            stages = new[] { new { id = "plan", approved_when_status = "In Progress", reason = "<img src=x onerror=alert(1)>" } },
            risk_areas = new[] { new { name = "pricing", description = "<b>prices</b>", paths = new[] { "src/Pricing/**" }, reason = "<script>x()</script>" } },
            notes = new[] { "<img src=y>" },
        });

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", cut.Markup);
        Assert.Contains("&lt;script&gt;", cut.Markup);
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("b"));
    }

    [Fact]
    public async Task Apply_proposal_fills_mapping_and_marks_changed()
    {
        WriteProject("orders");
        var cut = RenderWithSecrets("orders");
        await Propose(cut, new
        {
            stages = new[] { new { id = "plan", approved_when_status = "In Progress", reason = "next column" } },
            added_stages = new[] { new { id = "qa", kind = "uat", after = "implement", reason = "a QA column" } },
            risk_areas = new[] { new { name = "pricing", description = "Prices", paths = new[] { "src/Pricing/**" }, reason = "money" } },
        });
        var add = cut.Find("[data-proposal] input[type=checkbox]");
        Assert.False(add.HasAttribute("checked"));
        Assert.Contains("Proposed", cut.Find("[data-proposal]").TextContent);

        await Button(cut, "Apply proposal").ClickAsync(new());

        var plan = cut.FindAll("[data-stage-row]")[1];
        Assert.Equal("In Progress", cut.Find("#stage-1-approved").GetAttribute("value"));
        Assert.Contains("(changed)", plan.TextContent);
        Assert.DoesNotContain("(changed)", cut.FindAll("[data-stage-row]")[0].TextContent);
        Assert.DoesNotContain(cut.FindAll("[data-stage-row] input[id$='-id']"), i => i.GetAttribute("value") == "qa");
        Assert.Equal("pricing", Assert.Single(service.Current!.Form.RiskAreas).Name);
        Assert.Empty(cut.FindAll("[data-proposal]"));
    }

    [Fact]
    public async Task Secret_value_is_stored_and_never_rendered_or_written()
    {
        var cut = Render();
        FillNewProject(cut);
        var field = cut.Find("[data-secret='jira-orders']");
        Assert.Contains("Not set", field.TextContent);

        field.QuerySelector("input[type=password]")!.Change("s3cret");
        await cut.Find("[data-secret='jira-orders'] button").ClickAsync(new());

        Assert.Equal("s3cret", secrets.Values["jira-orders"]);
        Assert.DoesNotContain("s3cret", cut.Markup);
        Assert.Contains("Set", cut.Find("[data-secret='jira-orders']").TextContent);
        Assert.DoesNotContain("Not set", cut.Find("[data-secret='jira-orders']").TextContent);
        Assert.True(string.IsNullOrEmpty(cut.Find("[data-secret='jira-orders'] input[type=password]").GetAttribute("value")));

        await Button(cut, "Save project").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Markup));

        var yaml = File.ReadAllText(PathOf("orders.yaml"));
        Assert.DoesNotContain("s3cret", yaml);
        Assert.Contains("tokenSecret: jira-orders", yaml);
        Assert.Contains("tokenSecret: bitbucket-orders", yaml);
        Assert.DoesNotContain("s3cret", cut.Markup);
    }

    [Fact]
    public async Task Save_writes_project_and_links_to_board()
    {
        var cut = Render();
        FillNewProject(cut);

        await Button(cut, "Save project").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains($"Saved {PathOf("orders.yaml")}", cut.Find("[data-saved]").TextContent));
        Assert.Contains(registry.Projects, p => p.Id == "orders");
        Assert.Contains(cut.FindAll("[data-saved] a"), a => a.GetAttribute("href") == "/?project=orders");
    }

    [Fact]
    public async Task Save_errors_are_listed()
    {
        var text = WriteProject("orders");
        await tickets.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "At plan", StageId = "plan",
            Status = StageStatus.AwaitingApproval, CreatedAt = Now, UpdatedAt = Now,
        }, default);
        var cut = Render("orders");

        await cut.Find("button[aria-label='Remove stage plan']").ClickAsync(new());
        Assert.Equal(6, cut.FindAll("[data-stage-row]").Count);
        await Button(cut, "Save project").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("stage 'plan' is removed but active tickets are at it: ORD-1",
            cut.Find("[data-save-errors]").TextContent));
        Assert.Equal(text, File.ReadAllText(PathOf("orders.yaml")));
        Assert.Empty(cut.FindAll("[data-saved]"));
    }

    [Fact]
    public async Task Status_not_in_tracker_list_is_flagged_in_words()
    {
        WriteProject("orders");
        var cut = RenderWithSecrets("orders");
        await CheckConnections(cut);

        cut.Find("#stage-0-enter").Change("Nope");
        cut.Find("#stage-1-enter").Change("planned"); // case differs only: a status of the project

        var row = cut.FindAll("[data-stage-row]")[0];
        Assert.Contains("not a status of Jira project ACME", row.TextContent);
        Assert.DoesNotContain("board query", row.TextContent);
        Assert.DoesNotContain("not a status", cut.FindAll("[data-stage-row]")[1].TextContent);
        Assert.Contains(cut.FindAll("datalist option"), o => o.GetAttribute("value") == "In Progress");
    }

    [Fact]
    public async Task Status_not_on_github_board_also_says_the_query_fails()
    {
        WriteProject("orders", "github-projects", "github");
        discovery.Tracker = new TrackerDiscovery("dev", "GitHub board acme #7 (Status)",
            [new("Todo", StatusCategory.Unknown, ""), new("Done", StatusCategory.Unknown, "")], 2, []);
        var cut = RenderWithSecrets("orders");
        await CheckConnections(cut);

        cut.Find("#stage-0-enter").Change("Nope");

        var text = cut.FindAll("[data-stage-row]")[0].TextContent;
        Assert.Contains("not a status of GitHub board acme #7 (Status)", text);
        Assert.Contains("the board query fails until it exists", text);
    }

    [Fact]
    public void Typing_the_id_names_the_default_secrets()
    {
        var cut = Render();

        cut.Find("#project-id").Change("Big_Orders");

        var form = service.Current!.Form;
        Assert.Equal("jira-big-orders", form.Tracker.Values["tokenSecret"]);
        Assert.Equal("bitbucket-big-orders", form.Repo.Values["tokenSecret"]);
        Assert.NotEmpty(cut.FindAll("[data-secret='jira-big-orders']"));

        form.Repo.Values["tokenSecret"] = "shared-token";
        cut.Find("#project-id").Change("orders");
        Assert.Equal("jira-orders", form.Tracker.Values["tokenSecret"]);
        Assert.Equal("shared-token", form.Repo.Values["tokenSecret"]); // chosen by hand: kept
    }

    [Fact]
    public void Jira_project_key_prefills_a_blank_jql()
    {
        var cut = Render();

        cut.Find("#tracker-projectKey").Change("ACME");

        Assert.Equal("project = ACME AND assignee = currentUser() AND labels = Claude AND statusCategory != Done",
            cut.Find("#tracker-jql").GetAttribute("value"));
        cut.Find("#tracker-jql").Change("labels = Mine");
        cut.Find("#tracker-projectKey").Change("ORD");
        Assert.Equal("labels = Mine", cut.Find("#tracker-jql").GetAttribute("value"));
    }

    [Fact]
    public async Task Prerendering_shows_loading_and_opens_no_session()
    {
        WriteProject("orders");
        var existing = service.Open(null);
        // Static rendering, as during prerender: OnAfterRender never runs.
        await using var renderer = new HtmlRenderer(ctx.Services, NullLoggerFactory.Instance);

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ProjectSetup>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = "orders" }))).ToHtmlString());

        Assert.Contains("Loading the setup", html);
        Assert.Same(existing, service.Current); // not replaced by a page the browser never connected to
        var cut = Render("orders");
        Assert.Equal("orders", service.Current!.ProjectId);
        Assert.Empty(cut.FindAll("[data-loading]"));
    }

    [Fact]
    public void Jql_and_risk_descriptions_are_multi_line_text_areas()
    {
        var cut = Render();

        var jql = cut.Find("#tracker-jql");
        Assert.Equal("TEXTAREA", jql.TagName);
        jql.Change("project = ACME\r\nAND labels = Claude");
        cut.FindAll("button").First(b => b.TextContent == "Add risk area").Click();
        var description = cut.Find("#risk-0-description");
        Assert.Equal("TEXTAREA", description.TagName);
        description.Change("Prices.\r\nAnd tax.");

        var form = service.Current!.Form;
        Assert.Equal("project = ACME\nAND labels = Claude", form.Tracker.Values["jql"]);
        Assert.Equal("Prices.\nAnd tax.", form.RiskAreas[0].Description);
    }

    [Fact]
    public async Task Ended_session_shows_its_message_with_a_reload_link()
    {
        WriteProject("orders");
        var cut = Render("orders");
        service.Open(null); // another tab

        await Button(cut, "Check connections").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("this setup was replaced by another one; reload the page",
            cut.Find("[role=alert]").TextContent));
        Assert.NotNull(cut.Find("[data-reload]"));
    }

    [Fact]
    public async Task Setup_edits_and_saves_evidence_and_pr_comments()
    {
        var text = WriteProject("orders");
        var cut = Render("orders");

        var headings = cut.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(headings.IndexOf("Review and guardrails") + 1, headings.IndexOf("Evidence"));
        Assert.Contains("PR comments", headings);
        Assert.Equal(["Off", "High risk", "Always"], cut.FindAll("#evidence-mode option").Select(o => o.TextContent.Trim()));
        Assert.Contains(cut.FindAll("#evidence-mode option"), o => o.TextContent.Trim() == "High risk" && o.HasAttribute("selected"));

        cut.Find("#evidence-mode").Change("Always");
        cut.Find("#evidence-setup").Change("npm ci\nnpm run seed");
        cut.Find("#evidence-start").Change("npm start");
        cut.Find("#evidence-url").Change("http://localhost:3000");
        cut.Find("#evidence-ready").Change("Listening on");
        cut.Find("#evidence-notes").Change("Sign in as demo/demo.");
        await Button(cut, "Add feature").ClickAsync(new());
        cut.Find("#evidence-feature-0-name").Change("Checkout");
        cut.Find("#evidence-feature-0-steps").Change("Add an item\r\nPay");
        cut.Find("#evidence-timeout").Change("45");
        cut.Find("#evidence-model").Change("opus");
        var others = cut.Find("#pr-comments-others");
        Assert.False(others.HasAttribute("checked"));
        Assert.Equal("Let anyone's @claude PR comments start a fix run", cut.Find("label[for='pr-comments-others']").TextContent.Trim());
        Assert.Contains("teammates' comments direct an agent that pushes under your account", cut.Find("[data-pr-comments]").TextContent);
        others.Change(true);

        Assert.Equal(text, File.ReadAllText(PathOf("orders.yaml"))); // nothing saved until Save
        await Button(cut, "Save project").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-saved]")));

        var saved = registry.Projects.Single(p => p.Id == "orders");
        Assert.Equal(EvidenceMode.Always, saved.Evidence.Mode);
        Assert.Equal(["npm ci", "npm run seed"], saved.Evidence.Run.Setup);
        Assert.Equal("npm start", saved.Evidence.Run.Start);
        Assert.Equal("http://localhost:3000", saved.Evidence.Run.Url);
        Assert.Equal("Listening on", saved.Evidence.Run.Ready);
        Assert.Equal("Sign in as demo/demo.", saved.Evidence.Run.Notes);
        var feature = Assert.Single(saved.Evidence.Features);
        Assert.Equal(("Checkout", "Add an item\nPay"), (feature.Name, feature.Steps));
        Assert.Equal(45, saved.Evidence.Agent.TimeoutMinutes);
        Assert.Equal("opus", saved.Evidence.Agent.Model);
        Assert.True(saved.PrComments.FromOthers);
    }

    [Fact]
    public async Task Feature_editor_removes_a_feature()
    {
        WriteProject("orders");
        var cut = Render("orders");
        await Button(cut, "Add feature").ClickAsync(new());
        cut.Find("#evidence-feature-0-name").Change("Checkout");

        await cut.Find("button[aria-label='Remove feature Checkout']").ClickAsync(new());

        Assert.Empty(service.Current!.Form.Evidence.Features);
        Assert.Empty(cut.FindAll("[data-feature-row]"));
    }

    [Fact]
    public async Task Setup_prefills_from_evidence_proposal_without_saving()
    {
        var text = WriteProject("orders");
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "Cart", StageId = "implement",
            Status = StageStatus.AwaitingApproval, CreatedAt = Now, UpdatedAt = Now,
        };
        await tickets.SaveAsync(ticket, default);
        EvidenceRecord Record(DateTimeOffset at, EvidenceRun? run, IReadOnlyList<EvidenceFeature> features) =>
            new(ticket.Id, "orders", "implement", at, at, "abc",
                new EvidenceReport(["ok"], [], [], null, new Walkthrough(true, null, EvidenceSource.Guessed, []), run, features).ToJson(),
                false, null, [], true);
        await tickets.AddEvidenceAsync(Record(Now.AddHours(-2),
            new EvidenceRun([], "old start", null, null, null), [new EvidenceFeature("Old", "old steps")]), default);
        await tickets.AddEvidenceAsync(Record(Now.AddHours(-1),
            new EvidenceRun(["npm ci"], "npm start", "http://localhost:3000", null, null), [new EvidenceFeature("Cart", "Open the cart")]), default);
        await tickets.AddEvidenceAsync(Record(Now, null, []), default); // the latest has no proposal

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/orders/setup?evidenceFrom=ORD-1");
        var cut = Render("orders");

        cut.WaitForAssertion(() => Assert.Equal("npm start", cut.Find("#evidence-start").GetAttribute("value")));
        Assert.Equal("http://localhost:3000", cut.Find("#evidence-url").GetAttribute("value"));
        Assert.Equal("Cart", cut.Find("#evidence-feature-0-name").GetAttribute("value"));
        Assert.Single(service.Current!.Form.Evidence.Features);
        Assert.Contains("ORD-1", cut.Find("[data-evidence-from]").TextContent);
        Assert.Equal(text, File.ReadAllText(PathOf("orders.yaml")));
    }

    [Fact]
    public async Task Onboarding_proposal_evidence_shows_as_proposed_and_applies()
    {
        WriteProject("orders");
        var cut = RenderWithSecrets("orders");
        await Propose(cut, new
        {
            stages = Array.Empty<object>(),
            risk_areas = Array.Empty<object>(),
            evidence = new
            {
                run = new { start = "npm start", url = "http://localhost:3000" },
                features = new[] { new { name = "Checkout", steps = "Pay for an item" } },
                reason = "package.json has a start script <b>x</b>",
            },
        });

        var proposed = cut.Find("[data-proposal-evidence]");
        Assert.Contains("Proposed", proposed.TextContent);
        Assert.Contains("package.json has a start script <b>x</b>", proposed.TextContent);
        Assert.Empty(cut.FindAll("b"));
        Assert.Null(service.Current!.Form.Evidence.Start);

        await Button(cut, "Apply proposal").ClickAsync(new());

        Assert.Equal("npm start", cut.Find("#evidence-start").GetAttribute("value"));
        Assert.Equal("Checkout", cut.Find("#evidence-feature-0-name").GetAttribute("value"));
    }
}
