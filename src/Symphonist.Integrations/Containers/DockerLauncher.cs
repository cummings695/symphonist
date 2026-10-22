using System.Globalization;
using System.Runtime.InteropServices;
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
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(45);

    /// <summary>Domains Claude Code itself needs; always allowed (init-firewall.sh mirrors this list).</summary>
    public static readonly IReadOnlyList<string> BaseAllowedDomains =
        ["api.anthropic.com", "statsig.anthropic.com", "sentry.io"];

    public bool ProvidesIsolation => true;

    /// <summary>Makes sure <paramref name="image"/> exists locally, building the default image if it is missing.</summary>
    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        ValidateImage(image);
        var inspect = await runner.RunAsync(new ProcessSpec("docker", ["image", "inspect", image],
            Timeout: InspectTimeout), ct);
        if (inspect.ExitCode == 0) return;
        if (image != DefaultImage)
            throw new InvalidOperationException($"docker image '{image}' not found; build or pull it first");

        var build = await runner.RunAsync(new ProcessSpec("docker", ["build", "-t", image, agentContextDirectory],
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
            NameOption, $"symphonist-{Guid.NewGuid():N}", "--label", "symphonist=agent",
            "--cap-add=NET_ADMIN", "--cap-add=NET_RAW",
            "-v", $"{request.WorkingDirectory}:/workspace",
            // Read-only .git: the host later runs git in this workspace, so the agent must not be able to plant
            // hooks or config that would execute there.
            "-v", $"{Path.Combine(request.WorkingDirectory, ".git")}:/workspace/.git:ro",
            "-w", "/workspace",
            "-e", TokenVar, "-e", DomainsVar,
        };
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

    /// <summary>Force-removes the container of a timed-out or cancelled run (killing the docker CLI does not stop it).</summary>
    public async Task CleanupAsync(ProcessSpec wrapped, CancellationToken ct)
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
