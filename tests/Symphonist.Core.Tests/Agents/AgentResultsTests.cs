using System.Text.Json;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;

namespace Symphonist.Core.Tests.Agents;

public class AgentResultsTests
{
    private static AgentRunRequest Request(string schema = StageOutputSchema.Json) =>
        new("wd", "prompt", new AgentSettings("claude-code", Isolation.Host, [], [], 10, null, null), schema);

    private static JsonElement Json(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }

    [Fact]
    public void FromStructured_stage_completed_succeeds()
    {
        var r = AgentResults.FromStructured(Request(), Json("""{"status":"completed","summary":"done"}"""), 1.5m,
            TimeSpan.FromSeconds(2), "log");
        Assert.True(r.Succeeded);
        Assert.Equal("done", r.Output!.Summary);
        Assert.Equal(1.5m, r.CostUsd);
        Assert.Equal("log", r.RawLog);
        Assert.NotNull(r.Structured);
    }

    [Fact]
    public void FromStructured_failed_status_fails_with_summary()
    {
        var r = AgentResults.FromStructured(Request(), Json("""{"status":"failed","summary":"broke"}"""), null,
            TimeSpan.Zero, "log");
        Assert.False(r.Succeeded);
        Assert.Equal("broke", r.Error);
        Assert.Equal("failed", r.Output!.Status);
    }

    [Fact]
    public void FromStructured_missing_fails()
    {
        var r = AgentResults.FromStructured(Request(), null, 2m, TimeSpan.Zero, "log");
        Assert.False(r.Succeeded);
        Assert.Equal("missing structured output", r.Error);
        Assert.Equal(2m, r.CostUsd);
    }

    [Fact]
    public void FromStructured_non_stage_returns_structured()
    {
        var r = AgentResults.FromStructured(Request(PrFixOutputSchema.Json), Json("""{"a":1}"""), null,
            TimeSpan.Zero, "log");
        Assert.True(r.Succeeded);
        Assert.Null(r.Output);
        Assert.Equal(1, r.Structured!.Value.GetProperty("a").GetInt32());
    }
}
