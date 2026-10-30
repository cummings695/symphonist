using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.ClaudeCode;

public class IsolationLauncherTests
{
    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
    }

    /// <summary>Records every call; reports the given isolation for every request.</summary>
    private sealed class RecordingLauncher(bool isolates) : IAgentLauncher
    {
        public List<string> Calls { get; } = [];
        public bool ProvidesIsolation(AgentRunRequest request)
        {
            Calls.Add("ProvidesIsolation");
            return isolates;
        }
        public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
        {
            Calls.Add("Wrap");
            return agentCommand;
        }
        public Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct)
        {
            Calls.Add("Cleanup");
            return Task.CompletedTask;
        }
    }

    private readonly RecordingProcessRunner processes = new();

    private static AgentRunRequest Request(Isolation isolation) =>
        new("/work", "prompt", new AgentSettings("claude-code", isolation, [], ["Read"], 5, null, null));

    private IsolationLauncher Real() =>
        new(new HostLauncher(), new DockerLauncher(processes, "/ctx", _ => null));

    [Fact]
    public async Task Container_settings_never_reach_the_host_launcher()
    {
        var host = new RecordingLauncher(false);
        var container = new RecordingLauncher(true);
        var launcher = new IsolationLauncher(host, container);
        var request = Request(Isolation.Container);

        Assert.True(launcher.ProvidesIsolation(request));
        var spec = launcher.Wrap(new ProcessSpec("claude", ["-p"]), request);
        await launcher.CleanupAsync(spec, request, default);

        Assert.Empty(host.Calls);
        Assert.Equal(["ProvidesIsolation", "Wrap", "Cleanup"], container.Calls);
    }

    [Fact]
    public async Task Host_settings_never_reach_the_container_launcher()
    {
        var host = new RecordingLauncher(false);
        var container = new RecordingLauncher(true);
        var launcher = new IsolationLauncher(host, container);
        var request = Request(Isolation.Host);

        Assert.False(launcher.ProvidesIsolation(request));
        var spec = launcher.Wrap(new ProcessSpec("claude", ["-p"]), request);
        await launcher.CleanupAsync(spec, request, default);

        Assert.Empty(container.Calls);
        Assert.Equal(["ProvidesIsolation", "Wrap", "Cleanup"], host.Calls);
    }

    [Fact]
    public async Task Host_settings_never_get_the_bypass_flag()
    {
        var runner = new ClaudeCodeRunner(processes, Real());
        await runner.RunAsync(Request(Isolation.Host), default);

        var spec = Assert.Single(processes.Calls);
        Assert.Equal("claude", spec.FileName);
        Assert.DoesNotContain(HostLauncher.PermissionBypassFlag, spec.Arguments);
        Assert.Contains("--allowedTools", spec.Arguments);
    }

    [Fact]
    public async Task Container_settings_run_docker_with_the_bypass_flag_inside()
    {
        var runner = new ClaudeCodeRunner(processes, Real());
        await runner.RunAsync(Request(Isolation.Container), default);

        var spec = Assert.Single(processes.Calls);
        Assert.Equal("docker", spec.FileName);
        var args = spec.Arguments.ToList();
        Assert.True(args.IndexOf(HostLauncher.PermissionBypassFlag) > args.IndexOf(DockerLauncher.DefaultImage));
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public void Disabled_containers_fail_container_runs_only()
    {
        var launcher = Real();
        launcher.DisableContainers(IsolationLauncher.DockerNotAvailable);

        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", ["-p"]), Request(Isolation.Container)));
        Assert.Equal("Docker not available", ex.Message);
        Assert.Equal("claude", launcher.Wrap(new ProcessSpec("claude", ["-p"]), Request(Isolation.Host)).FileName);
    }

    [Fact]
    public void Disabled_image_fails_only_runs_using_it()
    {
        var launcher = Real();
        launcher.DisableImage("acme/agent:7", "docker image 'acme/agent:7' not found");
        var custom = Request(Isolation.Container) with
        {
            Settings = Request(Isolation.Container).Settings with { Image = "acme/agent:7" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), custom));
        Assert.Equal("docker image 'acme/agent:7' not found", ex.Message);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(Isolation.Container)).FileName);
    }

    [Fact]
    public void Enable_containers_and_image_clear_problems()
    {
        var launcher = Real();
        var custom = Request(Isolation.Container) with
        {
            Settings = Request(Isolation.Container).Settings with { Image = "acme/agent:7" },
        };
        launcher.DisableContainers(IsolationLauncher.DockerNotAvailable);
        launcher.DisableImage("acme/agent:7", "docker image 'acme/agent:7' not found");
        launcher.DisableImage(DockerLauncher.DefaultImage, "build failed");

        launcher.EnableContainers();
        Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), custom));
        launcher.EnableImage("acme/agent:7");

        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), custom).FileName);
        // Only the named image is cleared.
        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request(Isolation.Container)));
        Assert.Equal("build failed", ex.Message);
    }

    [Fact]
    public void Required_prepared_images_refuse_others_until_enabled()
    {
        var launcher = Real();
        launcher.RequirePreparedImages();

        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request(Isolation.Container)));
        Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
        Assert.Equal("claude", launcher.Wrap(new ProcessSpec("claude", ["-p"]), Request(Isolation.Host)).FileName);
        launcher.EnableImage(DockerLauncher.DefaultImage);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(Isolation.Container)).FileName);
    }

    [Theory]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox=true")]
    [InlineData("--yolo")]
    [InlineData("--sandbox", "danger-full-access")]
    [InlineData("-s", "danger-full-access")]
    [InlineData("--sandbox=danger-full-access")]
    [InlineData("-s=danger-full-access")]
    [InlineData("-sdanger-full-access")]
    [InlineData("--sandbox", "DANGER-FULL-ACCESS")]
    [InlineData("-c", "sandbox_mode=danger-full-access")]
    [InlineData("--config", "sandbox_mode=\"danger-full-access\"")]
    public void Host_launcher_refuses_codex_bypass_and_full_access(params string[] bypass)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new HostLauncher().Wrap(
            new ProcessSpec("codex", ["exec", .. bypass]), Request(Isolation.Host)));
        Assert.Contains("sandbox", ex.Message);
    }

    [Fact]
    public void Host_launcher_allows_codex_workspace_sandbox()
    {
        var spec = new HostLauncher().Wrap(new ProcessSpec("codex", ["exec", "--sandbox", "workspace-write"]),
            Request(Isolation.Host));
        Assert.Equal("/work", spec.WorkingDirectory);
    }

    [Fact]
    public void Host_launcher_refuses_mounts()
    {
        var command = new ProcessSpec("codex", ["exec"], Mounts: [new BindMount("/tmp/run", "/symphonist-run")]);
        var ex = Assert.Throws<InvalidOperationException>(() => new HostLauncher().Wrap(command, Request(Isolation.Host)));
        Assert.Contains("mount", ex.Message);
    }
}
