using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>Decides where an agent command actually runs (host process, container, ...).</summary>
public interface IAgentLauncher
{
    /// <summary>
    /// True only when the wrapped command runs inside a sandbox (container + network allowlist). Permission prompts
    /// may be bypassed only when this is true.
    /// </summary>
    bool ProvidesIsolation { get; }

    ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request);
}

/// <summary>
/// Runs the agent command directly on the host, in the request's working directory. Fails closed: refuses requests
/// that expect container isolation and commands that bypass permission prompts.
/// </summary>
public sealed class HostLauncher : IAgentLauncher
{
    public const string PermissionBypassFlag = "--dangerously-skip-permissions";

    public bool ProvidesIsolation => false;

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        if (request.Settings.Isolation == Isolation.Container)
            throw new InvalidOperationException("container isolation requested but HostLauncher cannot provide it");
        if (agentCommand.Arguments.Contains(PermissionBypassFlag))
            throw new InvalidOperationException($"HostLauncher refuses to run a command with {PermissionBypassFlag}");
        return agentCommand with { WorkingDirectory = request.WorkingDirectory };
    }
}
