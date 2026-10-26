using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>Decides where an agent command actually runs (host process, container, ...).</summary>
public interface IAgentLauncher
{
    /// <summary>
    /// True only when <see cref="Wrap"/> would run <paramref name="request"/>'s command inside a sandbox (container +
    /// network allowlist). Permission prompts may be bypassed only when this is true. Asked per request because a
    /// launcher may delegate different requests to different launchers.
    /// </summary>
    bool ProvidesIsolation(AgentRunRequest request);

    ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request);

    /// <summary>
    /// Releases whatever a <paramref name="wrapped"/> run of <paramref name="request"/> may have left behind after it
    /// timed out or was cancelled (killing the launching process alone may not stop it, e.g. a container).
    /// </summary>
    Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct);
}

/// <summary>
/// Runs the agent command directly on the host, in the request's working directory. Fails closed: refuses requests
/// that expect container isolation, commands that need bind mounts, and commands that bypass permission prompts
/// (Claude Code) or approvals and the sandbox (Codex).
/// </summary>
public sealed class HostLauncher : IAgentLauncher
{
    public const string PermissionBypassFlag = "--dangerously-skip-permissions";
    public const string CodexBypassFlag = "--dangerously-bypass-approvals-and-sandbox";

    private const string PermissionModeOption = "--permission-mode";
    private const string BypassPermissionsMode = "bypassPermissions";
    private const string CodexBypassAlias = "--yolo";
    // Codex's unsandboxed mode. Matched anywhere in an argument: it can come as --sandbox/-s in any spelling or as a
    // -c/--config override (sandbox_mode=...).
    private const string CodexFullAccess = "danger-full-access";

    public bool ProvidesIsolation(AgentRunRequest request) => false;

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        if (request.Settings.Isolation == Isolation.Container)
            throw new InvalidOperationException("container isolation requested but HostLauncher cannot provide it");
        if (agentCommand.Mounts is { Count: > 0 })
            throw new InvalidOperationException("HostLauncher cannot provide bind mounts; they need a container");
        if (BypassesPermissions(agentCommand.Arguments))
            throw new InvalidOperationException("HostLauncher refuses to run a command that bypasses permission prompts");
        if (BypassesCodexSandbox(agentCommand.Arguments))
            throw new InvalidOperationException("HostLauncher refuses to run a command that bypasses the Codex sandbox");
        return agentCommand with { WorkingDirectory = request.WorkingDirectory };
    }

    private static bool BypassesCodexSandbox(IReadOnlyList<string> args) => args.Any(a =>
        a == CodexBypassFlag || a.StartsWith(CodexBypassFlag + "=", StringComparison.Ordinal)
        || a == CodexBypassAlias || a.StartsWith(CodexBypassAlias + "=", StringComparison.Ordinal)
        || a.Contains(CodexFullAccess, StringComparison.OrdinalIgnoreCase));

    public Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct) => Task.CompletedTask;

    private static bool BypassesPermissions(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == PermissionBypassFlag || a.StartsWith(PermissionBypassFlag + "=", StringComparison.Ordinal))
                return true;
            if (a == PermissionModeOption && i + 1 < args.Count
                && args[i + 1].Equals(BypassPermissionsMode, StringComparison.OrdinalIgnoreCase))
                return true;
            if (a.Equals($"{PermissionModeOption}={BypassPermissionsMode}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
