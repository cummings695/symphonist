using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations;

/// <summary>
/// Runs a draft project's read-only provider discovery. Each slot resolves its provider, named HTTP client and secret
/// as <see cref="IntegrationFactory"/> does (the build provider uses the repository's client and token), but nothing
/// is cached and the factory is never touched: a draft never becomes a live integration.
/// </summary>
public sealed class ProjectDiscovery(IHttpClientFactory http, ISecretStore secrets, ProviderRegistry providers)
    : IProjectDiscovery
{
    public async Task<TrackerDiscovery> DiscoverTrackerAsync(ProjectConfig draft, CancellationToken ct)
    {
        var provider = providers.Tracker(draft.Tracker.Type);
        var token = Secret(draft.Tracker.TokenSecret);
        return await provider.DiscoverAsync(draft, http.CreateClient(provider.HttpClientName), token, ct);
    }

    public async Task<RepositoryDiscovery> DiscoverRepositoryAsync(ProjectConfig draft, CancellationToken ct)
    {
        var provider = providers.Repo(draft.Repo.Type);
        var token = Secret(draft.Repo.TokenSecret);
        return await provider.DiscoverAsync(draft, http.CreateClient(provider.HttpClientName), token, ct);
    }

    public async Task<BuildDiscovery> DiscoverBuildAsync(ProjectConfig draft, CancellationToken ct)
    {
        var provider = providers.Build(draft.Build.Type);
        var repoProvider = providers.Repo(draft.Repo.Type);
        var token = Secret(draft.Repo.TokenSecret);
        return await provider.DiscoverAsync(draft, http.CreateClient(repoProvider.HttpClientName), token, ct);
    }

    private string Secret(string name) =>
        secrets.Get(name) ?? throw new InvalidOperationException($"secret '{name}' not set; run: symphonist secret set {name}");
}
