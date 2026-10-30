using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeGitWorkspace : IGitWorkspace
{
    public string WorkspacePath { get; set; } = Path.Combine(Path.GetTempPath(), "symphonist-fake-ws");
    public List<(string TicketKey, string Branch)> Prepared { get; } = [];
    public List<(string Workspace, string Message)> Commits { get; } = [];
    public List<(string Workspace, string Branch)> Pushes { get; } = [];

    /// <summary>Return value of CommitAllAsync per message (false = nothing changed); default true.</summary>
    public Func<string, bool> OnCommit { get; set; } = _ => true;

    /// <summary>Return value of HasCommitsAheadAsync; default: whether any commit returned true so far.</summary>
    public Func<bool>? OnHasCommitsAhead { get; set; }
    public List<(string Workspace, string BaseBranch)> AheadChecks { get; } = [];
    private bool committed;

    /// <summary>Thrown from PrepareAsync when set (e.g. a GitCommandException).</summary>
    public Exception? PrepareError { get; set; }

    /// <summary>Thrown from PushAsync when set.</summary>
    public Exception? PushError { get; set; }

    /// <summary>Returned by ChangedFilesAsync; default none.</summary>
    public List<string> ChangedFiles { get; set; } = [];
    public List<(string Workspace, string Range)> ChangedFilesCalls { get; } = [];

    /// <summary>Thrown from ChangedFilesAsync when set.</summary>
    public Exception? ChangedFilesError { get; set; }

    /// <summary>Returned by UncommittedFilesAsync; default none.</summary>
    public List<string> UncommittedFiles { get; set; } = [];

    /// <summary>Returned by DiffAsync; default a one-line patch.</summary>
    public string Diff { get; set; } = "diff --git a/x b/x\n+change\n";
    public List<(string Workspace, string Range)> DiffCalls { get; } = [];

    /// <summary>Thrown from DiffAsync when set.</summary>
    public Exception? DiffError { get; set; }

    /// <summary>Commits ResolveCommitAsync knows; anything else resolves to null (no such commit).</summary>
    public Dictionary<string, string> RemoteCommits { get; } = new()
    {
        ["refs/remotes/origin/main"] = "main000", ["refs/remotes/origin/production"] = "prod000",
        ["refs/remotes/origin/symphonist/ORD-1"] = "head000",
    };

    /// <summary>"prepare {key}" and "remove {key}" in call order.</summary>
    public List<string> Events { get; } = [];

    /// <summary>LocalPath of the project at each PrepareAsync call.</summary>
    public List<string> PreparedFrom { get; } = [];

    public Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct)
    {
        if (PrepareError is not null) throw PrepareError;
        Events.Add($"prepare {ticketKey}");
        PreparedFrom.Add(project.Repo.LocalPath);
        Prepared.Add((ticketKey, branch));
        return Task.FromResult(WorkspacePath);
    }

    public Task<bool> CommitAllAsync(string workspace, string message, CancellationToken ct)
    {
        Commits.Add((workspace, message));
        var changed = OnCommit(message);
        committed |= changed;
        return Task.FromResult(changed);
    }

    public Task<bool> HasCommitsAheadAsync(string workspace, string baseBranch, CancellationToken ct)
    {
        AheadChecks.Add((workspace, baseBranch));
        return Task.FromResult(OnHasCommitsAhead?.Invoke() ?? committed);
    }

    public Task PushAsync(string workspace, string branch, CancellationToken ct)
    {
        if (PushError is not null) throw PushError;
        Pushes.Add((workspace, branch));
        // A push of a new branch creates its remote-tracking ref at HEAD.
        if (Head() is { } head) RemoteCommits.TryAdd($"refs/remotes/origin/{branch}", head);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ChangedFilesAsync(string workspace, string range, CancellationToken ct)
    {
        ChangedFilesCalls.Add((workspace, range));
        if (ChangedFilesError is not null) throw ChangedFilesError;
        return Task.FromResult<IReadOnlyList<string>>(ChangedFiles.ToList());
    }

    public Task<IReadOnlyList<string>> UncommittedFilesAsync(string workspace, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(UncommittedFiles.ToList());

    public Task<string> DiffAsync(string workspace, string range, CancellationToken ct)
    {
        DiffCalls.Add((workspace, range));
        if (DiffError is not null) throw DiffError;
        return Task.FromResult(Diff);
    }

    /// <summary>
    /// Like the real workspace, HEAD is the last prepared branch: its remote head when pushed, else origin's main (a new
    /// branch starts there). A "HEAD" entry in <see cref="RemoteCommits"/> overrides it.
    /// </summary>
    public Task<string?> ResolveCommitAsync(string workspace, string rev, CancellationToken ct) =>
        Task.FromResult(rev == "HEAD" ? Head() : RemoteCommits.GetValueOrDefault(rev));

    private string? Head() =>
        RemoteCommits.GetValueOrDefault("HEAD")
        ?? (Prepared.Count > 0 ? RemoteCommits.GetValueOrDefault($"refs/remotes/origin/{Prepared[^1].Branch}") : null)
        ?? RemoteCommits.GetValueOrDefault("refs/remotes/origin/main");

    /// <summary>Returned by FirstParentCommitsAsync for any range (newest first); default none.</summary>
    public List<string> FirstParentCommits { get; set; } = [];
    public List<(string Workspace, string Range)> FirstParentCalls { get; } = [];

    /// <summary>Thrown from FirstParentCommitsAsync when set.</summary>
    public Exception? FirstParentError { get; set; }

    public Task<IReadOnlyList<string>> FirstParentCommitsAsync(string workspace, string range, CancellationToken ct)
    {
        FirstParentCalls.Add((workspace, range));
        if (FirstParentError is not null) throw FirstParentError;
        return Task.FromResult<IReadOnlyList<string>>(FirstParentCommits.ToList());
    }

    /// <summary>Returned by OriginUrlAsync for any path; default null (not a checkout).</summary>
    public string? OriginUrl { get; set; }

    public Task<string?> OriginUrlAsync(string checkoutPath, CancellationToken ct) => Task.FromResult(OriginUrl);

    /// <summary>(project id, key) of each RemoveAsync call.</summary>
    public List<(string ProjectId, string Key)> Removed { get; } = [];

    /// <summary>Thrown from RemoveAsync when set (after recording the call).</summary>
    public Exception? RemoveError { get; set; }

    /// <summary>Runs on each RemoveAsync call after recording it (e.g. to throw on the n-th call).</summary>
    public Action? OnRemove { get; set; }

    public Task RemoveAsync(ProjectConfig project, string key, CancellationToken ct)
    {
        Events.Add($"remove {key}");
        Removed.Add((project.Id, key));
        if (RemoveError is not null) throw RemoveError;
        OnRemove?.Invoke();
        return Task.CompletedTask;
    }
}
