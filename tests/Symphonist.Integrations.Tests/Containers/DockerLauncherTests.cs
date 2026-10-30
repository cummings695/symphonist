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

    private static AgentRunRequest Request(string workDir, string? image = null,
        string? attachments = null) =>
        new(workDir, "prompt",
            new AgentSettings("claude-code", Isolation.Container, ["api.nuget.org"], [], 5, null, image),
            AttachmentsDirectory: attachments);

    [Fact]
    public void Wrap_mounts_attachments_read_only_when_set()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/work/t1", attachments: "/home/s/att/T-1"));
        var i = spec.Arguments.ToList().IndexOf("/home/s/att/T-1:/attachments:ro");
        Assert.True(i > 0);
        Assert.Equal("-v", spec.Arguments[i - 1]);
    }

    [Fact]
    public void Wrap_has_no_attachments_mount_when_unset()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/work/t1"));
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("/attachments"));
    }

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
        Assert.Equal("api.nuget.org", spec.Environment["SYMPHONIST_ALLOWED_DOMAINS"]);
        Assert.Equal(["claude", "-p"], spec.Arguments.TakeLast(2));
    }

    private static List<string> Mounts(ProcessSpec spec) =>
        spec.Arguments.Select((a, i) => (a, i)).Where(x => x.a == "-v").Select(x => spec.Arguments[x.i + 1]).ToList();

    [Fact]
    public void Read_only_request_mounts_workspace_read_only()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/work/t1") with { ReadOnlyWorkspace = true });
        Assert.Equal(["/work/t1:/workspace:ro"], Mounts(spec));
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("/workspace/.git"));
        Assert.Equal("/workspace", spec.Arguments[spec.Arguments.ToList().IndexOf("-w") + 1]);
        Assert.Contains("SYMPHONIST_ALLOWED_DOMAINS", spec.Arguments);
        Assert.Equal(["claude", "-p"], spec.Arguments.TakeLast(2));
    }

    [Fact]
    public void Default_request_keeps_writable_workspace_and_read_only_git()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/work/t1"));
        Assert.Equal(["/work/t1:/workspace", Path.Combine("/work/t1", ".git") + ":/workspace/.git:ro"], Mounts(spec));
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
            "-e", "CLAUDE_CODE_OAUTH_TOKEN", "-e", "SYMPHONIST_BASE_DOMAINS", "-e", "SYMPHONIST_ALLOWED_DOMAINS",
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

    private static AgentRunRequest Codex(CodexSettings codex, IReadOnlyList<string>? domains = null) =>
        new("/w", "prompt",
            new AgentSettings("codex", Isolation.Container, domains ?? ["api.nuget.org"], [], 5, null, null)
            {
                Codex = codex,
            });

    private static List<string> EnvNames(ProcessSpec spec) =>
        spec.Arguments.Select((a, i) => (a, i)).Where(x => x.a == "-e").Select(x => spec.Arguments[x.i + 1]).ToList();

    [Fact]
    public void Allowed_domains_no_longer_include_base_domains()
    {
        var req = Request("/w") with
        {
            Settings = Request("/w").Settings with { AllowedDomains = ["github.com", "api.nuget.org"] },
        };
        var spec = launcher.Wrap(new("claude", []), req);
        Assert.Equal("github.com,api.nuget.org", spec.Environment!["SYMPHONIST_ALLOWED_DOMAINS"]);
    }

    [Fact]
    public void Claude_run_gets_claude_token_and_anthropic_domains_only()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/w"));
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", EnvNames(spec));
        Assert.DoesNotContain("CODEX_API_KEY", spec.Arguments);
        Assert.False(spec.Environment!.ContainsKey("CODEX_API_KEY"));
        Assert.Contains("SYMPHONIST_BASE_DOMAINS", EnvNames(spec));
        Assert.Equal("api.anthropic.com,statsig.anthropic.com,sentry.io", spec.Environment["SYMPHONIST_BASE_DOMAINS"]);
    }

    [Fact]
    public void Codex_api_key_run_gets_its_project_secret_and_openai_domains_only()
    {
        var codex = new DockerLauncher(fake, "/ctx", name => name switch
        {
            "openai-orders" => "sk-x",
            "claude-code-token" => "tok-123",
            _ => null,
        });
        var spec = codex.Wrap(new("codex", ["exec"]), Codex(new(CodexAuth.ApiKey, "openai-orders")));
        Assert.Contains(DockerLauncher.CodexApiKeyVar, EnvNames(spec));
        Assert.Equal("sk-x", spec.Environment![DockerLauncher.CodexApiKeyVar]);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("sk-x"));
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", spec.Arguments);
        Assert.False(spec.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.DoesNotContain(spec.Environment.Values, v => v == "tok-123");
        // An API key needs only the API; the ChatGPT sign-in domains stay closed.
        Assert.Equal("api.openai.com", spec.Environment[DockerLauncher.BaseDomainsVar]);
        Assert.Equal("api.nuget.org", spec.Environment["SYMPHONIST_ALLOWED_DOMAINS"]);
    }

    [Fact]
    public void Codex_chatgpt_run_gets_no_api_key()
    {
        var codex = new DockerLauncher(fake, "/ctx", _ => "anything");
        var spec = codex.Wrap(new("codex", ["exec"]), Codex(new(CodexAuth.ChatGpt, CodexSettings.DefaultApiKeySecret)));
        Assert.DoesNotContain(DockerLauncher.CodexApiKeyVar, spec.Arguments);
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", spec.Arguments);
        Assert.False(spec.Environment!.ContainsKey(DockerLauncher.CodexApiKeyVar));
        Assert.False(spec.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.Equal("api.openai.com,auth.openai.com,chatgpt.com", spec.Environment[DockerLauncher.BaseDomainsVar]);
    }

    [Fact]
    public void Codex_api_key_run_without_secret_throws_naming_the_secret()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            launcher.Wrap(new("codex", ["exec"]), Codex(new(CodexAuth.ApiKey, "openai-orders"))));
        Assert.Contains("openai-orders", ex.Message);
        Assert.Contains("not set", ex.Message);
    }

    [Fact]
    public void Unknown_agent_type_is_refused()
    {
        var req = Request("/w") with { Settings = Request("/w").Settings with { Type = "other" } };
        Assert.ThrowsAny<ArgumentException>(() => launcher.Wrap(new("other", []), req));
    }

    [Fact]
    public void Command_environment_is_forwarded_by_name()
    {
        var command = new ProcessSpec("codex", ["exec"],
            Environment: new Dictionary<string, string?> { ["CODEX_HOME"] = "/symphonist-run/home" });
        var spec = launcher.Wrap(command, Request("/w"));
        Assert.Contains("CODEX_HOME", EnvNames(spec));
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("/symphonist-run/home"));
        Assert.Equal("/symphonist-run/home", spec.Environment!["CODEX_HOME"]);
        Assert.Equal(["codex", "exec"], spec.Arguments.TakeLast(2));
    }

    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("CODEX_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("SYMPHONIST_BASE_DOMAINS")]
    [InlineData("SYMPHONIST_ALLOWED_DOMAINS")]
    [InlineData("SYMPHONIST_UID")]
    [InlineData("symphonist_allowed_domains")]
    [InlineData("DOCKER_HOST")]
    [InlineData("A=B")]
    [InlineData("-v")]
    [InlineData("")]
    [InlineData("BASH_ENV")]
    [InlineData("LD_PRELOAD")]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("codex_home")]
    [InlineData("CODEX_HOME=x")]
    public void Command_environment_forwards_only_allowlisted_names(string name)
    {
        var command = new ProcessSpec("codex", ["exec"],
            Environment: new Dictionary<string, string?> { [name] = "x" });
        Assert.Throws<InvalidOperationException>(() => launcher.Wrap(command, Request("/w")));
    }

    [Fact]
    public void Mounts_become_volume_arguments()
    {
        var command = new ProcessSpec("codex", ["exec"], Mounts:
        [
            new BindMount("C:/tmp/run", "/symphonist-run"),
            new BindMount("C:/tmp/cfg", "/symphonist-cfg", ReadOnly: true),
        ]);
        var spec = launcher.Wrap(command, Request("/work/t1"));
        Assert.Equal([
            "/work/t1:/workspace", Path.Combine("/work/t1", ".git") + ":/workspace/.git:ro",
            "C:/tmp/run:/symphonist-run", "C:/tmp/cfg:/symphonist-cfg:ro",
        ], Mounts(spec));
        Assert.Null(spec.Mounts);
        Assert.True(spec.Arguments.ToList().IndexOf("C:/tmp/run:/symphonist-run")
            < spec.Arguments.ToList().IndexOf(DockerLauncher.DefaultImage));
    }

    [Theory]
    [InlineData("/workspace")]
    [InlineData("/workspace/")]
    [InlineData("/workspace/.git")]
    [InlineData("/workspace/sub/dir")]
    [InlineData("//workspace")]
    [InlineData("/x/../workspace")]
    [InlineData("/./workspace")]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/run:rw")]
    [InlineData("/usr/local/bin")]
    [InlineData("/usr/local/bin/init-firewall.sh")]
    [InlineData("/etc")]
    public void Mounts_cannot_touch_the_workspace_or_system_paths(string containerPath)
    {
        var command = new ProcessSpec("codex", ["exec"], Mounts: [new BindMount("C:/tmp/run", containerPath)]);
        Assert.Throws<InvalidOperationException>(() => launcher.Wrap(command, Request("/w")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("named-volume")]
    [InlineData("/tmp/a:/etc")]
    [InlineData("C:/tmp/a:/etc")]
    [InlineData("tmp/run")]
    [InlineData(@"tmp\run")]
    [InlineData("./run")]
    [InlineData("../run")]
    [InlineData("C:run/x")]
    [InlineData(@"\run")]
    public void Mounts_need_a_plain_host_directory_path(string hostPath)
    {
        var command = new ProcessSpec("codex", ["exec"], Mounts: [new BindMount(hostPath, "/symphonist-run")]);
        Assert.Throws<InvalidOperationException>(() => launcher.Wrap(command, Request("/w")));
    }

    [Theory]
    [InlineData(@"C:\Users\dev\AppData\Local\Temp\run")]
    [InlineData("C:/tmp/run")]
    [InlineData(@"\\server\share\run")]
    [InlineData("/tmp/run")]
    public void Mounts_accept_windows_and_unix_host_paths(string hostPath)
    {
        var command = new ProcessSpec("codex", ["exec"], Mounts: [new BindMount(hostPath, "/symphonist-run")]);
        Assert.Contains($"{hostPath}:/symphonist-run", Mounts(launcher.Wrap(command, Request("/w"))));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(127, false)]
    public async Task HasCommand_runs_the_command_version_in_the_image(int exitCode, bool expected)
    {
        fake.ExitCode = _ => exitCode;
        Assert.Equal(expected, await launcher.HasCommandAsync("acme/agent:7", "codex", default));
        var call = Assert.Single(fake.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal(["run", "--rm", "--pull=never", "--network=none", "--cap-drop=ALL", "--entrypoint", "codex",
            "acme/agent:7", "--version"], call.Arguments);
    }

    [Theory]
    [InlineData("-v", "codex")]
    [InlineData("acme/agent:7", "-v")]
    [InlineData("acme/agent:7", "")]
    public async Task HasCommand_rejects_option_like_names(string image, string command)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.HasCommandAsync(image, command, default));
        Assert.Empty(fake.Calls);
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

    private static readonly AgentPackages Graphify = new(["ripgrep"], ["graphifyy"], ["typescript"]);

    private static AgentSettings WithPackages(AgentPackages packages, string? image = null) =>
        Request("/w", image).Settings with { Packages = packages };

    [Fact]
    public void Image_without_packages_is_the_configured_image()
    {
        Assert.Equal(DockerLauncher.DefaultImage, DockerLauncher.ImageFor(Request("/w").Settings));
        Assert.Equal("acme/agent:7", DockerLauncher.ImageFor(Request("/w", "acme/agent:7").Settings));
    }

    [Fact]
    public void Image_with_packages_is_tagged_by_base_and_packages()
    {
        var image = DockerLauncher.ImageFor(WithPackages(Graphify));
        Assert.Matches("^symphonist/agent-packages:[0-9a-f]{16}$", image);
        Assert.Equal(image, DockerLauncher.ImageFor(WithPackages(new(["ripgrep"], ["graphifyy"], ["typescript"]))));
        Assert.NotEqual(image, DockerLauncher.ImageFor(WithPackages(Graphify, "acme/agent:7")));
        Assert.NotEqual(image, DockerLauncher.ImageFor(WithPackages(Graphify with { Pip = ["graphifyy==0.8.33"] })));
    }

    [Fact]
    public void Wrap_runs_in_the_packages_image()
    {
        var spec = launcher.Wrap(new("claude", ["-p"]), Request("/w") with { Settings = WithPackages(Graphify) });
        Assert.Equal([DockerLauncher.ImageFor(WithPackages(Graphify)), "claude", "-p"], spec.Arguments.TakeLast(3));
    }

    [Fact]
    public void Packages_dockerfile_installs_each_kind_on_the_base()
    {
        Assert.Equal("""
            # Generated by Symphonist from agent.packages.
            FROM acme/agent:7
            RUN apt-get update \
             && apt-get install -y --no-install-recommends 'ripgrep' 'python3' 'python3-venv' \
             && rm -rf /var/lib/apt/lists/*
            RUN python3 -m venv /opt/symphonist-pip \
             && /opt/symphonist-pip/bin/pip install --no-cache-dir 'graphifyy'
            ENV PATH=/opt/symphonist-pip/bin:$PATH
            RUN npm install -g 'typescript' \
             && npm cache clean --force

            """.Replace("\r\n", "\n"), DockerLauncher.PackagesDockerfile("acme/agent:7", Graphify));
    }

    [Fact]
    public void Packages_dockerfile_skips_installers_without_packages()
    {
        var dockerfile = DockerLauncher.PackagesDockerfile(DockerLauncher.DefaultImage, new([], [], ["typescript"]));
        Assert.DoesNotContain("apt-get", dockerfile);
        Assert.DoesNotContain("pip", dockerfile);
        Assert.Contains("npm install -g 'typescript'", dockerfile);
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("--registry=x")]
    [InlineData("a\nRUN evil")]
    public void Packages_dockerfile_rejects_unsafe_items(string item)
    {
        Assert.Throws<ArgumentException>(() => DockerLauncher.PackagesDockerfile("img", new([item], [], [])));
    }

    [Fact]
    public void Image_names_with_whitespace_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => DockerLauncher.ImageFor(WithPackages(Graphify, "img\nRUN evil")));
    }

    private const string BaseIdFormat = "{{index .Config.Labels \"symphonist.base-id\"}}";

    [Fact]
    public async Task EnsureImage_builds_the_packages_image_from_stdin_when_missing_or_stale()
    {
        var settings = WithPackages(Graphify, "acme/agent:7");
        var image = DockerLauncher.ImageFor(settings);
        fake.Stdout = s => s.Arguments.Contains("{{.Id}}") ? "sha256:base1\n" : "sha256:old\n";

        await launcher.EnsureImageAsync(settings, default);

        Assert.Equal(4, fake.Calls.Count);
        Assert.Equal(["image", "inspect", "acme/agent:7"], fake.Calls[0].Arguments);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", "acme/agent:7"], fake.Calls[1].Arguments);
        Assert.Equal(["image", "inspect", "--format", BaseIdFormat, image], fake.Calls[2].Arguments);
        Assert.Equal(["build", "--pull=false", "-t", image, "--label", "symphonist.base-id=sha256:base1", "-"],
            fake.Calls[3].Arguments);
        Assert.Equal(DockerLauncher.PackagesDockerfile("acme/agent:7", Graphify), fake.Calls[3].StandardInput);
    }

    [Fact]
    public async Task EnsureImage_keeps_a_packages_image_built_from_the_current_base()
    {
        fake.Stdout = _ => "sha256:base1\n";

        await launcher.EnsureImageAsync(WithPackages(Graphify, "acme/agent:7"), default);

        Assert.Equal(3, fake.Calls.Count);
        Assert.DoesNotContain(fake.Calls, c => c.Arguments[0] == "build");
    }

    [Fact]
    public async Task EnsureImage_without_packages_only_ensures_the_base()
    {
        await launcher.EnsureImageAsync(Request("/w", "acme/agent:7").Settings, default);
        Assert.Equal(["image", "inspect", "acme/agent:7"], Assert.Single(fake.Calls).Arguments);
    }

    [Fact]
    public async Task EnsureImage_throws_when_the_packages_build_fails()
    {
        fake.Stdout = s => s.Arguments.Contains("{{.Id}}") ? "sha256:base1" : "";
        fake.ExitCode = s => s.Arguments[0] == "build" ? 1 : 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.EnsureImageAsync(WithPackages(Graphify, "acme/agent:7"), default));
        Assert.Contains("agent.packages", ex.Message);
    }

    [Fact]
    public async Task EnsureImage_does_not_build_custom_images()
    {
        fake.ExitCode = _ => 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.EnsureImageAsync("acme/agent:7", default));
        Assert.Single(fake.Calls);
    }
}
