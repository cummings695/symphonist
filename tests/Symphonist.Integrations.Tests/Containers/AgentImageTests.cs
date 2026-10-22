using System.Diagnostics;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.Containers;

/// <summary>A fact that is skipped when no Docker daemon is reachable (`docker info` fails).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public DockerFactAttribute()
    {
        if (!Available.Value) Skip = "Docker is not available (`docker info` failed)";
    }

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("info");
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Builds the agent image once for all Docker tests.</summary>
public sealed class AgentImageFixture : IAsyncLifetime
{
    public ProcessRunner Runner { get; } = new();
    public DockerLauncher Launcher { get; }

    public AgentImageFixture() =>
        Launcher = new DockerLauncher(Runner, Path.Combine(AppContext.BaseDirectory, "Containers", "agent"), _ => null);

    public Task InitializeAsync() => Launcher.EnsureImageAsync(DockerLauncher.DefaultImage, CancellationToken.None);
    public Task DisposeAsync() => Task.CompletedTask;
}

[Trait("Category", "Docker")]
public class AgentImageTests(AgentImageFixture fixture) : IClassFixture<AgentImageFixture>, IDisposable
{
    private readonly string workspace = CreateWorkspace();

    private static string CreateWorkspace()
    {
        var ws = Directory.CreateTempSubdirectory("symphonist-docker-").FullName;
        Directory.CreateDirectory(Path.Combine(ws, ".git", "hooks"));
        return ws;
    }

    public void Dispose()
    {
        try { Directory.Delete(workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ProcessSpec Wrap(string fileName, string[] args, TimeSpan timeout)
    {
        var request = new AgentRunRequest(workspace, "",
            new AgentSettings("claude-code", Isolation.Container, ["api.nuget.org"], [], 5, null, null));
        return fixture.Launcher.Wrap(new ProcessSpec(fileName, args, Timeout: timeout), request);
    }

    private Task<ProcessResult> RunInContainer(string fileName, params string[] args) =>
        fixture.Runner.RunAsync(Wrap(fileName, args, TimeSpan.FromMinutes(3)), CancellationToken.None);

    private async Task<string> ContainersNamed(string name) =>
        (await fixture.Runner.RunAsync(new ProcessSpec("docker", ["ps", "-a", "-q", "--filter", $"name=^{name}$"]),
            CancellationToken.None)).StandardOutput.Trim();

    private static void AssertExit(int expected, ProcessResult r) =>
        Assert.True(r.ExitCode == expected, $"exit {r.ExitCode}\nstdout: {r.StandardOutput}\nstderr: {r.StandardError}");

    [DockerFact]
    public async Task Image_builds_and_has_tools()
    {
        AssertExit(0, await RunInContainer("claude", "--version"));
        AssertExit(0, await RunInContainer("dotnet", "--version"));
        AssertExit(0, await RunInContainer("git", "--version"));
        AssertExit(0, await RunInContainer("chromium", "--version"));
    }

    [DockerFact]
    public async Task Firewall_blocks_unlisted_and_allows_listed()
    {
        var blocked = await RunInContainer("curl", "-sS", "-m", "5", "https://example.com");
        Assert.True(blocked.ExitCode != 0, $"example.com should be blocked\n{blocked.StandardError}");
        Assert.Contains("init-firewall: ready", blocked.StandardError); // the curl itself ran, after the firewall
        AssertExit(0, await RunInContainer("curl", "-sS", "-m", "10", "-o", "/dev/null",
            "https://api.nuget.org/v3/index.json"));
    }

    [DockerFact]
    public async Task Port_53_is_open_only_to_the_configured_resolver()
    {
        // A public resolver that accepts TCP on 53: must be unreachable (port 53 is not a general egress channel).
        var arbitrary = await RunInContainer("bash", "-c", "timeout 5 bash -c 'exec 3<>/dev/tcp/1.1.1.1/53'");
        Assert.True(arbitrary.ExitCode != 0, $"tcp 1.1.1.1:53 should be blocked\n{arbitrary.StandardError}");

        // An allowed domain's IP is reachable over HTTPS only, not on 53.
        var allowedIp = await RunInContainer("bash", "-c",
            "ip=$(getent ahostsv4 api.nuget.org | awk 'NR==1 { print $1 }') && timeout 5 bash -c \"exec 3<>/dev/tcp/$ip/53\"");
        Assert.True(allowedIp.ExitCode != 0, $"tcp <api.nuget.org>:53 should be blocked\n{allowedIp.StandardError}");
    }

    [DockerFact]
    public async Task Git_directory_is_read_only_inside_the_container()
    {
        var r = await RunInContainer("touch", "/workspace/.git/hooks/pre-commit");
        Assert.NotEqual(0, r.ExitCode);
        Assert.False(File.Exists(Path.Combine(workspace, ".git", "hooks", "pre-commit")));
        AssertExit(0, await RunInContainer("touch", "/workspace/still-writable"));
    }

    [DockerFact]
    public async Task Timed_out_container_is_removed_by_cleanup()
    {
        var spec = Wrap("sleep", ["300"], TimeSpan.FromSeconds(2));
        var name = DockerLauncher.ContainerName(spec)!;
        var r = await fixture.Runner.RunAsync(spec, CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.NotEmpty(await ContainersNamed(name)); // killing the docker CLI leaves the container running

        await fixture.Launcher.CleanupAsync(spec, CancellationToken.None);
        Assert.Empty(await ContainersNamed(name));
    }

    [DockerFact]
    public async Task Agent_cannot_undo_the_firewall()
    {
        var r = await RunInContainer("iptables", "-F");
        Assert.NotEqual(0, r.ExitCode);
    }

    [DockerFact]
    public async Task Fails_closed_when_firewall_cannot_be_set_up()
    {
        // Same command line minus the NET_ADMIN/NET_RAW capabilities: iptables setup must fail and the
        // command must never run.
        var request = new AgentRunRequest(workspace, "",
            new AgentSettings("claude-code", Isolation.Container, [], [], 5, null, null));
        var spec = fixture.Launcher.Wrap(new ProcessSpec("touch", ["/workspace/should-not-exist"]), request);
        spec = spec with { Arguments = spec.Arguments.Where(a => !a.StartsWith("--cap-add=")).ToList() };
        var r = await fixture.Runner.RunAsync(spec with { Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
        AssertExit(70, r);
        Assert.False(File.Exists(Path.Combine(workspace, "should-not-exist")));
    }

    [DockerFact]
    public async Task Runs_as_non_root_with_workspace_writable()
    {
        var id = await RunInContainer("id", "-u");
        AssertExit(0, id);
        var expectedUid = OperatingSystem.IsWindows() ? "1000" : (await HostId()).Trim();
        Assert.Equal(expectedUid, id.StandardOutput.Trim());

        AssertExit(0, await RunInContainer("touch", "/workspace/x"));
        Assert.True(File.Exists(Path.Combine(workspace, "x")));
    }

    private static async Task<string> HostId() =>
        (await new ProcessRunner().RunAsync(new ProcessSpec("id", ["-u"]), CancellationToken.None)).StandardOutput;
}
