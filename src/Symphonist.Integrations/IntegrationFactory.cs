using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations;

/// <summary>
/// Builds a project's tracker, source control and build watcher through the providers its configuration names, from
/// each provider's named HTTP client and the project's secrets. The build watcher uses the repository's token and HTTP
/// client. Cached per project id, per integration: each is rebuilt only when the configuration section it was built
/// from or its token changes. A missing secret is reported, never cached.
/// </summary>
public sealed class IntegrationFactory(IHttpClientFactory http, ISecretStore secrets, ProviderRegistry providers,
    ILoggerFactory? loggers = null) : IIntegrationFactory
{
    private readonly ILoggerFactory _loggers = loggers ?? NullLoggerFactory.Instance;

    private sealed record Slot<T>(object[] Configs, string Token, T Value) where T : class
    {
        public bool Matches(object[] configs, string token) =>
            string.Equals(Token, token, StringComparison.Ordinal)
            && Configs.Length == configs.Length && Configs.Zip(configs).All(c => ReferenceEquals(c.First, c.Second));
    }

    private sealed record Entry(Slot<ITracker> Tracker, Slot<ISourceControl> Repo, Slot<IBuildWatcher> Build,
        ProjectIntegrations Io);

    private readonly ConcurrentDictionary<string, Entry> cache = new(StringComparer.Ordinal);

    public ProjectIntegrations Create(ProjectConfig project)
    {
        // Secrets are read on every call so a changed or removed secret takes effect without a restart.
        var trackerToken = Secret(project.Tracker.TokenSecret);
        var repoToken = project.Repo.TokenSecret == project.Tracker.TokenSecret
            ? trackerToken
            : Secret(project.Repo.TokenSecret);

        cache.TryGetValue(project.Id, out var cached);
        var repoProvider = providers.Repo(project.Repo.Type);

        var tracker = Reuse(cached?.Tracker, [project.Tracker], trackerToken, () =>
        {
            var provider = providers.Tracker(project.Tracker.Type);
            return provider.Create(project, http.CreateClient(provider.HttpClientName), trackerToken, _loggers);
        });
        var repo = Reuse(cached?.Repo, [project.Repo], repoToken,
            () => repoProvider.Create(project, http.CreateClient(repoProvider.HttpClientName), repoToken));
        // The watcher reads the repository's configuration too, so it is rebuilt when either section changes.
        var build = Reuse(cached?.Build, [project.Build, project.Repo], repoToken,
            () => providers.Build(project.Build.Type)
                .Create(project, http.CreateClient(repoProvider.HttpClientName), repoToken));

        if (cached is not null && ReferenceEquals(tracker, cached.Tracker) && ReferenceEquals(repo, cached.Repo)
            && ReferenceEquals(build, cached.Build))
            return cached.Io;

        var io = new ProjectIntegrations(tracker.Value, repo.Value, build.Value);
        cache[project.Id] = new Entry(tracker, repo, build, io);
        return io;
    }

    private static Slot<T> Reuse<T>(Slot<T>? slot, object[] configs, string token, Func<T> create) where T : class =>
        slot is not null && slot.Matches(configs, token) ? slot : new Slot<T>(configs, token, create());

    private string Secret(string name) =>
        secrets.Get(name) ?? throw new InvalidOperationException($"secret '{name}' not set; run: symphonist secret set {name}");
}
