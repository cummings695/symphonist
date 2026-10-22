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

public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, AgentSettings Settings);

public sealed record AgentRunResult(bool Succeeded, StageOutput? Output, decimal? CostUsd, TimeSpan Duration,
    string? Error, string RawLog);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}
