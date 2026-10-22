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

    public static PullRequestInfo Pr(long id, PullRequestState state = PullRequestState.Open, string? hash = null) =>
        new(id, new Uri($"https://scm.example/pr/{id}"), state, hash);

    public Task<PullRequestInfo> OpenPullRequestAsync(string sourceBranch, string destinationBranch, string title,
        string description, CancellationToken ct)
    {
        if (OpenError is not null) throw OpenError;
        Opened.Add((sourceBranch, destinationBranch, title, description));
        while (PullRequests.ContainsKey(nextId)) nextId++;
        var pr = Pr(nextId++);
        PullRequests[pr.Id] = pr;
        return Task.FromResult(pr);
    }

    public Task<PullRequestInfo?> FindOpenPullRequestAsync(string sourceBranch, string destinationBranch, CancellationToken ct)
    {
        FindCalls.Add((sourceBranch, destinationBranch));
        return Task.FromResult(OnFindOpen(sourceBranch, destinationBranch));
    }

    public Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct) =>
        PullRequests.TryGetValue(id, out var pr)
            ? Task.FromResult(pr)
            : throw new HttpRequestException($"pull request {id} not found");

    public Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct)
    {
        MergeCalls.Add(id);
        var result = MergeResults.Count > 0 ? MergeResults.Dequeue() : new MergeResult(true, $"merge-{id}", null);
        if (result.Merged) PullRequests[id] = Pr(id, PullRequestState.Merged, result.MergeCommitHash);
        return Task.FromResult(result);
    }

    /// <summary>Branch heads for GetBranchHeadAsync; unknown branches throw.</summary>
    public Dictionary<string, string> BranchHeads { get; } = [];

    public Task<string> GetBranchHeadAsync(string branch, CancellationToken ct) =>
        BranchHeads.TryGetValue(branch, out var hash)
            ? Task.FromResult(hash)
            : throw new HttpRequestException($"branch {branch} not found");

    public Uri BranchFileUrl(string branch, string path) => new($"https://scm.example/src/{branch}/{path}");
}
