using System.Text.Json;
using Symphonist.Core.Agents;
using Symphonist.Core.Risk;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeAgentRunner : IAgentRunner
{
    public List<AgentRunRequest> Requests { get; } = [];
    public IReadOnlyList<string> Prompts => Requests.Select(r => r.Prompt).ToList();

    /// <summary>Queued results, consumed in order; when empty, <see cref="OnRun"/> is used.</summary>
    public Queue<AgentRunResult> Results { get; } = new();

    /// <summary>Fallback result; default succeeds with summary "done".</summary>
    public Func<AgentRunRequest, AgentRunResult> OnRun { get; set; } = r => r.OutputSchema == PrFixOutputSchema.Json ? PrFix() : Ok(Output());

    /// <summary>Asynchronous fallback (e.g. to block a run on a TaskCompletionSource); takes precedence over <see cref="OnRun"/>.</summary>
    public Func<AgentRunRequest, Task<AgentRunResult>>? OnRunAsync { get; set; }

    /// <summary>Like <see cref="OnRunAsync"/>, but sees the run's cancellation token; takes precedence over both.</summary>
    public Func<AgentRunRequest, CancellationToken, Task<AgentRunResult>>? OnRunCancellableAsync { get; set; }

    public static StageOutput Output(string summary = "done", string? comment = null, string? specPath = null,
        string? planPath = null, string? prTitle = null, string? prDescription = null) =>
        new("completed", summary, comment, specPath, planPath, prTitle, prDescription, null);

    public static AgentRunResult Ok(StageOutput output) =>
        new(true, output, 0.25m, TimeSpan.FromSeconds(3), null, """{"raw":true}""");

    /// <summary>A successful PR comments fix run with no replies (each comment then gets the default reply).</summary>
    public static AgentRunResult PrFix() =>
        new(true, null, 0.25m, TimeSpan.FromSeconds(3), null, """{"raw":true}""",
            JsonSerializer.SerializeToElement(new { status = "completed", summary = "done", replies = Array.Empty<object>() }));

    public static AgentRunResult Fail(string error) =>
        new(false, null, null, TimeSpan.FromSeconds(1), error, """{"raw":false}""");

    /// <summary>A run the provider's usage limit stopped; <paramref name="resetsAt"/> null when it named no reset.</summary>
    public static AgentRunResult Limited(DateTimeOffset? resetsAt, string error = "Claude usage limit reached") =>
        new(false, null, null, TimeSpan.FromSeconds(1), error, """{"raw":"limited"}""", Limit: new UsageLimit(resetsAt));

    /// <summary>A successful review run whose structured output is a gate verdict.</summary>
    public static AgentRunResult Review(string risk, string summary = "reviewed", string[]? reasons = null,
        string[]? checkedItems = null, string[]? areas = null)
    {
        var verdict = new Dictionary<string, object>
        {
            ["risk"] = risk, ["summary"] = summary, ["reasons"] = reasons ?? [], ["checked"] = checkedItems ?? ["diff"],
        };
        if (areas is not null) verdict["risk_areas_touched"] = areas;
        return new(true, null, 0.1m, TimeSpan.FromSeconds(2), null, """{"raw":"review"}""",
            JsonSerializer.SerializeToElement(verdict));
    }

    public static bool IsReview(AgentRunRequest r) => r.OutputSchema == GateReviewSchema.Json;

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (Results.Count > 0) return Task.FromResult(Results.Dequeue());
        if (OnRunCancellableAsync is not null) return OnRunCancellableAsync(request, ct);
        return OnRunAsync?.Invoke(request) ?? Task.FromResult(OnRun(request));
    }
}
