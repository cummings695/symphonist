using System.Text.Json;

namespace Symphonist.Core.Agents;

/// <summary>Turns an agent's schema-validated structured output into an <see cref="AgentRunResult"/>, for every provider.</summary>
public static class AgentResults
{
    public static AgentRunResult FromStructured(AgentRunRequest request, JsonElement? structured, decimal? costUsd,
        TimeSpan duration, string rawLog)
    {
        AgentRunResult Fail(string error, StageOutput? output = null) =>
            new(false, output, costUsd, duration, error, rawLog, structured);

        if (request.OutputSchema != StageOutputSchema.Json)
            return structured is null
                ? Fail("missing structured output")
                : new AgentRunResult(true, null, costUsd, duration, null, rawLog, structured);

        var output = StageOutputSchema.Read(structured);
        if (output is null) return Fail("missing structured output");
        if (output.Status != "completed")
            return Fail(string.IsNullOrWhiteSpace(output.Summary) ? $"stage status '{output.Status}'" : output.Summary, output);
        return new AgentRunResult(true, output, costUsd, duration, null, rawLog, structured);
    }
}
