using System.Diagnostics;
using System.Text.Json;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Codex;

/// <summary>
/// Runs OpenAI Codex headlessly (<c>codex exec</c>) inside the agent container. Each run gets its own directory, mounted
/// at <see cref="RunFilesMount"/>, holding the strict output schema, the final message and a private Codex home; with
/// ChatGPT sign-in that home holds a copy of the developer's <c>auth.json</c>, written back if Codex refreshed it.
/// </summary>
/// <param name="codexHome">The developer's Codex home (see <see cref="DefaultCodexHome"/>).</param>
/// <param name="runFilesRoot">Where per-run directories are created; each is deleted after its run.</param>
public sealed class CodexRunner(IProcessRunner runner, IAgentLauncher launcher, string codexHome, string runFilesRoot)
    : IAgentRunner
{
    public const string RunFilesMount = "/symphonist-run";

    private const string AuthFile = "auth.json";
    private const string SchemaFile = "schema.json";
    private const string LastMessageFile = "last-message.json";
    private const string HomeDir = "home";

    /// <summary>Serialises write-backs of the developer's auth.json across concurrent runs in this process.</summary>
    private static readonly SemaphoreSlim WriteBackLock = new(1, 1);

    /// <summary><c>CODEX_HOME</c> when set, else <c>~/.codex</c>, as the Codex CLI itself resolves it.</summary>
    public static string DefaultCodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { } home && !string.IsNullOrWhiteSpace(home)
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var settings = request.Settings;
        // Codex runs with its own sandbox and approvals off; only the container may stand in for them.
        if (!launcher.ProvidesIsolation(request)) return Fail("codex runs only in containers");

        byte[]? signedIn = null;
        if (settings.Codex.Auth == CodexAuth.ChatGpt)
        {
            try { signedIn = File.ReadAllBytes(Path.Combine(codexHome, AuthFile)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (signedIn is null) return Fail("Codex is not signed in: run `codex login` on this machine");
        }

        var runDir = Path.Combine(Path.GetFullPath(runFilesRoot), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(runDir, HomeDir));
            await File.WriteAllTextAsync(Path.Combine(runDir, SchemaFile), CodexSchema.Strict(request.OutputSchema), ct);
            if (signedIn is not null) await File.WriteAllBytesAsync(Path.Combine(runDir, HomeDir, AuthFile), signedIn, ct);

            var spec = launcher.Wrap(Command(request, runDir), request);
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
            return Parse(result, request, Path.Combine(runDir, LastMessageFile), sw.Elapsed, rawLog);
        }
        finally
        {
            if (signedIn is not null) await WriteBackAsync(Path.Combine(runDir, HomeDir, AuthFile), signedIn);
            try { Directory.Delete(runDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ProcessSpec Command(AgentRunRequest request, string runDir)
    {
        var settings = request.Settings;
        var args = new List<string>
        {
            "exec", HostLauncher.CodexBypassFlag, "--skip-git-repo-check", "--ephemeral", "--color", "never",
            "--output-schema", $"{RunFilesMount}/{SchemaFile}", "-o", $"{RunFilesMount}/{LastMessageFile}",
        };
        if (request.ReadOnlyWorkspace)
        {
            // The workspace is what is being reviewed: its AGENTS.md must not steer the run, and the run gets no web
            // search. Its .codex/ config is not loaded because the fresh per-run home trusts no project.
            args.Add("-c"); args.Add("project_doc_max_bytes=0");
            args.Add("-c"); args.Add("web_search=\"disabled\"");
        }
        if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("-m"); args.Add(settings.Model); }
        args.Add("-"); // prompt on stdin

        return new ProcessSpec("codex", args, request.WorkingDirectory, request.Prompt,
            Environment: new Dictionary<string, string?> { ["CODEX_HOME"] = $"{RunFilesMount}/{HomeDir}" },
            Timeout: TimeSpan.FromMinutes(settings.TimeoutMinutes),
            Mounts: [new BindMount(runDir, RunFilesMount)]);
    }

    /// <summary>
    /// Codex may have refreshed (and so rotated) the ChatGPT tokens in the run's copy; the developer's file must then
    /// get the new ones or their next run signs in with a dead refresh token. Only a changed copy that is still a JSON
    /// object is written back, atomically.
    /// </summary>
    private async Task WriteBackAsync(string runAuth, byte[] original)
    {
        byte[] current;
        try { current = await File.ReadAllBytesAsync(runAuth, CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (current.AsSpan().SequenceEqual(original) || !IsJsonObject(current)) return;

        await WriteBackLock.WaitAsync(CancellationToken.None);
        var temp = Path.Combine(codexHome, $"{AuthFile}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, current, CancellationToken.None);
            File.Move(temp, Path.Combine(codexHome, AuthFile), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the run's result stands; a later run with the stale token tells the developer to sign in.
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            WriteBackLock.Release();
        }
    }

    private static bool IsJsonObject(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static AgentRunResult Parse(ProcessResult result, AgentRunRequest request, string lastMessage,
        TimeSpan duration, string rawLog)
    {
        AgentRunResult Failed(string error) => new(false, null, null, duration, error, rawLog);

        if (result.TimedOut) return Failed($"timed out after {request.Settings.TimeoutMinutes} minutes");
        if (result.ExitCode != 0) return Failed(Tail(result.StandardError) ?? $"agent exited with code {result.ExitCode}");

        JsonElement? structured = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(lastMessage));
            if (doc.RootElement.ValueKind == JsonValueKind.Object) structured = CodexSchema.DropNulls(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return structured is null
            ? Failed("missing structured output")
            : AgentResults.FromStructured(request, structured, costUsd: null, duration, rawLog);
    }

    private static AgentRunResult Fail(string error) => new(false, null, null, TimeSpan.Zero, error, "");

    private static string? Tail(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? null : s.Length <= 2000 ? s : s[^2000..];
    }
}
