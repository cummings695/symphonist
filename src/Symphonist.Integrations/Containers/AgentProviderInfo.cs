using Symphonist.Core.Config;

namespace Symphonist.Integrations.Containers;

/// <summary>What the agent container needs per provider, beyond the project's own settings.</summary>
public static class AgentProviderInfo
{
    private static readonly IReadOnlyList<string> ClaudeCodeDomains =
        ["api.anthropic.com", "statsig.anthropic.com", "sentry.io"];

    private static readonly IReadOnlyList<string> CodexApiKeyDomains = ["api.openai.com"];

    // The API, plus what ChatGPT sign-in and token refresh reach.
    private static readonly IReadOnlyList<string> CodexChatGptDomains = ["api.openai.com", "auth.openai.com", "chatgpt.com"];

    /// <summary>The provider's CLI executable name.</summary>
    public static string Command(string agentType) => agentType switch
    {
        AgentTypes.ClaudeCode => "claude",
        AgentTypes.Codex => "codex",
        _ => throw new ArgumentException($"unknown agent type '{agentType}'", nameof(agentType)),
    };

    /// <summary>
    /// The domains the provider's CLI itself needs with the run's sign-in: a Codex API key reaches only the API, while
    /// ChatGPT sign-in also needs its login and token-refresh domains. The container firewall always allows them and
    /// refuses to start when one cannot be resolved.
    /// </summary>
    public static IReadOnlyList<string> BaseDomains(AgentSettings settings) => settings.Type switch
    {
        AgentTypes.ClaudeCode => ClaudeCodeDomains,
        AgentTypes.Codex => settings.Codex.Auth == CodexAuth.ChatGpt ? CodexChatGptDomains : CodexApiKeyDomains,
        _ => throw new ArgumentException($"unknown agent type '{settings.Type}'", nameof(settings)),
    };
}
