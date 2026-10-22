using System.Net;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Bitbucket;

public sealed class BitbucketSourceControl(HttpClient http, RepoConfig config, string apiToken) : ISourceControl
{
    private readonly BitbucketApi _api = new(http, config, apiToken);

    public async Task<PullRequestInfo> OpenPullRequestAsync(
        string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct)
    {
        using var doc = await _api.SendAsync(HttpMethod.Post, $"{_api.RepoPath}/pullrequests", new
        {
            title,
            description,
            source = new { branch = new { name = sourceBranch } },
            destination = new { branch = new { name = destinationBranch } },
            close_source_branch = false,
        }, ct);
        return Map(doc.RootElement);
    }

    public async Task<PullRequestInfo?> FindOpenPullRequestAsync(
        string sourceBranch, string destinationBranch, CancellationToken ct)
    {
        var q = $"source.branch.name=\"{sourceBranch}\" AND destination.branch.name=\"{destinationBranch}\"";
        using var doc = await _api.SendAsync(HttpMethod.Get,
            $"{_api.RepoPath}/pullrequests?state=OPEN&q={Uri.EscapeDataString(q)}", null, ct);
        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var pr in values.EnumerateArray())
                return Map(pr);
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
            return new MergeResult(false, null, "merge in progress");

        if ((int)status is >= 400 and < 500)
            return new MergeResult(false, null, ErrorMessage(text) ?? BitbucketApi.Excerpt(text));

        if ((int)status is < 200 or > 299)
            throw BitbucketApi.Failure(HttpMethod.Post, path, status, text);

        using var doc = JsonDocument.Parse(text);
        return new MergeResult(true, MergeHash(doc.RootElement), null);
    }

    public Uri BranchFileUrl(string branch, string path) =>
        new($"https://bitbucket.org/{Uri.EscapeDataString(config.Workspace)}/{Uri.EscapeDataString(config.Slug)}/src/{branch}/{path}");

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
        return new PullRequestInfo(pr.GetProperty("id").GetInt64(), new Uri(url), state, MergeHash(pr));
    }
}
