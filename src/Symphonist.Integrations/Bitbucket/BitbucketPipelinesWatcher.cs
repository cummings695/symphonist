using System.Text.Json;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Bitbucket;

public sealed class BitbucketPipelinesWatcher(HttpClient http, BitbucketRepoConfig config, string apiToken) : IBuildWatcher
{
    private readonly BitbucketApi _api = new(http, config, apiToken);

    public async Task<BuildStatus> GetStatusAsync(string commitHash, BuildPurpose purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commitHash)) return BuildStatus.NotFound;

        using var doc = await _api.SendAsync(HttpMethod.Get,
            $"{_api.RepoPath}/pipelines/?sort=-created_on&pagelen=50", null, ct);
        if (!doc.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return BuildStatus.NotFound;

        foreach (var p in values.EnumerateArray())
        {
            var hash = p.TryGetProperty("target", out var t) && t.TryGetProperty("commit", out var c)
                && c.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hash)) continue;
            // Prefix match in either direction: callers may hold a short or full hash.
            if (!hash.StartsWith(commitHash, StringComparison.OrdinalIgnoreCase)
                && !commitHash.StartsWith(hash, StringComparison.OrdinalIgnoreCase)) continue;
            return Map(p);
        }

        return BuildStatus.NotFound;
    }

    private static BuildStatus Map(JsonElement pipeline)
    {
        var state = pipeline.GetProperty("state");
        var name = state.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (name is "PENDING" or "IN_PROGRESS") return BuildStatus.Running;
        var result = state.TryGetProperty("result", out var r) && r.TryGetProperty("name", out var rn)
            ? rn.GetString() : null;
        return name == "COMPLETED" && result == "SUCCESSFUL" ? BuildStatus.Succeeded : BuildStatus.Failed;
    }
}
