using System.Globalization;
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

    private sealed class Dto;
}
