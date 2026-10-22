using System.ComponentModel;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Git;

public sealed class GitCommandException(string message) : Exception(message);

public sealed class GitWorkspace(IProcessRunner runner, string workRoot) : IGitWorkspace
{
    public async Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct)
    {
        var repo = project.Repo;
        var ws = Path.Combine(workRoot, project.Id, ticketKey);

        if (!Directory.Exists(ws))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ws)!);
            await GitAsync(null, ct, "clone", repo.LocalPath, ws);
            var originUrl = (await GitAsync(repo.LocalPath, ct, "remote", "get-url", "origin")).Trim();
            await GitAsync(ws, ct, "remote", "set-url", "origin", originUrl);
            await GitAsync(ws, ct, "fetch", "origin");

            var remoteBranch = await RemoteBranchExistsAsync(ws, branch, ct);
            await GitAsync(ws, ct, "checkout", "-B", branch,
                remoteBranch ? $"origin/{branch}" : $"origin/{repo.MainBranch}");
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

    public async Task PushAsync(string workspace, string branch, CancellationToken ct) =>
        await GitAsync(workspace, ct, "push", "-u", "origin", branch);

    private async Task<bool> RemoteBranchExistsAsync(string ws, string branch, CancellationToken ct)
    {
        var r = await RunAsync(ws, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
        return r.ExitCode == 0;
    }

    private async Task<ProcessResult> RunAsync(string? dir, CancellationToken ct, params string[] args)
    {
        try
        {
            return await runner.RunAsync(new ProcessSpec("git", args, dir), ct);
        }
        catch (Win32Exception ex)
        {
            throw new GitCommandException($"Could not run 'git'; is git installed and on PATH? ({ex.Message})");
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
