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
        public Func<ProcessSpec, string> Stdout { get; set; } = _ => "";
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(ExitCode(spec), Stdout(spec), "", false));
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
        await launcher.CleanupAsync(spec, Request("/w"), default);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal(["rm", "-f", DockerLauncher.ContainerName(spec)!], call.Arguments);
    }

    [Fact]
    public async Task Cleanup_ignores_specs_it_did_not_wrap()
    {
        await launcher.CleanupAsync(new ProcessSpec("claude", ["-p"]), Request("/w"), default);
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

    private const string HashFormat = "{{index .Config.Labels \"symphonist.context-hash\"}}";

    /// <summary>A launcher over a real (temporary) build context, as EnsureImage hashes it.</summary>
    private sealed class Context : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "symphonist-ctx-" + Guid.NewGuid().ToString("N"));

        public Context()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "sub"));
            Write("Dockerfile", "FROM ubuntu:24.04\n");
            Write(Path.Combine("sub", "init.sh"), "echo hi\n");
        }

        public void Write(string relative, string text) => File.WriteAllText(Path.Combine(Directory, relative), text);

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    [Fact]
    public void Context_hash_is_stable_and_tracks_contents_and_names()
    {
        using var a = new Context();
        using var b = new Context();
        var hash = DockerLauncher.ContextHash(a.Directory);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, DockerLauncher.ContextHash(a.Directory));
        Assert.Equal(hash, DockerLauncher.ContextHash(b.Directory)); // independent of location

        b.Write("Dockerfile", "FROM ubuntu:24.04\nRUN true\n");
        var changed = DockerLauncher.ContextHash(b.Directory);
        Assert.NotEqual(hash, changed);

        File.Move(Path.Combine(b.Directory, "sub", "init.sh"), Path.Combine(b.Directory, "sub", "init2.sh"));
        Assert.NotEqual(changed, DockerLauncher.ContextHash(b.Directory));
    }

    [Fact]
    public async Task EnsureImage_does_nothing_when_default_image_matches_the_context()
    {
        using var ctx = new Context();
        var docker = new DockerLauncher(fake, ctx.Directory, _ => null);
        fake.Stdout = _ => DockerLauncher.ContextHash(ctx.Directory) + "\n";

        await docker.EnsureImageAsync(DockerLauncher.DefaultImage, default);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(["image", "inspect", "--format", HashFormat, DockerLauncher.DefaultImage], call.Arguments);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("unlabeled")]
    public async Task EnsureImage_builds_labeled_default_image_when_missing_or_stale(string state)
    {
        using var ctx = new Context();
        var docker = new DockerLauncher(fake, ctx.Directory, _ => null);
        var hash = DockerLauncher.ContextHash(ctx.Directory);
        fake.ExitCode = s => state == "missing" && s.Arguments[0] == "image" ? 1 : 0;
        fake.Stdout = s => s.Arguments[0] != "image" ? "" : state == "stale" ? new string('0', 64) : "<no value>";

        await docker.EnsureImageAsync(DockerLauncher.DefaultImage, default);

        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("docker", fake.Calls[1].FileName);
        Assert.Equal(["build", "-t", DockerLauncher.DefaultImage, "--label", $"symphonist.context-hash={hash}", ctx.Directory],
            fake.Calls[1].Arguments);
    }

    [Fact]
    public async Task EnsureImage_only_checks_custom_images_exist()
    {
        await launcher.EnsureImageAsync("acme/agent:7", default);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(["image", "inspect", "acme/agent:7"], call.Arguments);
    }

    [Fact]
    public async Task Leftover_agent_containers_are_force_removed()
    {
        fake.Stdout = s => s.Arguments[0] == "ps" ? "abc123\r\ndef456\n\n" : "";

        var removed = await launcher.RemoveLeftoverContainersAsync(default);

        Assert.Equal(2, removed);
        Assert.Equal(["ps", "-aq", "--filter", "label=symphonist=agent"], fake.Calls[0].Arguments);
        Assert.Equal(["rm", "-f", "abc123"], fake.Calls[1].Arguments);
        Assert.Equal(["rm", "-f", "def456"], fake.Calls[2].Arguments);
    }

    [Fact]
    public async Task Leftover_listing_failure_throws()
    {
        fake.ExitCode = _ => 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.RemoveLeftoverContainersAsync(default));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Availability_asks_the_docker_daemon()
    {
        Assert.True(await launcher.IsAvailableAsync(default));
        Assert.Equal(["version", "--format", "{{.Server.Version}}"], Assert.Single(fake.Calls).Arguments);
        fake.ExitCode = _ => 1;
        Assert.False(await launcher.IsAvailableAsync(default));
    }

    [Fact]
    public async Task Availability_is_false_when_docker_cannot_be_started()
    {
        var missing = new DockerLauncher(new ThrowingProcessRunner(), "/ctx", _ => null);
        Assert.False(await missing.IsAvailableAsync(default));
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified");
    }

    [Fact]
    public async Task EnsureImage_throws_when_build_fails()
    {
        using var ctx = new Context();
        var docker = new DockerLauncher(fake, ctx.Directory, _ => null);
        fake.ExitCode = _ => 1;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => docker.EnsureImageAsync(DockerLauncher.DefaultImage, default));
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
