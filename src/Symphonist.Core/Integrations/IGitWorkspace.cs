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

    /// <summary>
    /// Files changed in <c>git diff --name-only {range}</c> ('/'-separated, exactly as stored), e.g. "origin/main...HEAD".
    /// A rename lists both its old and its new path.
    /// </summary>
    Task<IReadOnlyList<string>> ChangedFilesAsync(string workspace, string range, CancellationToken ct);

    /// <summary>The patch of <c>git diff {range}</c>; renames show as delete plus add.</summary>
    Task<string> DiffAsync(string workspace, string range, CancellationToken ct);

    /// <summary>
    /// The commits of <c>git rev-list --first-parent {range}</c> (e.g. "{production sha}..{main sha}"): full hashes, newest
    /// first. On a branch that only takes merges these are its merge (or squashed) commits plus any direct commits.
    /// </summary>
    Task<IReadOnlyList<string>> FirstParentCommitsAsync(string workspace, string range, CancellationToken ct);

    /// <summary>The commit SHA <paramref name="rev"/> (e.g. "origin/main") names, or null when there is no such commit.</summary>
    Task<string?> ResolveCommitAsync(string workspace, string rev, CancellationToken ct);

    /// <summary>
    /// origin's URL of the checkout at <paramref name="checkoutPath"/> (credentials stripped), or null when it is not a
    /// git checkout or has no origin.
    /// </summary>
    Task<string?> OriginUrlAsync(string checkoutPath, CancellationToken ct);

    /// <summary>Deletes the workspace <see cref="PrepareAsync"/> would use for (project, key); no-op when absent.</summary>
    Task RemoveAsync(ProjectConfig project, string key, CancellationToken ct);
}
