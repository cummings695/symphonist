using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.Containers;

/// <summary>A fact that only runs on Linux/macOS.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "not applicable on Windows";
    }
}

/// <summary>A fact that only runs on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

public class DockerLauncherTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Func<ProcessSpec, int> ExitCode { get; set; } = _ => 0;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(ExitCode(spec), "", "", false));
        }
    }

    private readonly FakeProcessRunner fake = new();
    private readonly DockerLauncher launcher;

    public DockerLauncherTests() =>
        launcher = new DockerLauncher(fake, "/ctx", name => name == "claude-code-token" ? "tok-123" : null);

    private static AgentRunRequest Request(string workDir, string? image = null) =>
        new(workDir, "prompt",
            new AgentSettings("claude-code", Isolation.Container, ["api.nuget.org"], [], 5, null, image));

    [Fact]
    public void Wrap_mounts_only_workspace_and_passes_secrets_by_env_name()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request(@"C:\work\orders\ORD 1"));
        Assert.Equal("docker", spec.FileName);
        var mounts = spec.Arguments.Select((a, i) => (a, i)).Where(x => x.a == "-v")
            .Select(x => spec.Arguments[x.i + 1]).ToList();
        Assert.Equal(2, mounts.Count);
        Assert.Equal(@"C:\work\orders\ORD 1:/workspace", mounts[0]);
        Assert.Equal(Path.Combine(@"C:\work\orders\ORD 1", ".git") + ":/workspace/.git:ro", mounts[1]);
        Assert.DoesNotContain(spec.Arguments, a => a.StartsWith("--mount") || a.StartsWith("--volume"));
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("tok-123"));
        Assert.Equal("tok-123", spec.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("api.anthropic.com,statsig.anthropic.com,sentry.io,api.nuget.org",
            spec.Environment["SYMPHONIST_ALLOWED_DOMAINS"]);
        Assert.Equal(["claude", "-p"], spec.Arguments.TakeLast(2));
    }

    [Fact]
    public void Wrap_produces_the_exact_docker_run_command()
    {
        var spec = launcher.Wrap(new("claude", ["-p", "--x"], "/elsewhere", "the prompt",
            Timeout: TimeSpan.FromMinutes(3)), Request("/work/t1"));
        var name = DockerLauncher.ContainerName(spec);
        Assert.Matches("^symphonist-[0-9a-f]{32}$", name);
        string[] expectedPrefix =
        [
            "run", "--rm", "-i", "--pull=never", "--name", name!, "--label", "symphonist=agent",
            "--cap-add=NET_ADMIN", "--cap-add=NET_RAW",
            "-v", "/work/t1:/workspace", "-v", Path.Combine("/work/t1", ".git") + ":/workspace/.git:ro",
            "-w", "/workspace",
            "-e", "CLAUDE_CODE_OAUTH_TOKEN", "-e", "SYMPHONIST_ALLOWED_DOMAINS",
        ];
        Assert.Equal(expectedPrefix, spec.Arguments.Take(expectedPrefix.Length));
        Assert.Equal([DockerLauncher.DefaultImage, "claude", "-p", "--x"], spec.Arguments.TakeLast(4));
        Assert.Equal("the prompt", spec.StandardInput);
        Assert.Equal(TimeSpan.FromMinutes(3), spec.Timeout);
    }

    [Fact]
    public void Wrap_uses_project_image_override()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/w", image: "acme/agent:7"));
        Assert.Contains("acme/agent:7", spec.Arguments);
        Assert.DoesNotContain(DockerLauncher.DefaultImage, spec.Arguments);
        Assert.Equal(["acme/agent:7", "claude", "-p"], spec.Arguments.TakeLast(3));
    }

    [Fact]
    public void Wrap_never_pulls_and_names_each_container_uniquely()
    {
        var a = launcher.Wrap(new("claude", []), Request("/w"));
        var b = launcher.Wrap(new("claude", []), Request("/w"));
        Assert.Contains("--pull=never", a.Arguments);
        Assert.NotEqual(DockerLauncher.ContainerName(a), DockerLauncher.ContainerName(b));
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("--privileged")]
    public void Wrap_rejects_image_names_that_look_like_options(string image)
    {
        Assert.Throws<ArgumentException>(() => launcher.Wrap(new("claude", []), Request("/w", image)));
    }

    [Fact]
    public async Task Cleanup_force_removes_the_container()
    {
        var spec = launcher.Wrap(new("claude", []), Request("/w"));
        await launcher.CleanupAsync(spec, default);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal(["rm", "-f", DockerLauncher.ContainerName(spec)!], call.Arguments);
    }

    [Fact]
    public async Task Cleanup_ignores_specs_it_did_not_wrap()
    {
        await launcher.CleanupAsync(new ProcessSpec("claude", ["-p"]), default);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Wrap_removes_token_from_docker_env_when_secret_missing()
    {
        var noSecret = new DockerLauncher(fake, "/ctx", _ => null);
        var spec = noSecret.Wrap(new("claude", ["-p"]), Request("/w"));
        Assert.True(spec.Environment!.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.Null(spec.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public void Wrap_does_not_duplicate_base_domains()
    {
        var req = Request("/w") with
        {
            Settings = Request("/w").Settings with { AllowedDomains = ["sentry.io", "github.com"] },
        };
        var spec = launcher.Wrap(new("claude", []), req);
        Assert.Equal("api.anthropic.com,statsig.anthropic.com,sentry.io,github.com",
            spec.Environment!["SYMPHONIST_ALLOWED_DOMAINS"]);
    }

    [NonWindowsFact]
    public void Wrap_maps_developer_uid_on_unix()
    {
        var spec = launcher.Wrap(new("claude", []), Request("/w"));
        Assert.Contains("SYMPHONIST_UID", spec.Arguments);
        Assert.Contains("SYMPHONIST_GID", spec.Arguments);
        Assert.False(string.IsNullOrEmpty(spec.Environment!["SYMPHONIST_UID"]));
        Assert.False(string.IsNullOrEmpty(spec.Environment["SYMPHONIST_GID"]));
    }

    [WindowsFact]
    public void Wrap_passes_no_uid_on_windows()
    {
        var spec = launcher.Wrap(new("claude", []), Request("/w"));
        Assert.DoesNotContain("SYMPHONIST_UID", spec.Arguments);
        Assert.False(spec.Environment!.ContainsKey("SYMPHONIST_UID"));
    }

    [Fact]
    public async Task EnsureImage_does_nothing_when_image_exists()
    {
        await launcher.EnsureImageAsync(DockerLauncher.DefaultImage, default);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(["image", "inspect", DockerLauncher.DefaultImage], call.Arguments);
    }

    [Fact]
    public async Task EnsureImage_builds_default_image_from_context_when_missing()
    {
        fake.ExitCode = s => s.Arguments[0] == "image" ? 1 : 0;
        await launcher.EnsureImageAsync(DockerLauncher.DefaultImage, default);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("docker", fake.Calls[1].FileName);
        Assert.Equal(["build", "-t", DockerLauncher.DefaultImage, "/ctx"], fake.Calls[1].Arguments);
    }

    [Fact]
    public async Task EnsureImage_throws_when_build_fails()
    {
        fake.ExitCode = _ => 1;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.EnsureImageAsync(DockerLauncher.DefaultImage, default));
    }

    [Fact]
    public async Task EnsureImage_rejects_image_names_that_look_like_options()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.EnsureImageAsync("-q", default));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task EnsureImage_does_not_build_custom_images()
    {
        fake.ExitCode = _ => 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.EnsureImageAsync("acme/agent:7", default));
        Assert.Single(fake.Calls);
    }
}
