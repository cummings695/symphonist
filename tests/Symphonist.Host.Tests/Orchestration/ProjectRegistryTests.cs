using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Core.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Onboarding;
using Symphonist.Integrations;

namespace Symphonist.Host.Tests.Orchestration;

[Collection(SymphonistHomeCollection.Name)]
public sealed class ProjectRegistryTests : IDisposable
{
    private readonly TestHome home = new();

    public void Dispose() => home.Dispose();

    private static ProjectRegistry Create() => new(NullLogger<ProjectRegistry>.Instance);

    [Fact]
    public void Invalid_project_file_is_skipped()
    {
        home.WriteProject("a-broken.yaml", "id: [unclosed");
        home.WriteProject("b-incomplete.yaml", "id: incomplete\n");
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));

        Assert.Equal(["orders"], Create().Projects.Select(p => p.Id));
    }

    [Fact]
    public void Null_pipeline_item_or_unexpected_error_skips_only_that_file()
    {
        home.WriteProject("a-null-stage.yaml", TestHome.ProjectYaml("nullstage").Split("pipeline:")[0] + "pipeline: [~]\n");
        // A NUL character in a path makes Path.GetFullPath throw ArgumentException (not a ProjectConfigException).
        home.WriteProject("b-bad-path.yaml", TestHome.ProjectYaml("badpath").Replace("localPath: ../acme-badpath", "localPath: \"a\\0b\""));
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));

        Assert.Equal(["orders"], Create().Projects.Select(p => p.Id));
    }

    [Fact]
    public void Paused_changed_raised_only_on_change()
    {
        var registry = Create();
        var raised = 0;
        registry.PausedChanged += () => raised++;

        registry.SetPaused("orders", true);
        registry.SetPaused("orders", true);
        Assert.Equal(1, raised);

        registry.SetPaused("orders", false);
        registry.SetPaused("orders", false);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Duplicate_project_ids_are_all_skipped()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("orders-copy.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing"));

        Assert.Equal(["billing"], Create().Projects.Select(p => p.Id));
    }

    [Fact]
    public void Unreadable_paused_file_means_nothing_paused()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var paused = Path.Combine(home.Path, "paused.json");
        File.WriteAllText(paused, """["orders"]""");

        ProjectRegistry registry;
        using (new FileStream(paused, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            registry = Create();

        Assert.False(registry.IsPaused("orders"));
        Assert.Single(registry.Projects);
    }

    [Fact]
    public void Reload_picks_up_added_changed_and_removed_files()
    {
        home.WriteProject("a.yaml", TestHome.ProjectYaml("a"));
        var registry = Create();
        Assert.Equal(["a"], registry.Projects.Select(p => p.Id));

        home.WriteProject("b.yaml", ProjectYamlWriter.NewFile(ProjectFileStoreTests.Form("b", "github-projects", "github"),
            BuiltInProviders.Registry));
        home.WriteProject("a.yaml", TestHome.ProjectYaml("a").Replace("name: Project a", "name: Renamed a"));
        var reload = registry.Reload();

        Assert.Equal(["a", "b"], reload.ProjectIds);
        Assert.Empty(reload.Errors);
        Assert.Equal(["a", "b"], registry.Projects.Select(p => p.Id));
        Assert.Equal("Renamed a", registry.Get("a").Name);
        Assert.Equal("github-projects", registry.Get("b").Tracker.Type);

        File.Delete(Path.Combine(home.Projects, "b.yaml"));
        Assert.Equal(["a"], registry.Reload().ProjectIds);
        Assert.Equal(["a"], registry.Projects.Select(p => p.Id));
    }

    [Fact]
    public void Reload_reports_errors_per_file_and_raises_projects_changed()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var registry = Create();
        var raised = 0;
        registry.ProjectsChanged += () => raised++;
        home.WriteProject("bad.yaml", "id: bad\n");
        home.WriteProject("unknown.yaml", TestHome.ProjectYaml("unknown").Replace("type: jira", "type: nope"));

        var reload = registry.Reload();

        Assert.Equal(1, raised);
        Assert.Equal(["orders"], reload.ProjectIds);
        Assert.Equal(["bad.yaml", "unknown.yaml"], reload.Errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("missing required", reload.Errors["bad.yaml"]);
        Assert.Contains("unsupported tracker.type 'nope'", reload.Errors["unknown.yaml"]);
        Assert.Equal(reload.Errors, registry.LoadErrors);

        File.Delete(Path.Combine(home.Projects, "bad.yaml"));
        File.Delete(Path.Combine(home.Projects, "unknown.yaml"));
        registry.Reload();
        Assert.Equal(2, raised);
        Assert.Empty(registry.LoadErrors);
    }

    [Fact]
    public void Duplicate_ids_are_reported_for_each_file()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("orders-copy.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing"));

        var registry = Create();

        Assert.Equal(["billing"], registry.Projects.Select(p => p.Id));
        Assert.Equal(2, registry.LoadErrors.Count);
        Assert.Equal("project id 'orders' is declared by 2 files", registry.LoadErrors["orders.yaml"]);
        Assert.Equal("project id 'orders' is declared by 2 files", registry.LoadErrors["orders-copy.yaml"]);
        Assert.Null(registry.FileOf("orders"));
    }

    [Fact]
    public void File_of_returns_declaring_file()
    {
        home.WriteProject("acme-orders.yml", TestHome.ProjectYaml("orders"));

        var registry = Create();

        Assert.Equal(Path.Combine(home.Projects, "acme-orders.yml"), registry.FileOf("orders"));
        Assert.Null(registry.FileOf("billing"));
    }
}
