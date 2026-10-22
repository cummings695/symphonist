using Symphonist.Core.Agents;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>Decides where an agent command actually runs (host process, container, ...).</summary>
public interface IAgentLauncher
{
    ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request);
}

/// <summary>Runs the agent command directly on the host, in the request's working directory.</summary>
public sealed class HostLauncher : IAgentLauncher
{
    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request) =>
        agentCommand with { WorkingDirectory = request.WorkingDirectory };
}
