using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Symphonist.Core.Agents;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Containers;

/// <summary>
/// Runs the agent command inside a throwaway Docker container. Only the ticket workspace is mounted, secrets are
/// passed by env-var name (values via the docker CLI's own environment, never on its command line), and the image's
/// entrypoint restricts outbound network to <see cref="BaseAllowedDomains"/> plus the project's allowed domains.
/// </summary>
public sealed class DockerLauncher(IProcessRunner runner, string agentContextDirectory,
    Func<string, string?> getSecret) : IAgentLauncher
{
    public const string DefaultImage = "symphonist/agent-claude:1";
    public const string TokenSecretName = "claude-code-token";

    private const string TokenVar = "CLAUDE_CODE_OAUTH_TOKEN";
    private const string DomainsVar = "SYMPHONIST_ALLOWED_DOMAINS";
    private const string UidVar = "SYMPHONIST_UID";
    private const string GidVar = "SYMPHONIST_GID";
    private const string NameOption = "--name";
    private const string AgentLabel = "symphonist=agent";
    private const string ContextHashLabel = "symphonist.context-hash";
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(45);

    /// <summary>Domains Claude Code itself needs; always allowed (init-firewall.sh mirrors this list).</summary>
    public static readonly IReadOnlyList<string> BaseAllowedDomains =
        ["api.anthropic.com", "statsig.anthropic.com", "sentry.io"];

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

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        var settings = request.Settings;
        var image = settings.Image ?? DefaultImage;
        ValidateImage(image);
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
        args.AddRange(["-w", "/workspace", "-e", TokenVar, "-e", DomainsVar]);
        var env = new Dictionary<string, string?>
        {
            // A null value removes the variable from the docker CLI's environment, so `-e NAME` passes nothing.
            [TokenVar] = getSecret(TokenSecretName),
            [DomainsVar] = string.Join(",", BaseAllowedDomains.Concat(settings.AllowedDomains).Distinct()),
        };
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
        if (string.IsNullOrWhiteSpace(image) || image.StartsWith('-'))
            throw new ArgumentException($"invalid docker image name '{image}'", nameof(image));
    }

    private static string Tail(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getuid();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getgid();
}
