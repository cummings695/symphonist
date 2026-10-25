using System.Text.Json;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.GitHub;

public sealed class GitHubActionsWatcher(HttpClient http, GitHubRepoConfig repo, GitHubActionsConfig config, string token)
    : IBuildWatcher
{
    private const int PageSize = 100;

    private static readonly string[] Passing = ["success", "skipped", "neutral"];

    private readonly GitHubApi _api = new(http, token);

    private sealed record Run(string File, DateTimeOffset Created, bool Completed, string? Conclusion);

    public async Task<BuildStatus> GetStatusAsync(string commitHash, BuildPurpose purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commitHash)) return BuildStatus.NotFound;

        // Latest run per workflow file: a re-run replaces the earlier result.
        var latest = new Dictionary<string, Run>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; ; page++)
        {
            using var doc = await _api.SendAsync(HttpMethod.Get,
                $"repos/{repo.Owner}/{repo.Repo}/actions/runs?head_sha={Uri.EscapeDataString(commitHash)}&per_page={PageSize}&page={page}",
                null, ct);
            var count = 0;
            if (doc.RootElement.TryGetProperty("workflow_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in runs.EnumerateArray())
                {
                    count++;
                    var run = ReadRun(element);
                    if (!latest.TryGetValue(run.File, out var existing) || run.Created > existing.Created)
                        latest[run.File] = run;
                }
            }

            if (count < PageSize) break;
        }

        var listed = purpose == BuildPurpose.Deploy ? config.DeployWorkflows : config.BuildWorkflows;
        IEnumerable<Run> relevant = latest.Values;
        var notStarted = false;
        if (listed.Count > 0)
        {
            relevant = latest.Values.Where(r => listed.Contains(r.File, StringComparer.OrdinalIgnoreCase)).ToList();
            notStarted = listed.Any(w => !latest.ContainsKey(w));
        }

        var all = relevant.ToList();
        // Listed workflows not started yet: Running while other runs exist for the commit, NotFound while none do.
        if (all.Count == 0) return notStarted && latest.Count > 0 ? BuildStatus.Running : BuildStatus.NotFound;
        if (all.Any(r => !r.Completed)) return BuildStatus.Running;
        if (all.Any(r => !Passing.Contains(r.Conclusion))) return BuildStatus.Failed;
        return notStarted ? BuildStatus.Running : BuildStatus.Succeeded;
    }

    private static Run ReadRun(JsonElement run)
    {
        var path = run.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var created = run.TryGetProperty("created_at", out var c) && c.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.MinValue;
        var status = run.TryGetProperty("status", out var s) ? s.GetString() : null;
        var conclusion = run.TryGetProperty("conclusion", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return new Run(Path.GetFileName(path), created, status == "completed", conclusion);
    }
}
