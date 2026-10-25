using Microsoft.Extensions.Logging;
using Symphonist.Core.Integrations;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Core.Tests.Integrations;

public class ProviderRegistryTests
{
    private sealed class FakeTracker(string type, string client) : ITrackerProvider
    {
        public string Type => type;
        public string DisplayName => type;
        public string HttpClientName => client;
        public TrackerConfig Parse(TrackerSection section) => throw new NotSupportedException();
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyDictionary<string, object?> ToValues(TrackerConfig config) => new Dictionary<string, object?>();
        public ITracker Create(ProjectConfig project, HttpClient http, string token, ILoggerFactory loggers) => throw new NotSupportedException();
        public Uri? IssueUrl(ProjectConfig project, string key) => null;
    }

    private sealed class FakeRepo(string type, string client) : IRepoProvider
    {
        public string Type => type;
        public string HttpClientName => client;
        public string DisplayName => type;
        public RepoConfig Parse(RepoSection section) => throw new NotSupportedException();
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyDictionary<string, object?> ToValues(RepoConfig config) => new Dictionary<string, object?>();
        public IReadOnlyList<string> FullNameKeys => ["owner", "name"];
        public ISourceControl Create(ProjectConfig project, HttpClient http, string token) => throw new NotSupportedException();
        public Uri PullRequestUrl(ProjectConfig project, long id) => throw new NotSupportedException();
    }

    private sealed class FakeBuild(string type, string repoType) : IBuildProvider
    {
        public string Type => type;
        public string RepoType => repoType;
        public string DisplayName => type;
        public BuildConfig Parse(BuildSection section) => throw new NotSupportedException();
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyDictionary<string, object?> ToValues(BuildConfig config) => new Dictionary<string, object?>();
        public IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Registry_lists_providers_and_builds_for_repo()
    {
        var registry = new ProviderRegistry([new FakeTracker("jira", "j"), new FakeTracker("github-projects", "g")],
            [new FakeRepo("github", "g"), new FakeRepo("bitbucket", "b")],
            [new FakeBuild("github-actions", "github"), new FakeBuild("bitbucket-pipelines", "bitbucket")]);
        Assert.Equal(["github-projects", "jira"], registry.AllTrackers.Select(p => p.Type));
        Assert.Equal(["bitbucket", "github"], registry.AllRepos.Select(p => p.Type));
        Assert.Equal(["bitbucket-pipelines", "github-actions"], registry.AllBuilds.Select(p => p.Type));
        Assert.Equal(["github-actions"], registry.BuildsFor("github").Select(p => p.Type));
        Assert.Empty(registry.BuildsFor("nope"));
    }

    [Fact]
    public void Unknown_tracker_type_lists_supported()
    {
        var registry = new ProviderRegistry([new FakeTracker("fake-b", "b"), new FakeTracker("fake-a", "a")], [], []);
        var ex = Assert.Throws<ProjectConfigException>(() => registry.Tracker("x"));
        Assert.Equal("unsupported tracker.type 'x' (supported: fake-a, fake-b)", ex.Message);
        Assert.Equal("fake-a", registry.Tracker("fake-a").Type);
    }

    [Fact]
    public void Unknown_repo_type_is_reported()
    {
        var registry = new ProviderRegistry([], [new FakeRepo("fake-r", "r")], []);
        var ex = Assert.Throws<ProjectConfigException>(() => registry.Repo("x"));
        Assert.Equal("unsupported repo.type 'x' (supported: fake-r)", ex.Message);
    }

    [Fact]
    public void Http_client_names_are_distinct()
    {
        var registry = new ProviderRegistry([new FakeTracker("a", "shared"), new FakeTracker("b", "alpha")],
            [new FakeRepo("r", "shared")], []);
        Assert.Equal(["alpha", "shared"], registry.HttpClientNames);
    }
}
