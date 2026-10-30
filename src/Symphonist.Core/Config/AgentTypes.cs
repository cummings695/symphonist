namespace Symphonist.Core.Config;

/// <summary>The agent providers a project may name in <c>agent.type</c> and its overrides.</summary>
public static class AgentTypes
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";

    public static readonly IReadOnlyList<string> All = [ClaudeCode, Codex];
}

/// <summary>How a Codex run signs in. YAML: api-key | chatgpt.</summary>
public enum CodexAuth { ApiKey, ChatGpt }

/// <summary>The project's one Codex sign-in (<c>agent.codex</c>), shared by every job that runs Codex.</summary>
/// <param name="ApiKeySecret">Name of the OS credential store secret holding the OpenAI key; used only with <see cref="CodexAuth.ApiKey"/>.</param>
public sealed record CodexSettings(CodexAuth Auth, string ApiKeySecret)
{
    public const string DefaultApiKeySecret = "openai-api-key";

    public static readonly CodexSettings Default = new(CodexAuth.ApiKey, DefaultApiKeySecret);
}

/// <summary>
/// The provider rules and their error messages, shared by the config loader and the project form so both say the same.
/// </summary>
public static class AgentRules
{
    public const string StaleCodexSettings = "agent.codex is set but no job uses codex";
    public const string ApiKeySecretNeedsApiKey = "agent.codex.apiKeySecret applies only to auth: api-key";
    public const string CodexNeedsContainer = "codex runs only in containers (isolation: host)";

    public static bool IsSupported(string type) => AgentTypes.All.Contains(type);

    /// <param name="field">The field as the user wrote it, such as <c>agent.type</c> or <c>review.agent.type</c>.</param>
    public static string UnsupportedType(string field, string type) =>
        $"unsupported {field} '{type}' (allowed: {string.Join(", ", AgentTypes.All)})";

    /// <summary>
    /// Codex runs only in containers for now. The one place that rule lives: lifting it later means changing this.
    /// </summary>
    public static bool AllowedIn(string type, Isolation isolation) =>
        isolation == Isolation.Container || type != AgentTypes.Codex;
}
