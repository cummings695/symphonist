using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Orchestration;

[Collection(SymphonistHomeCollection.Name)]
public sealed class DockerStartupTests : IDisposable
{
    private readonly TestHome home = new();
    private readonly FakeProcessRunner processes = new();
    private readonly string context;
    private readonly DockerLauncher docker;
    private readonly IsolationLauncher launcher;

    public DockerStartupTests()
    {
        context = Path.Combine(home.Path, "agent-context");
        Directory.CreateDirectory(context);
        File.WriteAllText(Path.Combine(context, "Dockerfile"), "FROM scratch\n");
        docker = new DockerLauncher(processes, context, _ => null);
        launcher = new IsolationLauncher(new HostLauncher(), docker);
    }

    public void Dispose() => home.Dispose();

    private Task StartAsync() =>
        new DockerStartup(new ProjectRegistry(NullLogger<ProjectRegistry>.Instance), docker, launcher,
            NullLogger<DockerStartup>.Instance).StartAsync(CancellationToken.None);

    private static AgentRunRequest Request(string? image = null) =>
        new("/w", "p", new AgentSettings("claude-code", Isolation.Container, [], [], 5, null, image));

    private static string Verb(ProcessSpec s) => string.Join(' ', s.Arguments.Take(2));

    [Fact]
    public async Task Host_only_projects_never_touch_docker()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders", TestHome.HostAgent));

        await StartAsync();

        Assert.Empty(processes.Calls);
    }

    [Fact]
    public async Task Docker_unavailable_fails_container_stages_with_a_clear_error()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));

        await StartAsync();

        Assert.Equal("version --format", Verb(Assert.Single(processes.Calls)));
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), Request()));
        Assert.Equal("Docker not available", ex.Message);
    }

    [Fact]
    public async Task Docker_unavailable_marks_each_image_not_prepared()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", image: "acme/agent:7"));

        await StartAsync();
        // A later container check switches containers back on, but not these images.
        launcher.EnableContainers();

        foreach (var request in new[] { Request(), Request("acme/agent:7") })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), request));
            Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
        }
    }

    [Fact]
    public async Task Removes_leftover_containers_then_ensures_each_image()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", image: "acme/agent:7"));
        home.WriteProject("hr.yaml", TestHome.ProjectYaml("hr", TestHome.HostAgent));
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "ps -aq" => new ProcessResult(0, "c1\n", "", false),
            "image inspect" when s.Arguments.Contains("acme/agent:7") => new ProcessResult(1, "", "No such image", false),
            "image inspect" => new ProcessResult(0, hash, "", false),
            _ => new ProcessResult(0, "", "", false),
        };

        await StartAsync();

        Assert.Equal(["version --format", "ps -aq", "rm -f", "image inspect", "image inspect"],
            processes.Calls.Select(Verb));
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request()).FileName);
        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request("acme/agent:7")));
        Assert.Contains("acme/agent:7", ex.Message);
    }

    [Fact]
    public async Task Builds_packages_images_and_disables_only_the_one_that_fails()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("graph.yaml", TestHome.ProjectYaml("graph", "type: claude-code\n  packages:\n    pip: [graphifyy]"));
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" when s.Arguments.Contains("{{.Id}}") => new ProcessResult(0, "sha256:base", "", false),
            "image inspect" when s.Arguments.Any(a => a.StartsWith(DockerLauncher.PackagesRepository))
                => new ProcessResult(1, "", "No such image", false),
            "image inspect" => new ProcessResult(0, hash, "", false),
            "build --pull=false" => new ProcessResult(1, "", "pip failed", false),
            _ => new ProcessResult(0, "", "", false),
        };

        await StartAsync();

        var build = Assert.Single(processes.Calls, s => s.Arguments[0] == "build");
        Assert.Contains("'graphifyy'", build.StandardInput);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request()).FileName);
        var packaged = Request() with
        {
            Settings = Request().Settings with { Packages = new AgentPackages([], ["graphifyy"], []) },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), packaged));
        Assert.Contains("pip failed", ex.Message);
    }

    [Fact]
    public async Task Image_of_a_project_added_after_startup_is_refused_until_a_check_enables_it()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" => new ProcessResult(0, hash, "", false),
            _ => new ProcessResult(0, "", "", false),
        };

        await StartAsync();

        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request()).FileName);
        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request("acme/agent:9")));
        Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
        launcher.EnableImage("acme/agent:9");
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request("acme/agent:9")).FileName);
    }

    [Fact]
    public async Task Leftover_cleanup_failure_does_not_stop_startup()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "ps -aq" => new ProcessResult(1, "", "boom", false),
            "image inspect" => new ProcessResult(0, hash, "", false),
            _ => new ProcessResult(0, "", "", false),
        };

        await StartAsync();

        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request()).FileName);
    }
}
