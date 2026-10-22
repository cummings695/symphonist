using System.ComponentModel;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Git;

public sealed class GitCommandException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GitWorkspace(IProcessRunner runner, string workRoot) : IGitWorkspace
{
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
                await GitAsync(tmp, ct, "remote", "set-url", "origin", originUrl);
                await GitAsync(tmp, ct, "fetch", "origin");

                var remoteBranch = await RemoteBranchExistsAsync(tmp, branch, ct);
                await GitAsync(tmp, ct, "checkout", "-B", branch,
                    remoteBranch ? $"origin/{branch}" : $"origin/{repo.MainBranch}");
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
            await GitAsync(ws, ct, "fetch", "origin");
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
        var count = await GitAsync(workspace, ct, "rev-list", "--count", $"origin/{baseBranch}..HEAD");
        return int.Parse(count.Trim(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public async Task PushAsync(string workspace, string branch, CancellationToken ct) =>
        await GitAsync(workspace, ct, "push", "-u", "origin", branch);

    private async Task<bool> RemoteBranchExistsAsync(string ws, string branch, CancellationToken ct)
    {
        var r = await RunAsync(ws, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
        return r.ExitCode == 0;
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
        new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" };

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
            throw new GitCommandException($"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.StandardError.Trim()}");
        return r.StandardOutput;
    }
}
