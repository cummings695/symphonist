using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Pipeline;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;

namespace Symphonist.Host.Tests.Onboarding;

[Collection(SymphonistHomeCollection.Name)]
public sealed class ProjectFileStoreTests : IDisposable
{
    private static readonly ProviderRegistry Providers = BuiltInProviders.Registry;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z");

    private readonly TestHome home = new();
    private readonly FakeTicketStore tickets = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly ProjectRegistry registry;
    private readonly Orchestrator orchestrator;
    private readonly ProjectFileStore files;

    public ProjectFileStoreTests()
    {
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        orchestrator = new Orchestrator(registry, tickets,
            new FakeIntegrationFactory(new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher())),
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), tickets, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), tickets, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);
        files = new ProjectFileStore(registry, tickets, orchestrator, Providers);
    }

    public void Dispose()
    {
        orchestrator.WhenIdleAsync().GetAwaiter().GetResult(); // the tick a save requests
        home.Dispose();
    }

    /// <summary>A new project's form with every required provider field filled.</summary>
    internal static ProjectForm Form(string id, string trackerType = "jira", string repoType = "bitbucket")
    {
        var form = ProjectForm.NewProject(id, Providers, trackerType, repoType);
        Fill(form.Tracker, Providers.Tracker(form.Tracker.Type).Fields);
        Fill(form.Repo, Providers.Repo(form.Repo.Type).Fields);
        return form;
    }

    private static void Fill(ProviderSectionForm section, IReadOnlyList<ProviderField> fields)
    {
        foreach (var field in fields.Where(f => f.Required && !section.Values.ContainsKey(f.Key)))
            section.Values[field.Key] = field.Kind switch
            {
                ProviderFieldKind.Url => "https://acme.atlassian.net",
                ProviderFieldKind.Number => "7",
                ProviderFieldKind.Path => "../acme-orders",
                _ => "acme",
            };
    }

    private string PathOf(string fileName) => Path.Combine(home.Projects, fileName);

    /// <summary>Writes <paramref name="text"/> as an existing project file, reloads, and returns its form as loaded.</summary>
    private ProjectForm Existing(string fileName, string text)
    {
        home.WriteProject(fileName, text);
        registry.Reload();
        return ProjectForm.From(ProjectConfigLoader.Load(PathOf(fileName), Providers), Providers);
    }

    private Task<ProjectSaveResult> SaveNew(ProjectForm form) =>
        files.SaveAsync(new ProjectSaveRequest(form, null, null, null), CancellationToken.None);

    private Task<ProjectSaveResult> SaveExisting(string fileName, ProjectForm original, ProjectForm edited, string loadedText) =>
        files.SaveAsync(new ProjectSaveRequest(edited, original, PathOf(fileName), loadedText), CancellationToken.None);

    private async Task SeedTicket(string key, string stageId, StageStatus status) =>
        await tickets.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = key, Title = key, StageId = stageId, Status = status,
            CreatedAt = Now, UpdatedAt = Now,
        }, CancellationToken.None);

    private void AssertNoLeftovers(string fileName)
    {
        Assert.False(File.Exists(PathOf(fileName) + ".tmp"));
        Assert.False(File.Exists(PathOf(fileName) + ".bak"));
    }

    [Fact]
    public async Task Saving_new_project_writes_file_and_reloads()
    {
        var result = await SaveNew(Form("orders"));

        Assert.True(result.Saved, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal(PathOf("orders.yaml"), result.File);
        Assert.Equal("orders", registry.Get("orders").Id);
        Assert.Equal(PathOf("orders.yaml"), registry.FileOf("orders"));
        Assert.Equal("orders", ProjectConfigLoader.Load(PathOf("orders.yaml"), Providers).Id);
        AssertNoLeftovers("orders.yaml");
    }

    [Fact]
    public async Task Save_refuses_existing_id()
    {
        home.WriteProject("acme.yaml", TestHome.ProjectYaml("orders")); // loaded under another file name
        home.WriteProject("billing.yaml", "not a project yet"); // not loaded, but the file name is taken
        registry.Reload();

        var orders = await SaveNew(Form("orders"));
        var billing = await SaveNew(Form("billing"));

        Assert.False(orders.Saved);
        Assert.Equal(["project 'orders' already exists"], orders.Errors);
        Assert.False(File.Exists(PathOf("orders.yaml")));
        Assert.False(billing.Saved);
        Assert.Equal(["project 'billing' already exists"], billing.Errors);
        Assert.Equal("not a project yet", File.ReadAllText(PathOf("billing.yaml")));
    }

    [Fact]
    public async Task Save_refuses_an_id_differing_only_by_case()
    {
        home.WriteProject("acme.yaml", TestHome.ProjectYaml("Orders"));
        home.WriteProject("Billing.yaml", "not a project yet");
        registry.Reload();

        var orders = await SaveNew(Form("orders"));
        var billing = await SaveNew(Form("billing"));

        Assert.Equal(["project 'orders' differs only by case from the existing 'Orders'; pick another id"], orders.Errors);
        Assert.False(File.Exists(PathOf("orders.yaml")));
        Assert.False(billing.Saved);
        Assert.Single(billing.Errors, e => e.StartsWith("project 'billing' ", StringComparison.Ordinal));
        Assert.Equal("not a project yet", File.ReadAllText(PathOf("Billing.yaml")));
    }

    [Fact]
    public async Task Changed_on_disk_is_reported_even_when_the_new_text_cannot_be_spliced()
    {
        var loaded = ProjectYamlWriter.NewFile(Form("orders"), Providers);
        var original = Existing("orders.yaml", loaded);
        var handEdited = loaded + "\nname: Second name\n"; // a repeated key the splicer refuses
        File.WriteAllText(PathOf("orders.yaml"), handEdited);
        var edited = original.Clone();
        edited.MaxAttemptsPerStage = 5;

        var result = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.Equal(["the file changed on disk since you opened it; reload the page"], result.Errors);
        Assert.Equal(handEdited, File.ReadAllText(PathOf("orders.yaml")));
    }

    [Fact]
    public async Task Save_refuses_when_file_changed_since_loaded()
    {
        var loaded = ProjectYamlWriter.NewFile(Form("orders"), Providers);
        var original = Existing("orders.yaml", loaded);
        var handEdited = loaded + "# added by hand while the screen was open\n";
        File.WriteAllText(PathOf("orders.yaml"), handEdited);
        var edited = original.Clone();
        edited.RiskAreas.Add(new RiskAreaForm { Name = "payments", Description = "Money", Paths = ["src/Payments/**"] });

        var result = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.False(result.Saved);
        Assert.Equal(["the file changed on disk since you opened it; reload the page"], result.Errors);
        Assert.Equal(handEdited, File.ReadAllText(PathOf("orders.yaml")));
        AssertNoLeftovers("orders.yaml");
    }

    [Fact]
    public async Task Save_refuses_removing_stage_with_active_tickets()
    {
        var loaded = ProjectYamlWriter.NewFile(Form("orders"), Providers);
        var original = Existing("orders.yaml", loaded);
        await SeedTicket("ORD-2", "plan", StageStatus.AwaitingApproval);
        await SeedTicket("ORD-1", "plan", StageStatus.Pending);
        await SeedTicket("ORD-3", "plan", StageStatus.Done); // done tickets do not hold a stage
        await SeedTicket("ORD-4", "triage", StageStatus.Pending);
        var edited = original.Clone();
        edited.Pipeline.RemoveAll(s => s.Id == "plan");

        var result = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.False(result.Saved);
        Assert.Equal(["stage 'plan' is removed but active tickets are at it: ORD-1, ORD-2"], result.Errors);
        Assert.Equal(loaded, File.ReadAllText(PathOf("orders.yaml")));
        AssertNoLeftovers("orders.yaml");
        Assert.Contains(registry.Get("orders").Pipeline, s => s.Id == "plan");
    }

    [Fact]
    public async Task Save_refuses_invalid_config_without_writing()
    {
        var noDone = Form("orders");
        noDone.Pipeline.RemoveAll(s => s.Kind == StageKind.Done);
        var noOwner = Form("gh", "github-projects", "github");
        noOwner.Tracker.Values.Remove("owner");

        var first = await SaveNew(noDone);
        var second = await SaveNew(noOwner);

        Assert.False(first.Saved);
        Assert.Contains("last stage must be kind 'done'", Assert.Single(first.Errors));
        Assert.False(File.Exists(PathOf("orders.yaml")));
        Assert.False(second.Saved);
        Assert.Contains("missing required 'tracker.owner'", Assert.Single(second.Errors));
        Assert.False(File.Exists(PathOf("gh.yaml")));
        AssertNoLeftovers("orders.yaml");

        // An edit of an existing file is parsed as the exact text about to be written, too.
        var loaded = ProjectYamlWriter.NewFile(Form("orders"), Providers);
        var original = Existing("orders.yaml", loaded);
        var edited = original.Clone();
        edited.Pipeline.RemoveAll(s => s.Kind == StageKind.Done);

        var existing = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.False(existing.Saved);
        Assert.Contains("last stage must be kind 'done'", Assert.Single(existing.Errors));
        Assert.Equal(loaded, File.ReadAllText(PathOf("orders.yaml")));
        AssertNoLeftovers("orders.yaml");
    }

    [Fact]
    public async Task Save_refuses_form_validation_errors_without_writing()
    {
        var form = Form("orders");
        form.Name = "Orders\nid: hijacked";

        var result = await SaveNew(form);

        Assert.False(result.Saved);
        Assert.Contains("name must be a single line", result.Errors);
        Assert.False(File.Exists(PathOf("orders.yaml")));
    }

    [Fact]
    public async Task Save_refuses_file_the_writer_cannot_edit_safely()
    {
        // The loader accepts a repeated top-level key (the last one wins); the splicer refuses to guess which to edit.
        var loaded = TestHome.ProjectYaml("orders") + "\nname: Second name\n";
        var original = Existing("orders.yaml", loaded);
        Assert.Equal("orders", registry.Get("orders").Id);
        var edited = original.Clone();
        edited.RiskAreas.Add(new RiskAreaForm { Name = "payments", Description = "Money", Paths = ["src/Payments/**"] });

        var result = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.False(result.Saved);
        Assert.EndsWith("edit this file by hand", Assert.Single(result.Errors));
        Assert.Equal(loaded, File.ReadAllText(PathOf("orders.yaml")));
        AssertNoLeftovers("orders.yaml");
    }

    [Fact]
    public async Task Save_refuses_bad_prompt_override()
    {
        Directory.CreateDirectory(PathOf("prompts"));
        File.WriteAllText(PathOf(Path.Combine("prompts", "plan.md")), "Plan the ticket. {{nope}}");
        var form = Form("orders");
        form.Prompts[StageKind.Plan] = "prompts/plan.md";

        var result = await SaveNew(form);

        Assert.False(result.Saved);
        Assert.Contains("unknown placeholder '{{nope}}'", Assert.Single(result.Errors));
        Assert.False(File.Exists(PathOf("orders.yaml")));
    }

    [Fact]
    public async Task Save_keeps_backup_and_hand_comments()
    {
        var loaded = "# Orders, set up by hand. Ask Dana before changing the tracker.\n"
            + TestHome.ProjectYaml("orders").ReplaceLineEndings("\n").Replace("build:\n", "# Pipelines run on every push.\nbuild:\n")
            + "\n# Keep this file in sync with the team wiki.\n";
        var original = Existing("orders.yaml", loaded);
        var edited = original.Clone();
        edited.RiskAreas.Add(new RiskAreaForm { Name = "payments", Description = "Money", Paths = ["src/Payments/**"] });

        var result = await SaveExisting("orders.yaml", original, edited, loaded);

        Assert.True(result.Saved, string.Join("; ", result.Errors));
        Assert.Equal(PathOf("orders.yaml"), result.File);
        Assert.Equal(loaded, File.ReadAllText(PathOf("orders.yaml.bak")));
        Assert.False(File.Exists(PathOf("orders.yaml.tmp")));
        var written = File.ReadAllText(PathOf("orders.yaml"));
        Assert.Contains("# Orders, set up by hand. Ask Dana before changing the tracker.\n", written);
        Assert.Contains("# Pipelines run on every push.\nbuild:\n", written);
        Assert.Contains("# Keep this file in sync with the team wiki.", written);
        Assert.Equal("payments", Assert.Single(registry.Get("orders").RiskAreas).Name);
    }
}
