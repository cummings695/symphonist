using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeSourceControl : ISourceControl
{
    private long nextId = 1;

    /// <summary>Pull requests known to the host, by id. Opened and merged PRs are recorded here.</summary>
    public Dictionary<long, PullRequestInfo> PullRequests { get; } = [];
    public List<(string Source, string Destination, string Title, string Description)> Opened { get; } = [];
    public List<(string Source, string Destination)> FindCalls { get; } = [];
    public List<long> MergeCalls { get; } = [];

    /// <summary>Result of FindOpenPullRequestAsync; default null (none open).</summary>
    public Func<string, string, PullRequestInfo?> OnFindOpen { get; set; } = (_, _) => null;

    /// <summary>Queued merge results; when empty, merges succeed with hash "merge-{id}".</summary>
    public Queue<MergeResult> MergeResults { get; } = new();

    /// <summary>Thrown from OpenPullRequestAsync when set.</summary>
    public Exception? OpenError { get; set; }

    /// <summary>The repository of the test projects (acme/orders); PRs come from it unless a test says otherwise.</summary>
    public const string Repository = "acme/orders";

    /// <summary>
    /// The source branch of pull requests, by id (opened ones are recorded). Like the real host, a pull request's source
    /// commit is its source branch's current head, unless the PR itself names one.
    /// </summary>
    public Dictionary<long, string> SourceBranches { get; } = [];

    public static PullRequestInfo Pr(long id, PullRequestState state = PullRequestState.Open, string? hash = null,
        string? sourceCommit = null, string? repository = Repository) =>
        new(id, new Uri($"https://scm.example/pr/{id}"), state, hash, repository, sourceCommit);

    /// <summary>Records an open pull request <paramref name="id"/> from <paramref name="sourceBranch"/> of this repository.</summary>
    public PullRequestInfo AddOpen(long id, string sourceBranch)
    {
        SourceBranches[id] = sourceBranch;
        return PullRequests[id] = Pr(id);
    }

    public Task<PullRequestInfo> OpenPullRequestAsync(string sourceBranch, string destinationBranch, string title,
        string description, CancellationToken ct)
    {
        if (OpenError is not null) throw OpenError;
        Opened.Add((sourceBranch, destinationBranch, title, description));
        while (PullRequests.ContainsKey(nextId)) nextId++;
        var pr = AddOpen(nextId++, sourceBranch);
        return Task.FromResult(WithSourceCommit(pr));
    }

    public Task<PullRequestInfo?> FindOpenPullRequestAsync(string sourceBranch, string destinationBranch, CancellationToken ct)
    {
        FindCalls.Add((sourceBranch, destinationBranch));
        return Task.FromResult(OnFindOpen(sourceBranch, destinationBranch));
    }

    public Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct) =>
        PullRequests.TryGetValue(id, out var pr)
            ? Task.FromResult(WithSourceCommit(pr))
            : throw new HttpRequestException($"pull request {id} not found");

    private PullRequestInfo WithSourceCommit(PullRequestInfo pr) =>
        pr.SourceCommit is null && SourceBranches.TryGetValue(pr.Id, out var branch) && BranchHeads.TryGetValue(branch, out var head)
            ? pr with { SourceCommit = head }
            : pr;

    public Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct)
    {
        MergeCalls.Add(id);
        var result = MergeResults.Count > 0 ? MergeResults.Dequeue() : new MergeResult(true, $"merge-{id}", null);
        if (result.Merged)
            PullRequests[id] = (PullRequests.GetValueOrDefault(id) ?? Pr(id)) with
            {
                State = PullRequestState.Merged, MergeCommitHash = result.MergeCommitHash,
            };
        return Task.FromResult(result);
    }

    /// <summary>Branch heads for GetBranchHeadAsync; unknown branches throw.</summary>
    public Dictionary<string, string> BranchHeads { get; } = [];

    public Task<string> GetBranchHeadAsync(string branch, CancellationToken ct) =>
        BranchHeads.TryGetValue(branch, out var hash)
            ? Task.FromResult(hash)
            : throw new HttpRequestException($"branch {branch} not found");

    /// <summary>BranchContainsAsync(branch, other); default false (nothing verified).</summary>
    public Func<string, string, bool> OnBranchContains { get; set; } = (_, _) => false;

    public Task<bool> BranchContainsAsync(string branch, string other, CancellationToken ct) =>
        Task.FromResult(OnBranchContains(branch, other));

    public Uri BranchFileUrl(string branch, string path) => new($"https://scm.example/src/{branch}/{path}");

    public Dictionary<long, List<PullRequestComment>> Comments { get; } = [];
    public List<(long Pr, string Body)> PostedComments { get; } = [];
    public List<(long Pr, string CommentId, string Body)> Replies { get; } = [];

    /// <summary>Thrown from ListPullRequestCommentsAsync when set.</summary>
    public Exception? CommentsError { get; set; }

    public Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(long id, CancellationToken ct)
    {
        if (CommentsError is not null) throw CommentsError;
        return Task.FromResult<IReadOnlyList<PullRequestComment>>(
            Comments.TryGetValue(id, out var list) ? [.. list] : []);
    }

    /// <summary>Thrown from CommentOnPullRequestAsync when set.</summary>
    public Exception? CommentError { get; set; }

    public Task CommentOnPullRequestAsync(long id, string markdown, CancellationToken ct)
    {
        if (CommentError is not null) throw CommentError;
        PostedComments.Add((id, markdown));
        return Task.CompletedTask;
    }

    public Task ReplyToPullRequestCommentAsync(long id, PullRequestComment comment, string markdown, CancellationToken ct)
    {
        Replies.Add((id, comment.Id, markdown));
        return Task.CompletedTask;
    }
}
