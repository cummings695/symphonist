using System.Net;
using System.Text.Json;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.GitHub;

public sealed class GitHubSourceControl(HttpClient http, GitHubRepoConfig config, string token) : ISourceControl
{
    private readonly GitHubApi _api = new(http, token);

    private string RepoPath => $"repos/{Uri.EscapeDataString(config.Owner)}/{Uri.EscapeDataString(config.Repo)}";

    public async Task<PullRequestInfo> OpenPullRequestAsync(
        string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct)
    {
        var path = $"{RepoPath}/pulls";
        var (status, text) = await _api.SendRawAsync(HttpMethod.Post, path,
            new { title, body = description, head = sourceBranch, @base = destinationBranch }, ct);

        // GitHub answers 422 with an errors[] item "No commits between {base} and {head}" when the destination already
        // has everything. Matched on that prefix only: callers treat it as "nothing to release".
        if (status == HttpStatusCode.UnprocessableEntity && NoChangesMessage(text) is { } message)
            throw new NoChangesToPullException(message);
        if ((int)status is < 200 or > 299) throw GitHubApi.Failure(HttpMethod.Post, path, status, text);

        using var doc = JsonDocument.Parse(text);
        return Map(doc.RootElement);
    }

    public async Task<PullRequestInfo?> FindOpenPullRequestAsync(
        string sourceBranch, string destinationBranch, CancellationToken ct)
    {
        // A fork's pull request can use the same branch names: only one from this repository is ours (checked here too,
        // not just trusted to the query).
        var head = Uri.EscapeDataString($"{config.Owner}:{sourceBranch}");
        using var doc = await _api.SendAsync(HttpMethod.Get,
            $"{RepoPath}/pulls?state=open&head={head}&base={Uri.EscapeDataString(destinationBranch)}", null, ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var pr in doc.RootElement.EnumerateArray().Select(Map))
                if (string.Equals(pr.SourceRepository, config.FullName, StringComparison.OrdinalIgnoreCase))
                    return pr;
        return null;
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct)
    {
        using var doc = await _api.SendAsync(HttpMethod.Get, $"{RepoPath}/pulls/{id}", null, ct);
        return Map(doc.RootElement);
    }

