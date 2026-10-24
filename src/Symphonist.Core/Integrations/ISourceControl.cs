namespace Symphonist.Core.Integrations;

public enum PullRequestState { Open, Merged, Declined }

/// <param name="SourceRepository">The repository the source branch lives in (e.g. "workspace/slug"); a fork's differs.</param>
/// <param name="SourceCommit">The source branch commit the pull request would merge now (may be abbreviated).</param>
public sealed record PullRequestInfo(long Id, Uri WebUrl, PullRequestState State, string? MergeCommitHash,
    string? SourceRepository = null, string? SourceCommit = null)
{
    /// <summary>
    /// Whether the pull request would merge <paramref name="commit"/> (a full or abbreviated hash) from
    /// <paramref name="repository"/>. Fails closed: anything unknown, or a hash shorter than 7 characters, is no.
    /// </summary>
    public bool WouldMerge(string repository, string commit) =>
        string.Equals(SourceRepository, repository, StringComparison.OrdinalIgnoreCase)
        && SourceCommit is { Length: >= 7 } source && commit.Length >= 7
        && (commit.StartsWith(source, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(commit, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Accepted">The host accepted the merge and is still performing it (not a refusal).</param>
public sealed record MergeResult(bool Merged, string? MergeCommitHash, string? Error, bool Accepted = false);

/// <summary>The host refused to open a pull request because the source branch has nothing the destination lacks.</summary>
public sealed class NoChangesToPullException(string message) : Exception(message);

public interface ISourceControl
{
    /// <exception cref="NoChangesToPullException">The destination already contains the source branch.</exception>
    Task<PullRequestInfo> OpenPullRequestAsync(string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct);
    /// <summary>An open pull request from <paramref name="sourceBranch"/> of this repository (never a fork's).</summary>
    Task<PullRequestInfo?> FindOpenPullRequestAsync(string sourceBranch, string destinationBranch, CancellationToken ct);
    Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct);

    /// <summary>Merged=false + Error when the host refuses (e.g. missing approvals) or the merge is still in progress.</summary>
    Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct);

    /// <summary>True when <paramref name="branch"/> already contains every commit of <paramref name="other"/>.</summary>
    Task<bool> BranchContainsAsync(string branch, string other, CancellationToken ct);

    /// <summary>The commit hash at the tip of <paramref name="branch"/>.</summary>
    Task<string> GetBranchHeadAsync(string branch, CancellationToken ct);

    Uri BranchFileUrl(string branch, string path);
}
