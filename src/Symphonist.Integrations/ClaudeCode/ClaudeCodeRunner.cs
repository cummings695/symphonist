using System.Diagnostics;
using System.Text.Json;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>Runs Claude Code headlessly (<c>claude -p</c>) and parses its JSON result.</summary>
public sealed class ClaudeCodeRunner(IProcessRunner runner, IAgentLauncher launcher) : IAgentRunner
{
    /// <summary>
    /// Denied on every read-only run, whatever the isolation (defense in depth): no writes, no shell, and no web access
    /// that untrusted content could use to send what the reviewer read elsewhere.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyDeniedTools =
        ["Write", "Edit", "NotebookEdit", "Bash", "WebFetch", "WebSearch"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var settings = request.Settings;
        var args = new List<string> { "-p", "--output-format", "json", "--json-schema", request.OutputSchema };
        var denied = new List<string>(request.ReadOnlyWorkspace ? ReadOnlyDeniedTools : []);
        // Bypass permission prompts only when the launcher actually sandboxes the run; config alone is not enough.
        if (settings.Isolation == Isolation.Container && launcher.ProvidesIsolation(request))
            args.Add(HostLauncher.PermissionBypassFlag);
        else
        {
            // On the host only the allowlist stands between a read-only run and the developer's files: fail closed.
            if (request.ReadOnlyWorkspace
                && settings.AllowedTools.FirstOrDefault(t => !ReviewConfig.ReadOnlyTools.Contains(t)) is { } tool)
                return new AgentRunResult(false, null, null, TimeSpan.Zero,
                    $"read-only run refuses write-capable tool '{tool}'", "");
            args.Add("--allowedTools"); args.Add(string.Join(",", settings.AllowedTools));
            if (request.AttachmentsDirectory is { } attachments)
            {
                // --add-dir makes the directory a working directory, which pre-approved Edit/Write could change:
                // attachments must stay as the ticket author attached them.
                args.Add("--add-dir"); args.Add(attachments);
                var rule = $"/{PermissionPath(attachments)}/**";
                denied.Add($"Edit({rule})"); denied.Add($"Write({rule})");
            }
        }
        if (denied.Count > 0) { args.Add("--disallowedTools"); args.Add(string.Join(",", denied)); }
        if (request.ReadOnlyWorkspace)
        {
            // The workspace is what is being reviewed: its .claude settings (permissions, hooks), CLAUDE.md / AGENTS.md
            // (loaded only with the project and local sources) and .mcp.json servers must not steer or widen the run.
            args.Add("--setting-sources"); args.Add("user");
            args.Add("--strict-mcp-config");
        }
        if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("--model"); args.Add(settings.Model); }

        var command = new ProcessSpec("claude", args, request.WorkingDirectory, request.Prompt,
            Timeout: TimeSpan.FromMinutes(settings.TimeoutMinutes));
        var spec = launcher.Wrap(command, request);

        var sw = Stopwatch.StartNew();
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(spec, ct);
        }
        catch (OperationCanceledException)
        {
            await launcher.CleanupAsync(spec, request, CancellationToken.None);
            throw;
        }
        sw.Stop();
        if (result.TimedOut) await launcher.CleanupAsync(spec, request, CancellationToken.None);

        var rawLog = result.StandardOutput + "\n--- stderr ---\n" + result.StandardError;
        return Parse(result, request, sw.Elapsed, rawLog);
    }

    /// <summary>
    /// An absolute path as Claude Code matches permission rules: POSIX form, with a Windows drive as a lowercase first
    /// segment (<c>C:\Users\me</c> becomes <c>/c/Users/me</c>) and no trailing separator.
    /// </summary>
    private static string PermissionPath(string path)
    {
        var posix = path.Replace('\\', '/').TrimEnd('/');
        return posix.Length >= 2 && char.IsAsciiLetter(posix[0]) && posix[1] == ':'
            ? $"/{char.ToLowerInvariant(posix[0])}{posix[2..]}"
            : posix;
    }

    private static AgentRunResult Parse(ProcessResult result, AgentRunRequest request, TimeSpan duration, string rawLog)
    {
        var settings = request.Settings;
        JsonElement? structured = null;
        AgentRunResult Fail(string error, StageOutput? output = null, decimal? cost = null) =>
            new(false, output, cost, duration, error, rawLog, structured);

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
        // Agent output comes only from schema-validated structured_output, never from free text.
        if (root.TryGetProperty("structured_output", out var so) && so.ValueKind == JsonValueKind.Object)
            structured = so.Clone();
        var isStage = request.OutputSchema == StageOutputSchema.Json;
        var output = isStage ? ReadOutput(structured) : null;

        if (result.ExitCode != 0 || isError)
            return Fail(FirstNonEmpty(Tail(result.StandardError), resultText,
                result.ExitCode != 0 ? $"agent exited with code {result.ExitCode}" : "agent reported an error")!,
                output, cost);
        if (!isStage)
            return structured is null
                ? Fail("missing structured output", null, cost)
                : new AgentRunResult(true, null, cost, duration, null, rawLog, structured);
        if (output is null) return Fail("missing structured output", null, cost);
        if (output.Status != "completed")
            return Fail(FirstNonEmpty(output.Summary, $"stage status '{output.Status}'")!, output, cost);
        return new AgentRunResult(true, output, cost, duration, null, rawLog, structured);
    }

    /// <summary>Stage output comes only from schema-validated <c>structured_output</c>, never from free text.</summary>
    private static StageOutput? ReadOutput(JsonElement? structured)
    {
        try
        {
            if (structured is { } so) return Validate(so.Deserialize<StageOutput>(Options));
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
