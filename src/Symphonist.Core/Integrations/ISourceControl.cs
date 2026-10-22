namespace Symphonist.Core.Integrations;

public enum PullRequestState { Open, Merged, Declined }

public sealed record PullRequestInfo(long Id, Uri WebUrl, PullRequestState State, string? MergeCommitHash);

public sealed record MergeResult(bool Merged, string? MergeCommitHash, string? Error);

public interface ISourceControl
{
    Task<PullRequestInfo> OpenPullRequestAsync(string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct);
    Task<PullRequestInfo?> FindOpenPullRequestAsync(string sourceBranch, string destinationBranch, CancellationToken ct);
    Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct);

    /// <summary>Merged=false + Error when the host refuses (e.g. missing approvals) or the merge is still in progress.</summary>
    Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct);

    Uri BranchFileUrl(string branch, string path);
}
