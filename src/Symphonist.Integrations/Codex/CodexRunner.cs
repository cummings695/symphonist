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
/// <remarks>
/// The run directory is writable by the untrusted agent, so everything read back from it is read as
/// <see cref="ReadRunFile">untrusted</see>: no links, no special files, bounded size.
/// </remarks>
/// <param name="codexHome">The developer's Codex home (see <see cref="DefaultCodexHome"/>).</param>
/// <param name="runFilesRoot">Where per-run directories are created; each is deleted after its run.</param>
public sealed class CodexRunner(IProcessRunner runner, IAgentLauncher launcher, string codexHome, string runFilesRoot)
    : IAgentRunner
{
    public const string RunFilesMount = "/symphonist-run";
    public const string RotatedElsewhereHint = "Codex sign-in was refreshed by another run: retry, or run `codex login`";

    private const string AuthFile = "auth.json";
    private const string SchemaFile = "schema.json";
    private const string LastMessageFile = "last-message.json";
    private const string HomeDir = "home";
    private const int MaxAuthBytes = 64 * 1024;
    private const int MaxLastMessageBytes = 1024 * 1024;

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
            signedIn = ReadDeveloperAuth();
            if (signedIn is null) return Fail("Codex is not signed in: run `codex login` on this machine");
        }

        var runDir = Path.Combine(Path.GetFullPath(runFilesRoot), Guid.NewGuid().ToString("N"));
        var home = Path.Combine(runDir, HomeDir);
        var notes = new List<string>();
        var rotatedElsewhere = false;
        ProcessResult? result = null;
        AgentRunResult outcome;
        try
        {
            CreatePrivateDirectory(runDir);
            CreatePrivateDirectory(home);
            await File.WriteAllTextAsync(Path.Combine(runDir, SchemaFile), CodexSchema.Strict(request.OutputSchema), ct);
            if (signedIn is not null) await WritePrivateAsync(Path.Combine(home, AuthFile), signedIn);

            var spec = launcher.Wrap(Command(request, runDir), request);
            var sw = Stopwatch.StartNew();
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
            outcome = Parse(result, request, Path.Combine(runDir, LastMessageFile), sw.Elapsed, rawLog);
        }
        finally
        {
            if (signedIn is not null)
            {
                // Checked before our own write-back: a change now can only come from another run.
                if (result is { ExitCode: not 0, TimedOut: false })
                    rotatedElsewhere = ReadDeveloperAuth() is { } now && !now.AsSpan().SequenceEqual(signedIn);
                if (await WriteBackAsync(home, signedIn) is { } note) notes.Add(note);
            }
            try { Directory.Delete(runDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        if (rotatedElsewhere && !outcome.Succeeded)
            outcome = outcome with { Error = $"{outcome.Error}; {RotatedElsewhereHint}" };
        if (notes.Count > 0) outcome = outcome with { RawLog = outcome.RawLog + "\n" + string.Join("\n", notes) };
        return outcome;
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

    private byte[]? ReadDeveloperAuth()
    {
        try { return File.ReadAllBytes(Path.Combine(codexHome, AuthFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Codex may have refreshed (and so rotated) the ChatGPT tokens in the run's copy; the developer's file must then
    /// get the new ones or their next run signs in with a dead refresh token. Only a changed copy that is a JSON object
    /// for the same account is written back, atomically and owner-only. Returns a note for the raw log when a changed
    /// copy is not written back.
    /// </summary>
    private async Task<string?> WriteBackAsync(string home, byte[] original)
    {
        var current = ReadRunFile(home, AuthFile, MaxAuthBytes, out var refusal);
        if (refusal is not null) return $"Codex auth.json not written back: {refusal}";
        if (current is null || current.AsSpan().SequenceEqual(original)) return null;
        if (!IsJsonObject(current)) return "Codex auth.json not written back: not a JSON object";
        // A prompt-injected agent must not be able to swap another account's tokens into the developer's login.
        if (AccountId(original) is { } account && AccountId(current) != account)
            return "Codex auth.json not written back: it names a different account";

        await WriteBackLock.WaitAsync(CancellationToken.None);
        var temp = Path.Combine(codexHome, $"{AuthFile}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WritePrivateAsync(temp, current);
            File.Move(temp, Path.Combine(codexHome, AuthFile), overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return $"Codex auth.json not written back: {e.Message}";
        }
        finally
        {
            WriteBackLock.Release();
        }
    }

    private static string? AccountId(byte[] auth)
    {
        try
        {
            using var doc = JsonDocument.Parse(auth);
            return doc.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
                && tokens.TryGetProperty("account_id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
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

    /// <summary>
    /// Reads <paramref name="name"/> in <paramref name="dir"/>, a directory the agent could write. Returns null when the
    /// file is absent; refuses (null plus <paramref name="refusal"/>) a link (file or directory, which could point at
    /// any host file), anything but a non-empty regular file (FIFOs and devices report size 0 and are never opened),
    /// and anything over <paramref name="maxBytes"/>. The agent's process has ended by now (exited, or cleaned up after
    /// a timeout or cancel), so nothing swaps the file between these checks and the read.
    /// </summary>
    private static byte[]? ReadRunFile(string dir, string name, int maxBytes, out string? refusal)
    {
        refusal = null;
        try
        {
            if (IsLink(new DirectoryInfo(dir))) { refusal = "its directory is a link"; return null; }
            var file = new FileInfo(Path.Combine(dir, name));
            if (IsLink(file)) { refusal = "it is a link"; return null; }
            if (!file.Exists) return null;
            if (file.Length == 0) { refusal = "it is empty or not a regular file"; return null; }
            if (file.Length > maxBytes) { refusal = "it is too large"; return null; }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[maxBytes + 1];
            int read, total = 0;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total > maxBytes) { refusal = "it is too large"; return null; }
            return buffer[..total];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    /// <summary>Owner-only on Linux/macOS (the container runs as the host user, so it can still use it).</summary>
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, CancellationToken.None);
    }

    private static AgentRunResult Parse(ProcessResult result, AgentRunRequest request, string lastMessage,
        TimeSpan duration, string rawLog)
    {
        AgentRunResult Failed(string error) => new(false, null, null, duration, error, rawLog);

        if (result.TimedOut) return Failed($"timed out after {request.Settings.TimeoutMinutes} minutes");
        if (result.ExitCode != 0) return Failed(Tail(result.StandardError) ?? $"agent exited with code {result.ExitCode}");

        JsonElement? structured = null;
        if (ReadRunFile(Path.GetDirectoryName(lastMessage)!, Path.GetFileName(lastMessage), MaxLastMessageBytes, out _)
            is { } bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) structured = CodexSchema.DropNulls(doc.RootElement);
            }
            catch (JsonException) { }
        }
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
