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

    public Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct)
    {
        if (PrepareError is not null) throw PrepareError;
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
        return Task.CompletedTask;
    }
}
