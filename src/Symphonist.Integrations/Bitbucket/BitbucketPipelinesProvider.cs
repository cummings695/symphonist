using System.Globalization;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.Bitbucket;

public sealed record BitbucketPipelinesConfig(string Type, int TimeoutMinutes) : BuildConfig(Type, TimeoutMinutes);

/// <summary>Bitbucket Pipelines: <c>build.type: bitbucket-pipelines</c>, read through the Bitbucket repository.</summary>
public sealed class BitbucketPipelinesProvider : IBuildProvider
{
    public string Type => "bitbucket-pipelines";

    public string RepoType => "bitbucket";

    public string DisplayName => "Bitbucket Pipelines";

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new("timeoutMinutes", "Timeout (minutes)", ProviderFieldKind.Number, Default: "45", Help: "How long to wait for a build or deployment"),
    ];

    public IReadOnlyDictionary<string, object?> ToValues(BuildConfig config) => new Dictionary<string, object?>
    {
        ["timeoutMinutes"] = config.TimeoutMinutes.ToString(CultureInfo.InvariantCulture),
    };

    public BuildConfig Parse(BuildSection section)
    {
        section.Bind<Dto>(); // no fields beyond the shared ones; rejects unknown keys
        return new BitbucketPipelinesConfig(section.Type, section.TimeoutMinutes);
    }

    public IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken) =>
        new BitbucketPipelinesWatcher(http, BitbucketProvider.Config(project), repoToken);

    public async Task<BuildDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string repoToken,
        CancellationToken ct)
    {
        var api = new BitbucketApi(http, BitbucketProvider.Config(draft), repoToken);
        using var doc = await api.SendAsync(HttpMethod.Get, $"{api.RepoPath}/pipelines/?pagelen=1&sort=-created_on", null, ct);
        var latest = doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().FirstOrDefault() : default;
        if (latest.ValueKind != JsonValueKind.Object) return new BuildDiscovery("no pipeline runs yet", [], []);

        var number = latest.TryGetProperty("build_number", out var n) && n.TryGetInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture) : "?";
        var state = latest.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()! : "unknown";
        return new BuildDiscovery($"latest pipeline #{number} ({state})", [], []);
    }

    private sealed class Dto;
}
