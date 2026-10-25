using System.Net;
using System.Text.Json;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Bitbucket;

public sealed class BitbucketSourceControl(HttpClient http, BitbucketRepoConfig config, string apiToken) : ISourceControl
{
    private readonly BitbucketApi _api = new(http, config, apiToken);

    public async Task<PullRequestInfo> OpenPullRequestAsync(
        string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct)
    {
        var path = $"{_api.RepoPath}/pullrequests";
        var (status, text) = await _api.SendRawAsync(HttpMethod.Post, path, new
        {
            title,
            description,
            source = new { branch = new { name = sourceBranch } },
            destination = new { branch = new { name = destinationBranch } },
            close_source_branch = false,
        }, ct);

        // Bitbucket answers 400 {"type":"error","error":{"message":"There are no changes to be pulled"}} when the
        // destination already has everything. Matched exactly: callers treat it as "nothing to release".
        if (status == HttpStatusCode.BadRequest && NoChangesMessage(text) is { } message)
            throw new NoChangesToPullException(message);
        if ((int)status is < 200 or > 299) throw BitbucketApi.Failure(HttpMethod.Post, path, status, text);

        using var doc = JsonDocument.Parse(text);
        return Map(doc.RootElement);
    }

    public async Task<PullRequestInfo?> FindOpenPullRequestAsync(
        string sourceBranch, string destinationBranch, CancellationToken ct)
    {
        // A fork's pull request can use the same branch names: only one from this repository is ours (checked here too,
        // not just trusted to the query).
        var q = $"source.repository.full_name=\"{config.FullName}\" AND source.branch.name=\"{sourceBranch}\" "
                + $"AND destination.branch.name=\"{destinationBranch}\"";
        using var doc = await _api.SendAsync(HttpMethod.Get,
            $"{_api.RepoPath}/pullrequests?state=OPEN&q={Uri.EscapeDataString(q)}", null, ct);
        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var pr in values.EnumerateArray().Select(Map))
                if (string.Equals(pr.SourceRepository, config.FullName, StringComparison.OrdinalIgnoreCase))
                    return pr;
        return null;
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct)
    {
        using var doc = await _api.SendAsync(HttpMethod.Get, $"{_api.RepoPath}/pullrequests/{id}", null, ct);
        return Map(doc.RootElement);
    }

    public async Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct)
    {
        var path = $"{_api.RepoPath}/pullrequests/{id}/merge";
        var (status, text) = await _api.SendRawAsync(HttpMethod.Post, path,
            new { merge_strategy = config.MergeStrategy }, ct);

        if (status == HttpStatusCode.Accepted)
            return new MergeResult(false, null, "merge in progress", Accepted: true);

        if ((int)status is >= 400 and < 500)
            return new MergeResult(false, null, ErrorMessage(text) ?? BitbucketApi.Excerpt(text));

        if ((int)status is < 200 or > 299)
            throw BitbucketApi.Failure(HttpMethod.Post, path, status, text);

        using var doc = JsonDocument.Parse(text);
        return new MergeResult(true, MergeHash(doc.RootElement), null);
    }

    /// <summary>Asks for commits on <paramref name="other"/> that are not on <paramref name="branch"/>; none means contained.</summary>
    public async Task<bool> BranchContainsAsync(string branch, string other, CancellationToken ct)
    {
        var path = $"{_api.RepoPath}/commits?include={Uri.EscapeDataString(other)}&exclude={Uri.EscapeDataString(branch)}&pagelen=1";
        using var doc = await _api.SendAsync(HttpMethod.Get, path, null, ct);
        return doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.GetArrayLength() == 0
            : throw new HttpRequestException($"Bitbucket GET {path} returned no commit list");
    }

    public async Task<string> GetBranchHeadAsync(string branch, CancellationToken ct)
    {
        var path = $"{_api.RepoPath}/refs/branches/{Uri.EscapeDataString(branch)}";
        using var doc = await _api.SendAsync(HttpMethod.Get, path, null, ct);
        return doc.RootElement.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
               && target.TryGetProperty("hash", out var hash) && hash.GetString() is { Length: > 0 } h
            ? h
            : throw new HttpRequestException($"Bitbucket GET {path} returned no target hash");
    }

    private string? _myAccountId;

    private async Task<string?> MyAccountIdAsync(CancellationToken ct)
    {
        if (_myAccountId is not null) return _myAccountId;
        using var doc = await _api.SendAsync(HttpMethod.Get, "user", null, ct);
        return _myAccountId = Str(doc.RootElement, "account_id") ?? "";
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(long id, CancellationToken ct)
    {
        var mine = await MyAccountIdAsync(ct);
        var comments = new List<PullRequestComment>();
        string? next = $"{_api.RepoPath}/pullrequests/{id}/comments?pagelen=100";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!seen.Add(next) || seen.Count > 200)
                throw new HttpRequestException("Bitbucket comment paging did not end (repeated or too many pages)");
            using var doc = await _api.SendAsync(HttpMethod.Get, next, null, ct);
            next = Str(doc.RootElement, "next") is { } link ? SameHostPath(link) : null;
            if (!doc.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) break;
            foreach (var c in values.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object
                    || (c.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)) continue;
                var user = c.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                var account = user.ValueKind == JsonValueKind.Object ? Str(user, "account_id") : null;
                string? path = null;
                int? line = null;
                if (c.TryGetProperty("inline", out var inline) && inline.ValueKind == JsonValueKind.Object)
                {
                    path = Str(inline, "path");
                    line = Int(inline, "to") ?? Int(inline, "from");
                }

                var parent = c.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object
                             && p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number
                    ? pid.GetInt64().ToString() : null;
                var raw = c.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                    ? Str(content, "raw") : null;
                comments.Add(new PullRequestComment(c.GetProperty("id").GetInt64().ToString(),
                    (user.ValueKind == JsonValueKind.Object ? Str(user, "display_name") : null) ?? "",
                    !string.IsNullOrEmpty(mine) && account == mine,
                    DateTimeOffset.TryParse(Str(c, "created_on"), out var created) ? created : DateTimeOffset.MinValue,
                    raw ?? "", path, line, parent));
            }
        }

        return [.. comments.OrderBy(c => c.Created)];
    }

    /// <summary>
    /// The path and query of a paging link, which must be https on api.bitbucket.org: the request carries credentials,
    /// so a link naming any other host is refused (throws) rather than followed.
    /// </summary>
    private static string SameHostPath(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "api.bitbucket.org", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort
            ? uri.PathAndQuery
            : throw new HttpRequestException("Bitbucket paging link names a foreign host; refusing to follow it");

    public async Task CommentOnPullRequestAsync(long id, string markdown, CancellationToken ct)
    {
        using var _ = await _api.SendAsync(HttpMethod.Post, $"{_api.RepoPath}/pullrequests/{id}/comments",
            new { content = new { raw = markdown } }, ct);
    }

    public async Task ReplyToPullRequestCommentAsync(long id, PullRequestComment comment, string markdown, CancellationToken ct)
    {
        using var _ = await _api.SendAsync(HttpMethod.Post, $"{_api.RepoPath}/pullrequests/{id}/comments",
            new { content = new { raw = markdown }, parent = new { id = long.Parse(comment.Id) } }, ct);
    }

    private static string? Str(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>The web page of pull request <paramref name="id"/> (no API call needed).</summary>
    public static Uri PullRequestUrl(BitbucketRepoConfig repo, long id) =>
        new($"https://bitbucket.org/{Uri.EscapeDataString(repo.Workspace)}/{Uri.EscapeDataString(repo.Slug)}/pull-requests/{id}");

    public Uri BranchFileUrl(string branch, string path) =>
        new($"https://bitbucket.org/{Uri.EscapeDataString(config.Workspace)}/{Uri.EscapeDataString(config.Slug)}/src/{branch}/{path}");

    private const string NoChangesText = "There are no changes to be pulled";

    private static string? NoChangesMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                   && type.GetString() == "error"
                   && ErrorMessage(text) is { } message && message.StartsWith(NoChangesText, StringComparison.Ordinal)
                ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? MergeHash(JsonElement pr) =>
        pr.TryGetProperty("merge_commit", out var mc) && mc.ValueKind == JsonValueKind.Object
        && mc.TryGetProperty("hash", out var h) ? h.GetString() : null;

    private static PullRequestInfo Map(JsonElement pr)
    {
        var state = pr.GetProperty("state").GetString() switch
        {
            "OPEN" => PullRequestState.Open,
            "MERGED" => PullRequestState.Merged,
            _ => PullRequestState.Declined, // DECLINED, SUPERSEDED
        };
        var url = pr.GetProperty("links").GetProperty("html").GetProperty("href").GetString()!;
        return new PullRequestInfo(pr.GetProperty("id").GetInt64(), new Uri(url), state, MergeHash(pr),
            SourceString(pr, "repository", "full_name"), SourceString(pr, "commit", "hash"));
    }

    /// <summary><c>source.{object}.{property}</c> when it is a string; null when missing or malformed.</summary>
    private static string? SourceString(JsonElement pr, string obj, string property) =>
        pr.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
        && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
