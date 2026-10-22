using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Integrations.Tests;

public class IntegrationFactoryTests
{
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient();
        }
    }

    private sealed class DictionarySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Remove(string name) => Values.Remove(name);
    }

    private readonly FakeHttpClientFactory http = new();
    private readonly DictionarySecretStore secrets = new();

    private static ProjectConfig Project(string id = "orders") => new(id, "Orders",
        new TrackerConfig("jira", new Uri("https://acme.atlassian.net"), "dev@example.com", "jira-acme", "labels = Claude"),
        new RepoConfig("bitbucket", "acme", "orders", "/src/orders", "main", "production", "dev@example.com",
            "bitbucket-acme", "merge_commit"),
        new BuildConfig("bitbucket-pipelines", 45),
        new AgentSettings("claude-code", Isolation.Container, [], [], 60, null, null),
        3, [new StageDefinition("done", StageKind.Done, GatePolicy.None, [], null, null, null)],
        new Dictionary<StageKind, string>(), "/cfg");

    [Fact]
    public void Creates_jira_and_bitbucket_integrations_from_named_clients()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");

        var io = new IntegrationFactory(http, secrets).Create(Project());

        Assert.IsType<JiraTracker>(io.Tracker);
        Assert.IsType<BitbucketSourceControl>(io.SourceControl);
        Assert.IsType<BitbucketPipelinesWatcher>(io.BuildWatcher);
        Assert.Equal(["jira", "bitbucket", "bitbucket"], http.Names);
    }

    [Fact]
    public void Missing_secret_names_the_command_to_set_it()
    {
        secrets.Set("jira-acme", "j");
        var ex = Assert.Throws<InvalidOperationException>(() => new IntegrationFactory(http, secrets).Create(Project()));
        Assert.Equal("secret 'bitbucket-acme' not set; run: symphonist secret set bitbucket-acme", ex.Message);
    }

    [Fact]
    public void Caches_per_project_id_until_the_config_changes()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");
        var factory = new IntegrationFactory(http, secrets);
        var config = Project();

        var first = factory.Create(config);
        Assert.Same(first, factory.Create(config));
        Assert.NotSame(first, factory.Create(Project("billing")));
        Assert.NotSame(first, factory.Create(Project())); // reloaded config for the same id
    }

    [Fact]
    public void Pull_request_url_points_at_bitbucket()
    {
        Assert.Equal(new Uri("https://bitbucket.org/acme/orders/pull-requests/42"),
            BitbucketSourceControl.PullRequestUrl(Project().Repo, 42));
    }
}
