using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations.Providers;

public interface ITrackerProvider
{
    string Type { get; }

    /// <summary>Human name for the dashboard source badge, e.g. "Jira" or "GitHub".</summary>
    string DisplayName { get; }

    string HttpClientName { get; }

    TrackerConfig Parse(TrackerSection section);

    ITracker Create(ProjectConfig project, HttpClient http, string token);

    Uri? IssueUrl(ProjectConfig project, string key);
}

public interface IRepoProvider
{
    string Type { get; }

    string HttpClientName { get; }

    RepoConfig Parse(RepoSection section);

    ISourceControl Create(ProjectConfig project, HttpClient http, string token);

    Uri PullRequestUrl(ProjectConfig project, long id);
}

public interface IBuildProvider
{
    string Type { get; }

    /// <summary>The repo provider type whose HTTP client and token this build provider reuses.</summary>
    string RepoType { get; }

    BuildConfig Parse(BuildSection section);

    /// <param name="http">The repo provider's client.</param>
    IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken);
}
