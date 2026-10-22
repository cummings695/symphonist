using System.Diagnostics;
using System.Text.Json;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>Runs Claude Code headlessly (<c>claude -p</c>) and parses its JSON result.</summary>
public sealed class ClaudeCodeRunner(IProcessRunner runner, IAgentLauncher launcher) : IAgentRunner
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var settings = request.Settings;
        var args = new List<string> { "-p", "--output-format", "json", "--json-schema", StageOutputSchema.Json };
        if (settings.Isolation == Isolation.Container) args.Add("--dangerously-skip-permissions");
        else { args.Add("--allowedTools"); args.Add(string.Join(",", settings.AllowedTools)); }
        if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("--model"); args.Add(settings.Model); }

        var command = new ProcessSpec("claude", args, request.WorkingDirectory, request.Prompt,
            Timeout: TimeSpan.FromMinutes(settings.TimeoutMinutes));
        var spec = launcher.Wrap(command, request);

        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(spec, ct);
        sw.Stop();

        var rawLog = result.StandardOutput + "\n--- stderr ---\n" + result.StandardError;
        return Parse(result, settings, sw.Elapsed, rawLog);
    }

    private static AgentRunResult Parse(ProcessResult result, AgentSettings settings, TimeSpan duration, string rawLog)
    {
        AgentRunResult Fail(string error, StageOutput? output = null, decimal? cost = null) =>
            new(false, output, cost, duration, error, rawLog);

        if (result.TimedOut) return Fail($"timed out after {settings.TimeoutMinutes} minutes");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Fail(FirstNonEmpty(Tail(result.StandardError), "agent output was not valid JSON")!);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return Fail(FirstNonEmpty(Tail(result.StandardError), "agent output was not a JSON object")!);

        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal() : null;
        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        var resultText = root.TryGetProperty("result", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        var output = ReadOutput(root, resultText);

        if (result.ExitCode != 0 || isError)
            return Fail(FirstNonEmpty(Tail(result.StandardError), resultText,
                result.ExitCode != 0 ? $"agent exited with code {result.ExitCode}" : "agent reported an error")!,
                output, cost);
        if (output is null) return Fail("missing structured output", null, cost);
        if (output.Status != "completed")
            return Fail(FirstNonEmpty(output.Summary, $"stage status '{output.Status}'")!, output, cost);
        return new AgentRunResult(true, output, cost, duration, null, rawLog);
    }

    private static StageOutput? ReadOutput(JsonElement root, string? resultText)
    {
        try
        {
            if (root.TryGetProperty("structured_output", out var so) && so.ValueKind == JsonValueKind.Object)
                return Validate(so.Deserialize<StageOutput>(Options));
            if (resultText is not null)
                return Validate(JsonSerializer.Deserialize<StageOutput>(resultText, Options));
        }
        catch (JsonException) { }
        return null;
    }

    private static StageOutput? Validate(StageOutput? o) => o is { Status: not null, Summary: not null } ? o : null;

    private static string? Tail(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? null : s.Length <= 2000 ? s : s[^2000..];
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