    public async Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct)
    {
        var path = $"{RepoPath}/pulls/{id}/merge";
        var (status, text) = await _api.SendRawAsync(HttpMethod.Put, path, new { merge_method = config.MergeStrategy }, ct);

        // 405 (not mergeable, e.g. branch protection), 409 (head changed) and other 4xx are the host refusing.
        if ((int)status is >= 400 and < 500)
            return new MergeResult(false, null, Message(text) ?? GitHubApi.Excerpt(text));
        if ((int)status is < 200 or > 299)
            throw GitHubApi.Failure(HttpMethod.Put, path, status, text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True
            ? new MergeResult(true, String(root, "sha"), null)
            : new MergeResult(false, null, Message(text) ?? "merge was not performed");
    }

    /// <summary>Asks how far <paramref name="other"/> is ahead of <paramref name="branch"/>; not at all means contained.</summary>
    public async Task<bool> BranchContainsAsync(string branch, string other, CancellationToken ct)
    {
        var path = $"{RepoPath}/compare/{GitHubApi.EscapeRef(branch)}...{GitHubApi.EscapeRef(other)}";
        using var doc = await _api.SendAsync(HttpMethod.Get, path, null, ct);
        return doc.RootElement.TryGetProperty("ahead_by", out var ahead) && ahead.ValueKind == JsonValueKind.Number
            ? ahead.GetInt64() == 0
            : throw new HttpRequestException($"GitHub GET {path} returned no ahead_by");
    }

    public async Task<string> GetBranchHeadAsync(string branch, CancellationToken ct)
    {
        var path = $"{RepoPath}/branches/{GitHubApi.EscapeRef(branch)}";
        using var doc = await _api.SendAsync(HttpMethod.Get, path, null, ct);
        return doc.RootElement.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
               && String(commit, "sha") is { Length: > 0 } sha
            ? sha
            : throw new HttpRequestException($"GitHub GET {path} returned no commit sha");
    }

    private string? _myLogin;

    private async Task<string> MyLoginAsync(CancellationToken ct)
    {
        if (_myLogin is not null) return _myLogin;
        using var doc = await _api.SendAsync(HttpMethod.Get, "user", null, ct);
        return _myLogin = String(doc.RootElement, "login") ?? "";
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(long id, CancellationToken ct)
    {
        var me = await MyLoginAsync(ct);
        var comments = new List<PullRequestComment>();
        await ReadPagesAsync($"{RepoPath}/issues/{id}/comments", comments, me, "issue", review: false, ct);
        await ReadPagesAsync($"{RepoPath}/pulls/{id}/comments", comments, me, "review", review: true, ct);
        return [.. comments.OrderBy(c => c.Created)];
    }

    private async Task ReadPagesAsync(string path, List<PullRequestComment> into, string me, string prefix, bool review,
        CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            using var doc = await _api.SendAsync(HttpMethod.Get, $"{path}?per_page=100&page={page}", null, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var login = c.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                    ? String(u, "login") : null;
                string? file = null;
                int? line = null;
                string? parent = null;
                if (review)
                {
                    file = String(c, "path");
                    line = Int(c, "line") ?? Int(c, "original_line");
                    if (c.TryGetProperty("in_reply_to_id", out var r) && r.ValueKind == JsonValueKind.Number)
                        parent = $"review:{r.GetInt64()}";
                }

                into.Add(new PullRequestComment($"{prefix}:{c.GetProperty("id").GetInt64()}", login ?? "ghost",
                    login is not null && me.Length > 0 && string.Equals(login, me, StringComparison.OrdinalIgnoreCase),
                    DateTimeOffset.TryParse(String(c, "created_at"), out var created) ? created : DateTimeOffset.MinValue,
                    String(c, "body") ?? "", file, line, parent));
            }

            if (doc.RootElement.GetArrayLength() < 100) return;
        }
    }

    public async Task CommentOnPullRequestAsync(long id, string markdown, CancellationToken ct)
    {
        using var _ = await _api.SendAsync(HttpMethod.Post, $"{RepoPath}/issues/{id}/comments", new { body = markdown }, ct);
    }

    public async Task ReplyToPullRequestCommentAsync(long id, PullRequestComment comment, string markdown, CancellationToken ct)
    {
        const string review = "review:";
        if (comment.Id.StartsWith(review, StringComparison.Ordinal) && long.TryParse(comment.Id[review.Length..], out var reviewId))
        {
            using var _ = await _api.SendAsync(HttpMethod.Post, $"{RepoPath}/pulls/{id}/comments/{reviewId}/replies",
                new { body = markdown }, ct);
            return;
        }

        var firstLine = comment.Body.ReplaceLineEndings("\n").Split('\n')[0];
        if (firstLine.Length > 200) firstLine = firstLine[..200];
        await CommentOnPullRequestAsync(id, $"> {firstLine}\n\n{markdown}", ct);
    }

    private static int? Int(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>The web page of pull request <paramref name="id"/> (no API call needed).</summary>
    public static Uri PullRequestUrl(GitHubRepoConfig repo, long id) =>
        new($"https://github.com/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Repo)}/pull/{id}");

    public Uri BranchFileUrl(string branch, string path) =>
        new($"https://github.com/{Uri.EscapeDataString(config.Owner)}/{Uri.EscapeDataString(config.Repo)}/blob/{branch}/{path}");

    private const string NoCommitsText = "No commits between";

    private static string? NoChangesMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var error in errors.EnumerateArray())
                    if (error.ValueKind == JsonValueKind.Object && String(error, "message") is { } m
                        && m.StartsWith(NoCommitsText, StringComparison.Ordinal))
                        return m;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Message(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? String(doc.RootElement, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static PullRequestInfo Map(JsonElement pr)
    {
        var merged = pr.TryGetProperty("merged_at", out var at) && at.ValueKind == JsonValueKind.String;
        var state = String(pr, "state") switch
        {
            "open" => PullRequestState.Open,
            _ => merged ? PullRequestState.Merged : PullRequestState.Declined,
        };
        string? repository = null, sha = null;
        if (pr.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
        {
            sha = String(head, "sha");
            if (head.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object)
                repository = String(repo, "full_name");
        }

        return new PullRequestInfo(pr.GetProperty("number").GetInt64(), new Uri(pr.GetProperty("html_url").GetString()!),
            state, merged ? String(pr, "merge_commit_sha") : null, repository, sha);
    }
}
