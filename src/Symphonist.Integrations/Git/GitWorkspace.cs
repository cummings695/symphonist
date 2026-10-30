using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Git;

public sealed class GitCommandException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Keeps credentials embedded in http(s) remote URLs (<c>https://user:token@host/...</c>) out of workspaces and logs.</summary>
public static partial class GitUrls
{
    /// <summary>The URL without its user info when it is an http(s) URL; anything else (ssh, paths) unchanged.</summary>
    public static string StripUserInfo(string url) => LeadingUserInfo().Replace(url, "$1", 1);

    /// <summary>Masks the user info of every http(s) URL in <paramref name="text"/> as <c>***</c>.</summary>
    public static string Redact(string text) => AnyUserInfo().Replace(text, "$1***@");

    [GeneratedRegex(@"^(https?://)[^/]*@", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingUserInfo();

    [GeneratedRegex(@"(https?://)[^/\s@'""]+@", RegexOptions.IgnoreCase)]
    private static partial Regex AnyUserInfo();
}

/// <param name="log">Optional; used to warn (once per checkout) about credentials in the developer's origin URL.</param>
public sealed class GitWorkspace(IProcessRunner runner, string workRoot, ILogger? log = null) : IGitWorkspace
{
    private readonly HashSet<string> warnedCheckouts = [];
    public async Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct)
    {
        var repo = project.Repo;
        var ws = Path.Combine(workRoot, project.Id, ticketKey);

        if (!Directory.Exists(ws))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ws)!);
            // Prepare in a temp sibling and move into place only when every step succeeded,
            // so a failure never leaves a half-prepared workspace behind.
            var tmp = ws + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await GitAsync(null, ct, "clone", repo.LocalPath, tmp);
                var originUrl = (await GitAsync(repo.LocalPath, ct, "remote", "get-url", "origin")).Trim();
                // The agent can read .git/config (mounted read-only): never copy credentials into it. Pushes from the
                // host still authenticate through the developer's credential helper.
                var safeUrl = GitUrls.StripUserInfo(originUrl);
                if (safeUrl != originUrl) WarnCredentialsOnce(repo.LocalPath);
                await GitAsync(tmp, ct, "remote", "set-url", "origin", safeUrl);
                await GitAsync(tmp, ct, "fetch", "--prune", "origin");

                var start = await RefExistsAsync(tmp, $"refs/remotes/origin/{branch}", ct)
                    ? $"refs/remotes/origin/{branch}"
                    : $"refs/remotes/origin/{repo.MainBranch}";
                // An existing ref named in full wins over git's fallbacks (e.g. a tag of the same name).
                if (!await RefExistsAsync(tmp, start, ct)) throw new GitCommandException($"{start} does not exist");
                await GitAsync(tmp, ct, "checkout", "-B", branch, start);
                Directory.Move(tmp, ws);
            }
            catch
            {
                DeleteQuietly(tmp);
                throw;
            }
        }
        else
        {
            await GitAsync(ws, ct, "fetch", "--prune", "origin");
            await GitAsync(ws, ct, "checkout", branch);
        }
        return ws;
    }

    public async Task<bool> CommitAllAsync(string workspace, string message, CancellationToken ct)
    {
        await GitAsync(workspace, ct, "add", "-A");
        var status = await GitAsync(workspace, ct, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status)) return false;
        await GitAsync(workspace, ct, "commit", "-m", message);
        return true;
    }

    public async Task<bool> HasCommitsAheadAsync(string workspace, string baseBranch, CancellationToken ct)
    {
        var count = await GitAsync(workspace, ct, "rev-list", "--count", $"refs/remotes/origin/{baseBranch}..HEAD");
        return int.Parse(count.Trim(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public async Task PushAsync(string workspace, string branch, CancellationToken ct) =>
        await GitAsync(workspace, ct, "push", "-u", "origin", branch);

    /// <summary>
    /// NUL-separated output is never C-quoted, so non-ASCII names come back exactly; with --no-renames a file moved out
    /// of a risk area still lists its old path.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string workspace, string range, CancellationToken ct)
    {
        var output = await GitAsync(workspace, ct, [.. DiffArgs, "-z", "--name-only", range, "--"]);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Porcelain v1 with <c>-z</c> is "XY path\0" per entry, never C-quoted and always relative to the repository root;
    /// with --no-renames there is no second (original) path.
    /// </summary>
    public async Task<IReadOnlyList<string>> UncommittedFilesAsync(string workspace, CancellationToken ct)
    {
        var output = await GitAsync(workspace, ct,
            "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.Length > 3)
            .Select(e => e[3..])
            .ToList();
    }

    public async Task<string> DiffAsync(string workspace, string range, CancellationToken ct) =>
        await GitAsync(workspace, ct, [.. DiffArgs, range, "--"]);

    /// <summary>
    /// Ignores settings that could run code on the host (external diff, textconv drivers) or change what is listed
    /// (relative paths, colour codes, a global attributes file, hidden submodule changes).
    /// </summary>
    private static readonly string[] DiffArgs =
    [
        "-c", "core.attributesFile=", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-relative",
        "--no-renames", "--ignore-submodules=none", "--submodule=short",
    ];

    /// <summary>The trailing <c>--</c> keeps the range from being read as a path; a range starting with '-' is refused.</summary>
    public async Task<IReadOnlyList<string>> FirstParentCommitsAsync(string workspace, string range, CancellationToken ct)
    {
        if (range.Length == 0 || range.StartsWith('-'))
            throw new ArgumentException($"'{range}' is not a revision range", nameof(range));
        var output = await GitAsync(workspace, ct, "rev-list", "--first-parent", range, "--");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// A full ref name (<c>refs/...</c>) must exist exactly as named: <c>rev-parse</c> alone would fall back to e.g.
    /// <c>refs/tags/refs/remotes/origin/x</c>, a tag anyone who can push tags can plant.
    /// </summary>
    public async Task<string?> ResolveCommitAsync(string workspace, string rev, CancellationToken ct)
    {
        if (rev.StartsWith("refs/", StringComparison.Ordinal) && !await RefExistsAsync(workspace, rev, ct)) return null;
        var r = await RunAsync(workspace, ct, "rev-parse", "--verify", "--quiet", rev + "^{commit}");
        if (!r.TimedOut && r.ExitCode == 0) return r.StandardOutput.Trim();
        if (!r.TimedOut && r.ExitCode == 1) return null; // --quiet: no such commit
        throw new GitCommandException(GitUrls.Redact(
            $"git rev-parse {rev} failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {r.StandardError.Trim()}"));
    }

    /// <summary>
    /// Only a directory holding its own <c>.git</c> counts as a checkout, so a folder inside some other repository
    /// never reports that repository's origin.
    /// </summary>
    public async Task<string?> OriginUrlAsync(string checkoutPath, CancellationToken ct)
    {
        var dotGit = Path.Combine(checkoutPath, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return null;
        var r = await RunAsync(checkoutPath, ct, "remote", "get-url", "origin");
        if (r.TimedOut)
            throw new GitCommandException($"git remote get-url origin timed out in {checkoutPath}");
        var url = r.StandardOutput.Trim();
        return r.ExitCode == 0 && url.Length > 0 ? GitUrls.StripUserInfo(url) : null;
    }

    public Task RemoveAsync(ProjectConfig project, string key, CancellationToken ct)
    {
        if (key.Length == 0 || key is "." or ".." || key.IndexOfAny(['/', '\\']) >= 0
            || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{key}' is not a workspace key", nameof(key));
        var ws = Path.Combine(workRoot, project.Id, key);
        if (Directory.Exists(ws))
        {
            // Git marks object files read-only, which Directory.Delete refuses on Windows.
            foreach (var f in Directory.EnumerateFiles(ws, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(ws, true);
        }

        return Task.CompletedTask;
    }

    /// <summary>Whether the full ref <paramref name="name"/> exists exactly (no fallbacks to other namespaces).</summary>
    private async Task<bool> RefExistsAsync(string ws, string name, CancellationToken ct)
    {
        var r = await RunAsync(ws, ct, "show-ref", "--verify", "--quiet", name);
        if (!r.TimedOut && r.ExitCode is 0 or 1) return r.ExitCode == 0;
        throw new GitCommandException(GitUrls.Redact(
            $"git show-ref {name} failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {r.StandardError.Trim()}"));
    }

    private void WarnCredentialsOnce(string checkout)
    {
        lock (warnedCheckouts)
            if (!warnedCheckouts.Add(checkout)) return;
        log?.LogWarning(
            "The origin URL of {Checkout} contains credentials; Symphonist removes them from agent workspaces and pushes " +
            "with your git credential helper. Consider removing them from the URL and using a credential helper instead.",
            checkout);
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch { /* best effort */ }
    }

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyDictionary<string, string?> GitEnv =
        new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };

    /// <summary>
    /// The agent can write to the workspace, so never let repo-local config run code on the host: hooks come from an
    /// empty directory Symphonist owns (outside any mounted workspace) and fsmonitor is off. These <c>-c</c> options
    /// take precedence over <c>.git/config</c>.
    /// </summary>
    private string[] Hardened(string[] args)
    {
        var emptyHooks = Path.Combine(workRoot, ".empty-hooks");
        Directory.CreateDirectory(emptyHooks);
        return ["-c", $"core.hooksPath={emptyHooks}", "-c", "core.fsmonitor=false", .. args];
    }

    private async Task<ProcessResult> RunAsync(string? dir, CancellationToken ct, params string[] args)
    {
        try
        {
            return await runner.RunAsync(
                new ProcessSpec("git", Hardened(args), dir, Environment: GitEnv, Timeout: GitTimeout), ct);
        }
        catch (Win32Exception ex)
        {
            throw new GitCommandException($"Could not run 'git'; is git installed and on PATH? ({ex.Message})", ex);
        }
    }

    private async Task<string> GitAsync(string? dir, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(dir, ct, args);
        if (r.TimedOut || r.ExitCode != 0)
            throw new GitCommandException(GitUrls.Redact(
                $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.StandardError.Trim()}"));
        return r.StandardOutput;
    }
}
