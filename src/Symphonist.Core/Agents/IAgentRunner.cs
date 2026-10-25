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

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The stage output in <paramref name="structured"/>, or null when missing or invalid (status and summary are required).</summary>
    public static StageOutput? Read(JsonElement? structured)
    {
        try
        {
            if (structured is { } so)
                return so.Deserialize<StageOutput>(Options) is { Status: not null, Summary: not null } o ? o : null;
        }
        catch (JsonException) { }
        return null;
    }
}

/// <summary>The agent's reply to one PR comment.</summary>
public sealed record PrReply(string CommentId, string Reply);

/// <summary>Output schema of a run that addresses PR comments: the stage output plus one reply per comment.</summary>
public static class PrFixOutputSchema
{
    public const string Json = """
        {"type":"object","properties":{"status":{"type":"string","enum":["completed","failed"]},"summary":{"type":"string"},"comment_markdown":{"type":"string"},"spec_path":{"type":"string"},"plan_path":{"type":"string"},"pr_title":{"type":"string"},"pr_description":{"type":"string"},"tests_passed":{"type":"boolean"},"replies":{"type":"array","items":{"type":"object","properties":{"comment_id":{"type":"string"},"reply":{"type":"string"}},"required":["comment_id","reply"]}}},"required":["status","summary","replies"]}
        """;

    private const int MaxReplies = 50;

    /// <summary>The replies in <paramref name="structured"/>; malformed entries are dropped and each field is bounded.</summary>
    public static IReadOnlyList<PrReply> ReadReplies(JsonElement? structured)
    {
        var replies = new List<PrReply>();
        if (structured is not { ValueKind: JsonValueKind.Object } so
            || !so.TryGetProperty("replies", out var list) || list.ValueKind != JsonValueKind.Array)
            return replies;
        foreach (var item in list.EnumerateArray())
        {
            if (replies.Count >= MaxReplies) break;
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("comment_id", out var id) || id.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String)
                continue;
            var text = Risk.ReviewerText.Line(reply.GetString(), Risk.ReviewerText.MaxSummaryChars * 4);
            var key = Risk.ReviewerText.Line(id.GetString(), Risk.ReviewerText.MaxItemChars);
            if (key.Length > 0 && text.Length > 0) replies.Add(new PrReply(key, text));
        }
        return replies;
    }
}

/// <param name="OutputSchema">JSON schema the agent's structured output must match.</param>
/// <param name="ReadOnlyWorkspace">The agent must not be able to modify the workspace (e.g. a reviewer).</param>
public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, AgentSettings Settings,
    string OutputSchema = StageOutputSchema.Json, bool ReadOnlyWorkspace = false);

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
