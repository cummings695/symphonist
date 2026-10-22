using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations;

public interface IGitWorkspace
{
    /// <summary>Returns the workspace path.</summary>
    Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct);

    /// <summary>Returns false when nothing changed.</summary>
    Task<bool> CommitAllAsync(string workspace, string message, CancellationToken ct);

    /// <summary>True when HEAD has commits not on <c>origin/{baseBranch}</c> (e.g. committed by an earlier attempt).</summary>
    Task<bool> HasCommitsAheadAsync(string workspace, string baseBranch, CancellationToken ct);

    Task PushAsync(string workspace, string branch, CancellationToken ct);
}
