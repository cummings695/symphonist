using Microsoft.Extensions.Logging;
using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations.Providers;

public interface ITrackerProvider
{
    string Type { get; }

    /// <summary>Human name for the dashboard source badge, e.g. "Jira" or "GitHub".</summary>
    string DisplayName { get; }

    string HttpClientName { get; }

    TrackerConfig Parse(TrackerSection section);

    /// <summary>Every key the section accepts, shared keys included, in writing order.</summary>
    IReadOnlyList<ProviderField> Fields { get; }

    /// <summary>
    /// Key -> string (Number as invariant text, Path absolute) or <c>IReadOnlyList&lt;string&gt;</c> (List); absent
    /// optional values are omitted.
    /// </summary>
    IReadOnlyDictionary<string, object?> ToValues(TrackerConfig config);

    ITracker Create(ProjectConfig project, HttpClient http, string token, ILoggerFactory loggers);

    /// <summary>
    /// Read-only: the signed-in account, the status source's statuses and an approximate ticket count. Throws
    /// <see cref="HttpRequestException"/> (or a subclass) on sign-in or source failures.
    /// </summary>
    Task<TrackerDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string token, CancellationToken ct);

    Uri? IssueUrl(ProjectConfig project, string key);
}

public interface IRepoProvider
{
    string Type { get; }

    /// <summary>Human name for the setup screen, e.g. "Bitbucket" or "GitHub".</summary>
    string DisplayName { get; }

    string HttpClientName { get; }

    RepoConfig Parse(RepoSection section);

    /// <summary>Every key the section accepts, shared keys included, in writing order.</summary>
    IReadOnlyList<ProviderField> Fields { get; }

    /// <summary>See <see cref="ITrackerProvider.ToValues"/>.</summary>
    IReadOnlyDictionary<string, object?> ToValues(RepoConfig config);

    /// <summary>The two keys that form <see cref="RepoConfig.FullName"/>, in order, e.g. workspace and slug.</summary>
    IReadOnlyList<string> FullNameKeys { get; }

    ISourceControl Create(ProjectConfig project, HttpClient http, string token);

    /// <summary>Read-only: the repository's name, main branch and whether the production branch exists.</summary>
    Task<RepositoryDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string token, CancellationToken ct);

    Uri PullRequestUrl(ProjectConfig project, long id);
}

public interface IBuildProvider
{
    string Type { get; }

    /// <summary>Human name for the setup screen, e.g. "GitHub Actions".</summary>
    string DisplayName { get; }

    /// <summary>The repo provider type whose HTTP client and token this build provider reuses.</summary>
    string RepoType { get; }

    BuildConfig Parse(BuildSection section);

    /// <summary>Every key the section accepts, shared keys included, in writing order.</summary>
    IReadOnlyList<ProviderField> Fields { get; }

    /// <summary>See <see cref="ITrackerProvider.ToValues"/>.</summary>
    IReadOnlyDictionary<string, object?> ToValues(BuildConfig config);

    /// <param name="http">The repo provider's client.</param>
    IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken);

    /// <summary>Read-only: a summary of the build host's state for the repository.</summary>
    /// <param name="http">The repo provider's client, as in <see cref="Create"/>.</param>
    Task<BuildDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string repoToken, CancellationToken ct);
}
