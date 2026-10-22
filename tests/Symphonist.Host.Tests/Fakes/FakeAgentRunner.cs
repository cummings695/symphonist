using Symphonist.Core.Agents;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeAgentRunner : IAgentRunner
{
    public List<AgentRunRequest> Requests { get; } = [];
    public IReadOnlyList<string> Prompts => Requests.Select(r => r.Prompt).ToList();

    /// <summary>Queued results, consumed in order; when empty, <see cref="OnRun"/> is used.</summary>
    public Queue<AgentRunResult> Results { get; } = new();

    /// <summary>Fallback result; default succeeds with summary "done".</summary>
    public Func<AgentRunRequest, AgentRunResult> OnRun { get; set; } = _ => Ok(Output());

    public static StageOutput Output(string summary = "done", string? comment = null, string? specPath = null,
        string? planPath = null, string? prTitle = null, string? prDescription = null) =>
        new("completed", summary, comment, specPath, planPath, prTitle, prDescription, null);

    public static AgentRunResult Ok(StageOutput output) =>
        new(true, output, 0.25m, TimeSpan.FromSeconds(3), null, """{"raw":true}""");

    public static AgentRunResult Fail(string error) =>
        new(false, null, null, TimeSpan.FromSeconds(1), error, """{"raw":false}""");

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : OnRun(request));
    }
}
