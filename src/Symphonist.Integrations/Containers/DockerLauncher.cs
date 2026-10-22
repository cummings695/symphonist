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
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(45);

    /// <summary>Domains Claude Code itself needs; always allowed (init-firewall.sh mirrors this list).</summary>
    public static readonly IReadOnlyList<string> BaseAllowedDomains =
        ["api.anthropic.com", "statsig.anthropic.com", "sentry.io"];

    public bool ProvidesIsolation => true;

    /// <summary>Makes sure <paramref name="image"/> exists locally, building the default image if it is missing.</summary>
    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
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
        var args = new List<string>
        {
            "run", "--rm", "-i", "--cap-add=NET_ADMIN", "--cap-add=NET_RAW",
            "-v", $"{request.WorkingDirectory}:/workspace", "-w", "/workspace",
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
        args.Add(settings.Image ?? DefaultImage);
        args.Add(agentCommand.FileName);
        args.AddRange(agentCommand.Arguments);

        return new ProcessSpec("docker", args, StandardInput: agentCommand.StandardInput, Environment: env,
            Timeout: agentCommand.Timeout);
    }

    private static string Tail(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getuid();

    [DllImport("libc", ExactSpelling = true)]
    private static extern uint getgid();
}
