using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Host.Orchestration;

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
}
