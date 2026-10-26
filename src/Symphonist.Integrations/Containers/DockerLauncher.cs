using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Containers;

/// <summary>
/// Runs the agent command inside a throwaway Docker container. Only the ticket workspace (plus the command's own
/// <see cref="ProcessSpec.Mounts"/>) is mounted, the run's provider gets only its own credential, passed by env-var
/// name (values via the docker CLI's own environment, never on its command line), and the image's entrypoint restricts
/// outbound network to the provider's <see cref="AgentProviderInfo.BaseDomains">base domains</see> plus the project's
/// allowed domains.
/// </summary>
public sealed class DockerLauncher(IProcessRunner runner, string agentContextDirectory,
    Func<string, string?> getSecret) : IAgentLauncher
{
    public const string DefaultImage = "symphonist/agent:2";
    /// <summary>Repository of the images built from <c>agent.packages</c>; the tag is a hash of their Dockerfile.</summary>
    public const string PackagesRepository = "symphonist/agent-packages";
    public const string TokenSecretName = "claude-code-token";
    /// <summary>The variable Codex reads its OpenAI API key from in <c>exec</c> mode.</summary>
    public const string CodexApiKeyVar = "CODEX_API_KEY";
    /// <summary>The provider's base domains, which init-firewall.sh requires and always allows.</summary>
    public const string BaseDomainsVar = "SYMPHONIST_BASE_DOMAINS";

    private const string TokenVar = "CLAUDE_CODE_OAUTH_TOKEN";
    private const string DomainsVar = "SYMPHONIST_ALLOWED_DOMAINS";
    private const string WorkspacePath = "/workspace";
    private const string UidVar = "SYMPHONIST_UID";
    private const string GidVar = "SYMPHONIST_GID";
    private const string NameOption = "--name";
    private const string AgentLabel = "symphonist=agent";
    private const string ContextHashLabel = "symphonist.context-hash";
    private const string BaseIdLabel = "symphonist.base-id";
    private const string PipVenv = "/opt/symphonist-pip";
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(45);

    /// <summary>
    /// The only command variables forwarded into the container. An allowlist: forwarded names also reach the docker
    /// CLI's own environment and the entrypoint and firewall, which run as root (BASH_ENV, LD_PRELOAD, PATH, ...),
    /// and must never override the launcher's own credential or SYMPHONIST_* variables.
    /// </summary>
    private static readonly HashSet<string> ForwardableVars = new(StringComparer.Ordinal) { "CODEX_HOME" };

    /// <summary>Container paths a mount may not cover or sit under: the workspace (its mounts carry the read-only
    /// rules) and system directories, which hold the firewall, entrypoint and tools that run before the agent.</summary>
    private static readonly string[] ReservedMountRoots =
        ["workspace", "bin", "sbin", "lib", "lib32", "lib64", "libx32", "usr", "etc", "opt", "proc", "sys", "dev",
            "root", "boot", "var"];

    public bool ProvidesIsolation(AgentRunRequest request) => true;

    /// <summary>Whether the docker CLI is installed and its daemon answers.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var version = await runner.RunAsync(new ProcessSpec("docker", ["version", "--format", "{{.Server.Version}}"],
                Timeout: InspectTimeout), ct);
            return version.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false; // docker CLI not installed / not startable
        }
    }

    /// <summary>
    /// Makes sure <paramref name="image"/> exists locally. The default image is (re)built from the build context
    /// whenever it is missing or was built from a different context (its <c>symphonist.context-hash</c> label differs
    /// from <see cref="ContextHash"/>), so changes such as firewall fixes reach existing installs.
    /// </summary>
    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        ValidateImage(image);
        if (image != DefaultImage)
        {
            var exists = await runner.RunAsync(new ProcessSpec("docker", ["image", "inspect", image],
                Timeout: InspectTimeout), ct);
            if (exists.ExitCode == 0) return;
            throw new InvalidOperationException($"docker image '{image}' not found; build or pull it first");
        }

        var hash = ContextHash(agentContextDirectory);
        var inspect = await runner.RunAsync(new ProcessSpec("docker",
            ["image", "inspect", "--format", $"{{{{index .Config.Labels \"{ContextHashLabel}\"}}}}", image],
            Timeout: InspectTimeout), ct);
        if (inspect.ExitCode == 0 && inspect.StandardOutput.Trim() == hash) return;

        var build = await runner.RunAsync(new ProcessSpec("docker",
            ["build", "-t", image, "--label", $"{ContextHashLabel}={hash}", agentContextDirectory],
            Timeout: BuildTimeout), ct);
        if (build.ExitCode != 0)
            throw new InvalidOperationException(build.TimedOut
                ? $"docker build of '{image}' timed out"
                : $"docker build of '{image}' failed (exit {build.ExitCode}): {Tail(build.StandardError)}");
    }

    /// <summary>
    /// Makes sure the image <paramref name="settings"/> run in exists: the base image as in
    /// <see cref="EnsureImageAsync(string, CancellationToken)"/>, plus, when there are <c>agent.packages</c>, the
    /// <see cref="ImageFor">derived image</see> with them installed. That one is (re)built whenever it is missing or
    /// was built from a different base image (its <c>symphonist.base-id</c> label differs from the base's image id).
    /// </summary>
    public async Task EnsureImageAsync(AgentSettings settings, CancellationToken ct)
    {
        var baseImage = settings.Image ?? DefaultImage;
        await EnsureImageAsync(baseImage, ct);
        if (settings.Packages.IsEmpty) return;

        var image = ImageFor(settings);
        var baseId = await runner.RunAsync(new ProcessSpec("docker",
            ["image", "inspect", "--format", "{{.Id}}", baseImage], Timeout: InspectTimeout), ct);
        if (baseId.ExitCode != 0)
            throw new InvalidOperationException($"docker image '{baseImage}' not found; build or pull it first");
        var id = baseId.StandardOutput.Trim();

        var inspect = await runner.RunAsync(new ProcessSpec("docker",
            ["image", "inspect", "--format", $"{{{{index .Config.Labels \"{BaseIdLabel}\"}}}}", image],
            Timeout: InspectTimeout), ct);
        if (inspect.ExitCode == 0 && inspect.StandardOutput.Trim() == id) return;

        // No build context: the Dockerfile comes on stdin. Never pull: the base is the local image just checked.
        var build = await runner.RunAsync(new ProcessSpec("docker",
            ["build", "--pull=false", "-t", image, "--label", $"{BaseIdLabel}={id}", "-"],
            StandardInput: PackagesDockerfile(baseImage, settings.Packages), Timeout: BuildTimeout), ct);
        if (build.ExitCode != 0)
            throw new InvalidOperationException(build.TimedOut
                ? $"docker build of '{image}' (agent.packages on '{baseImage}') timed out"
                : $"docker build of '{image}' (agent.packages on '{baseImage}') failed (exit {build.ExitCode}): {Tail(build.StandardError)}");
    }

    /// <summary>
    /// The image an agent with <paramref name="settings"/> runs in: <c>agent.image</c> (or <see cref="DefaultImage"/>),
    /// or, when there are <c>agent.packages</c>, <see cref="PackagesRepository"/> tagged with a hash of the Dockerfile
    /// that adds them, so each distinct base and package set gets its own image.
    /// </summary>
    public static string ImageFor(AgentSettings settings)
    {
        var baseImage = settings.Image ?? DefaultImage;
        ValidateImage(baseImage);
        if (settings.Packages.IsEmpty) return baseImage;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(PackagesDockerfile(baseImage, settings.Packages)));
        return $"{PackagesRepository}:{Convert.ToHexStringLower(hash)[..16]}";
    }

    /// <summary>
    /// The Dockerfile that installs <paramref name="packages"/> on <paramref name="baseImage"/>: apt packages, pip
    /// packages into a virtualenv on PATH (with python3 and python3-venv from apt), and global npm packages. It runs as
    /// the base image's build user, which must be root (it is for the default image). Items are single-quoted; the
    /// project config loader only accepts items without quotes or shell characters.
    /// </summary>
    public static string PackagesDockerfile(string baseImage, AgentPackages packages)
    {
        var apt = packages.Pip.Count > 0 ? packages.Apt.Concat(["python3", "python3-venv"]).Distinct().ToList() : packages.Apt;
        var sb = new StringBuilder();
        sb.Append("# Generated by Symphonist from agent.packages.\n");
        sb.Append($"FROM {baseImage}\n");
        if (apt.Count > 0)
            sb.Append("RUN apt-get update \\\n")
              .Append($" && apt-get install -y --no-install-recommends {Quoted(apt)} \\\n")
              .Append(" && rm -rf /var/lib/apt/lists/*\n");
        if (packages.Pip.Count > 0)
            sb.Append($"RUN python3 -m venv {PipVenv} \\\n")
              .Append($" && {PipVenv}/bin/pip install --no-cache-dir {Quoted(packages.Pip)}\n")
              .Append($"ENV PATH={PipVenv}/bin:$PATH\n");
        if (packages.Npm.Count > 0)
            sb.Append($"RUN npm install -g {Quoted(packages.Npm)} \\\n")
              .Append(" && npm cache clean --force\n");
        return sb.ToString();
    }

    private static string Quoted(IEnumerable<string> items) => string.Join(' ', items.Select(i =>
        string.IsNullOrEmpty(i) || i.StartsWith('-') || i.Contains('\'') || i.Any(char.IsWhiteSpace)
            ? throw new ArgumentException($"invalid package '{i}'", nameof(items))
            : $"'{i}'"));

    /// <summary>
    /// Whether <paramref name="image"/> has <paramref name="command"/>: runs <c>&lt;command&gt; --version</c> in it
    /// (no firewall, entrypoint bypassed; the image must already exist locally).
    /// </summary>
    public async Task<bool> HasCommandAsync(string image, string command, CancellationToken ct)
    {
        ValidateImage(image);
        if (string.IsNullOrWhiteSpace(command) || command.StartsWith('-') || command.Any(char.IsWhiteSpace))
            throw new ArgumentException($"invalid command '{command}'", nameof(command));
        var r = await runner.RunAsync(new ProcessSpec("docker",
            // The entrypoint (and with it the firewall) is bypassed, so no network and no capabilities.
            ["run", "--rm", "--pull=never", "--network=none", "--cap-drop=ALL", "--entrypoint", command, image,
                "--version"], Timeout: InspectTimeout), ct);
        return r.ExitCode == 0;
    }

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        var settings = request.Settings;
        var image = ImageFor(settings);
        var baseDomains = AgentProviderInfo.BaseDomains(settings);
        var credentials = Credentials(settings);
        var commandEnv = agentCommand.Environment ?? new Dictionary<string, string?>();
        foreach (var name in commandEnv.Keys) ValidateCommandVariable(name);
        foreach (var mount in agentCommand.Mounts ?? []) ValidateMount(mount);

        var args = new List<string>
        {
            // Never pull: a missing image must fail rather than fetch whatever owns the name on a registry.
            "run", "--rm", "-i", "--pull=never",
            NameOption, $"symphonist-{Guid.NewGuid():N}", "--label", AgentLabel,
            "--cap-add=NET_ADMIN", "--cap-add=NET_RAW",
        };
        if (request.ReadOnlyWorkspace)
            args.AddRange(["-v", $"{request.WorkingDirectory}:/workspace:ro"]); // covers .git too
        else
            args.AddRange([
                "-v", $"{request.WorkingDirectory}:/workspace",
                // Read-only .git: the host later runs git in this workspace, so the agent must not be able to plant
                // hooks or config that would execute there.
                "-v", $"{Path.Combine(request.WorkingDirectory, ".git")}:/workspace/.git:ro",
            ]);
        foreach (var mount in agentCommand.Mounts ?? [])
            args.AddRange(["-v", $"{mount.HostPath}:{mount.ContainerPath}{(mount.ReadOnly ? ":ro" : "")}"]);
        args.AddRange(["-w", WorkspacePath]);
        // A null value removes the variable from the docker CLI's environment, so `-e NAME` passes nothing.
        var env = new Dictionary<string, string?>();
        foreach (var (name, value) in commandEnv.Concat(credentials))
        {
            args.AddRange(["-e", name]);
            env[name] = value;
        }
        args.AddRange(["-e", BaseDomainsVar, "-e", DomainsVar]);
        env[BaseDomainsVar] = string.Join(",", baseDomains);
        env[DomainsVar] = string.Join(",", settings.AllowedDomains.Distinct());
        if (!OperatingSystem.IsWindows())
        {
            args.AddRange(["-e", UidVar, "-e", GidVar]);
            env[UidVar] = getuid().ToString(CultureInfo.InvariantCulture);
            env[GidVar] = getgid().ToString(CultureInfo.InvariantCulture);
        }
        args.Add(image);
        args.Add(agentCommand.FileName);
        args.AddRange(agentCommand.Arguments);

        return new ProcessSpec("docker", args, StandardInput: agentCommand.StandardInput, Environment: env,
            Timeout: agentCommand.Timeout);
    }

    /// <summary>The credential variables of the run's provider, and only those.</summary>
    private Dictionary<string, string?> Credentials(AgentSettings settings)
    {
        switch (settings.Type)
        {
            case AgentTypes.ClaudeCode:
                // Missing token: pass nothing and let Claude Code report it, as before providers existed.
                return new() { [TokenVar] = getSecret(TokenSecretName) };
            case AgentTypes.Codex when settings.Codex.Auth == CodexAuth.ApiKey:
                var secret = settings.Codex.ApiKeySecret;
                return getSecret(secret) is { Length: > 0 } key
                    ? new() { [CodexApiKeyVar] = key }
                    : throw new InvalidOperationException(
                        $"secret '{secret}' is not set; store the OpenAI API key under that name to run Codex");
            case AgentTypes.Codex:
                return []; // ChatGPT sign-in: auth.json arrives through the runner's mounts.
            default:
                throw new ArgumentException($"unknown agent type '{settings.Type}'", nameof(settings));
        }
    }

    private static void ValidateCommandVariable(string name)
    {
        if (!ForwardableVars.Contains(name))
            throw new InvalidOperationException($"agent command may not set environment variable '{name}'");
    }

    private static void ValidateMount(BindMount mount)
    {
        var path = mount.ContainerPath;
        // Rooted (docker resolves nothing relative, and a bare name would make it create a named volume instead), in
        // any OS's form, and with no ':' other than a drive letter's, which would shift the fields of the -v value.
        var host = mount.HostPath ?? "";
        var drive = host.Length > 2 && char.IsAsciiLetter(host[0]) && host[1] == ':' && host[2] is '/' or '\\';
        var rooted = drive || host.StartsWith('/') || host.StartsWith(@"\\", StringComparison.Ordinal);
        if (!rooted || host.IndexOf(':', drive ? 2 : 0) >= 0)
            throw new InvalidOperationException($"mount at '{path}' needs a host directory path, not '{mount.HostPath}'");
        // Absolute, already normal (no empty, '.' or '..' segments) and free of ':' and ',' so docker reads exactly
        // this path, which must stay clear of the workspace and system directories.
        var segments = path.StartsWith('/') ? path[1..].Split('/') : [];
        if (segments.Length == 0
            || segments.Any(s => s is "" or "." or ".." || s.Contains(':') || s.Contains(','))
            || ReservedMountRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"mount container path '{path}' must be absolute, normalized and outside {WorkspacePath} and system directories");
    }

    /// <summary>
    /// SHA-256 (lower-case hex) over every file of the build context: each file's relative path ('/'-separated) and
    /// length and bytes, in ordinal path order. Independent of where the context lives and of file timestamps.
    /// </summary>
    public static string ContextHash(string contextDirectory)
    {
        var root = Path.GetFullPath(contextDirectory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        foreach (var (full, relative) in files)
        {
            var bytes = File.ReadAllBytes(full);
            sha.AppendData(Encoding.UTF8.GetBytes(relative));
            sha.AppendData([0]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
            sha.AppendData(length);
            sha.AppendData(bytes);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// Force-removes every container labeled <c>symphonist=agent</c> (runs orphaned by a crash or restart). Call only
    /// while no agent runs are in flight. Returns how many were removed; throws if they cannot be listed.
    /// </summary>
    public async Task<int> RemoveLeftoverContainersAsync(CancellationToken ct)
    {
        var list = await runner.RunAsync(new ProcessSpec("docker", ["ps", "-aq", "--filter", $"label={AgentLabel}"],
            Timeout: InspectTimeout), ct);
        if (list.ExitCode != 0)
            throw new InvalidOperationException($"listing agent containers failed (exit {list.ExitCode}): {Tail(list.StandardError)}");

        var removed = 0;
        foreach (var id in list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rm = await runner.RunAsync(new ProcessSpec("docker", ["rm", "-f", id], Timeout: RemoveTimeout), ct);
            if (rm.ExitCode == 0) removed++;
        }
        return removed;
    }

    /// <summary>Force-removes the container of a timed-out or cancelled run (killing the docker CLI does not stop it).</summary>
    public async Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct)
    {
        if (ContainerName(wrapped) is not { } name) return;
        await runner.RunAsync(new ProcessSpec("docker", ["rm", "-f", name], Timeout: RemoveTimeout), ct);
    }

    /// <summary>The container name a <see cref="Wrap"/>ped spec runs under, or null if it is not one.</summary>
    public static string? ContainerName(ProcessSpec wrapped)
    {
        if (wrapped.FileName != "docker" || wrapped.Arguments.Count == 0 || wrapped.Arguments[0] != "run") return null;
        var i = wrapped.Arguments.ToList().IndexOf(NameOption);
        return i >= 0 && i + 1 < wrapped.Arguments.Count ? wrapped.Arguments[i + 1] : null;
    }

    private static void ValidateImage(string image)
    {
        // No whitespace: the name also goes into a generated Dockerfile's FROM line.
        if (string.IsNullOrWhiteSpace(image) || image.StartsWith('-') || image.Any(char.IsWhiteSpace))
            throw new ArgumentException($"invalid docker image name '{image}'", nameof(image));
    }

    private static string Tail(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getuid();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getgid();
}
