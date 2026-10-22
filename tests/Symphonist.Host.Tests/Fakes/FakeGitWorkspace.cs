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
        return Task.FromResult(OnCommit(message));
    }

    public Task PushAsync(string workspace, string branch, CancellationToken ct)
    {
        if (PushError is not null) throw PushError;
        Pushes.Add((workspace, branch));
        return Task.CompletedTask;
    }
}
