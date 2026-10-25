using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations.Providers;

public sealed class ProviderRegistry(IEnumerable<ITrackerProvider> trackers, IEnumerable<IRepoProvider> repos,
    IEnumerable<IBuildProvider> builds)
{
    private readonly Dictionary<string, ITrackerProvider> _trackers = trackers.ToDictionary(p => p.Type, StringComparer.Ordinal);
    private readonly Dictionary<string, IRepoProvider> _repos = repos.ToDictionary(p => p.Type, StringComparer.Ordinal);
    private readonly Dictionary<string, IBuildProvider> _builds = builds.ToDictionary(p => p.Type, StringComparer.Ordinal);

    public ITrackerProvider Tracker(string type) => Find(_trackers, "tracker", type);

    public IRepoProvider Repo(string type) => Find(_repos, "repo", type);

    public IBuildProvider Build(string type) => Find(_builds, "build", type);

    /// <summary>The distinct named HTTP clients the tracker and repo providers need, in ordinal order.</summary>
    public IReadOnlyList<string> HttpClientNames { get; } = trackers.Select(p => p.HttpClientName)
        .Concat(repos.Select(p => p.HttpClientName))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static T Find<T>(Dictionary<string, T> providers, string kind, string type) =>
        providers.TryGetValue(type, out var provider)
            ? provider
            : throw new ProjectConfigException(
                $"unsupported {kind}.type '{type}' (supported: {string.Join(", ", providers.Keys.Order(StringComparer.Ordinal))})");
}
