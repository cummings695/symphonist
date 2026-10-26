using Symphonist.Core.Config;

namespace Symphonist.Integrations.Containers;

/// <summary>What the agent container needs per provider, beyond the project's own settings.</summary>
public static class AgentProviderInfo
{
    private static readonly IReadOnlyList<string> ClaudeCodeDomains =
        ["api.anthropic.com", "statsig.anthropic.com", "sentry.io"];

    // The API, plus what ChatGPT sign-in and token refresh reach.
    private static readonly IReadOnlyList<string> CodexDomains = ["api.openai.com", "auth.openai.com", "chatgpt.com"];

    /// <summary>The provider's CLI executable name.</summary>
    public static string Command(string agentType) => agentType switch
    {
        AgentTypes.ClaudeCode => "claude",
        AgentTypes.Codex => "codex",
        _ => throw new ArgumentException($"unknown agent type '{agentType}'", nameof(agentType)),
    };

    /// <summary>
    /// The domains the provider's CLI itself needs. The container firewall always allows them and refuses to start
    /// when one cannot be resolved.
    /// </summary>
    public static IReadOnlyList<string> BaseDomains(string agentType) => agentType switch
    {
        AgentTypes.ClaudeCode => ClaudeCodeDomains,
        AgentTypes.Codex => CodexDomains,
        _ => throw new ArgumentException($"unknown agent type '{agentType}'", nameof(agentType)),
    };
}
