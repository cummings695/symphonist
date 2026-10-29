using System.Text.Json;
using Symphonist.Core.Config;

namespace Symphonist.Core.Agents;

/// <summary>Structured result an agent stage reports. Status is "completed" or "failed".</summary>
public sealed record StageOutput(string Status, string Summary, string? CommentMarkdown, string? SpecPath,
    string? PlanPath, string? PrTitle, string? PrDescription, bool? TestsPassed);

public static class StageOutputSchema
{
    public const string Json = """
        {"type":"object","properties":{"status":{"type":"string","enum":["completed","failed"]},"summary":{"type":"string"},"comment_markdown":{"type":"string"},"spec_path":{"type":"string"},"plan_path":{"type":"string"},"pr_title":{"type":"string"},"pr_description":{"type":"string"},"tests_passed":{"type":"boolean"}},"required":["status","summary"]}
        """;
}

/// <param name="OutputSchema">JSON schema the agent's structured output must match.</param>
/// <param name="ReadOnlyWorkspace">The agent must not be able to modify the workspace (e.g. a reviewer).</param>
/// <param name="AttachmentsDirectory">Host directory of the ticket's downloaded attachments, readable by the agent.</param>
public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, AgentSettings Settings,
    string OutputSchema = StageOutputSchema.Json, bool ReadOnlyWorkspace = false, string? AttachmentsDirectory = null);

/// <param name="Output">The parsed stage output; set only for runs with <see cref="StageOutputSchema"/>.</param>
/// <param name="Structured">The agent's schema-validated structured output, whatever the schema.</param>
public sealed record AgentRunResult(bool Succeeded, StageOutput? Output, decimal? CostUsd, TimeSpan Duration,
    string? Error, string RawLog, JsonElement? Structured = null);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}

/// <summary>Resolves the runner for a configured agent type (e.g. "claude-code").</summary>
public interface IAgentRunners
{
    IAgentRunner For(string agentType);
}

public sealed class AgentRunners(IReadOnlyDictionary<string, IAgentRunner> runners) : IAgentRunners
{
    public IAgentRunner For(string agentType) =>
        runners.TryGetValue(agentType, out var runner)
            ? runner
            : throw new NotSupportedException($"no agent runner for '{agentType}'");
}
