using System.Net;
using System.Text;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;
using Symphonist.Tests.Http;
using Symphonist.Integrations;

namespace Symphonist.Integrations.Tests;

public class IntegrationFactoryTests
{
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpMessageHandler? Handler { get; init; }
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        }
    }

    private sealed class DictionarySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public List<string> Reads { get; } = [];
        public string? Get(string name)
        {
            Reads.Add(name);
            return Values.GetValueOrDefault(name);
        }
        public void Set(string name, string value) => Values[name] = value;
        public void Remove(string name) => Values.Remove(name);
    }

    private readonly FakeHttpClientFactory http = new();
    private readonly DictionarySecretStore secrets = new();

    private static ProjectConfig Project(string id = "orders", string trackerSecret = "jira-acme",
        string repoSecret = "bitbucket-acme") => new(id, "Orders",
        new JiraTrackerConfig("jira", trackerSecret, new Uri("https://acme.atlassian.net"), "dev@example.com", "labels = Claude"),
        new BitbucketRepoConfig("bitbucket", "/src/orders", "main", "production", repoSecret, "acme", "orders",
            "dev@example.com", "merge_commit"),
        new BitbucketPipelinesConfig("bitbucket-pipelines", 45),
        new AgentSettings("claude-code", Isolation.Container, [], [], 60, null, null),
        3, [new StageDefinition("done", StageKind.Done, GatePolicy.None, [], null, null, null)],
        new Dictionary<StageKind, string>(), "/cfg");

    [Fact]
    public void Creates_jira_and_bitbucket_integrations_from_named_clients()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");

        var io = new IntegrationFactory(http, secrets, BuiltInProviders.Registry).Create(Project());

        Assert.IsType<JiraTracker>(io.Tracker);
        Assert.IsType<BitbucketSourceControl>(io.SourceControl);
        Assert.IsType<BitbucketPipelinesWatcher>(io.BuildWatcher);
        Assert.Equal(["jira", "bitbucket", "bitbucket"], http.Names);
    }

    [Fact]
    public void Missing_secret_names_the_command_to_set_it()
    {
        secrets.Set("jira-acme", "j");
        var ex = Assert.Throws<InvalidOperationException>(() => new IntegrationFactory(http, secrets, BuiltInProviders.Registry).Create(Project()));
        Assert.Equal("secret 'bitbucket-acme' not set; run: symphonist secret set bitbucket-acme", ex.Message);
    }

    [Fact]
    public void Caches_per_project_id_until_the_config_changes()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var config = Project();

        var first = factory.Create(config);
        Assert.Same(first, factory.Create(config));
        Assert.NotSame(first, factory.Create(Project("billing")));
        Assert.NotSame(first, factory.Create(Project())); // reloaded config for the same id
    }

    [Fact]
    public async Task New_secret_value_is_used_without_restart()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, "{\"issues\":[]}")
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, "{\"accountId\":\"me\"}");
        var factory = new IntegrationFactory(new FakeHttpClientFactory { Handler = stub }, secrets, BuiltInProviders.Registry);
        var config = Project();
        secrets.Set("jira-acme", "old");
        secrets.Set("bitbucket-acme", "b");
        var io1 = factory.Create(config);

        secrets.Set("jira-acme", "new");
        var io2 = factory.Create(config);

        Assert.NotSame(io1, io2);
        await io2.Tracker.QueryAsync(CancellationToken.None);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dev@example.com:new"));
        Assert.NotEmpty(stub.Authorizations);
        Assert.All(stub.Authorizations, a => Assert.Equal(expected, a));
    }

    [Fact]
    public void Unchanged_secrets_reuse_integrations()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var config = Project();

        Assert.Same(factory.Create(config), factory.Create(config));
    }

    [Fact]
    public void Changed_tracker_secret_rebuilds_only_the_tracker()
    {
        secrets.Set("jira-acme", "old");
        secrets.Set("bitbucket-acme", "b");
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var config = Project();
        var io1 = factory.Create(config);

        secrets.Set("jira-acme", "new");
        var io2 = factory.Create(config);

        Assert.NotSame(io1.Tracker, io2.Tracker);
        Assert.Same(io1.SourceControl, io2.SourceControl);
        Assert.Same(io1.BuildWatcher, io2.BuildWatcher);
    }

    [Fact]
    public void Shared_secret_is_read_once()
    {
        secrets.Set("shared", "s");
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var config = Project(trackerSecret: "shared", repoSecret: "shared");

        factory.Create(config);
        Assert.Equal(["shared"], secrets.Reads);
        factory.Create(config);
        Assert.Equal(["shared", "shared"], secrets.Reads);
    }

    [Fact]
    public void Removed_secret_is_reported()
    {
        secrets.Set("jira-acme", "j");
        secrets.Set("bitbucket-acme", "b");
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var config = Project();
        factory.Create(config);

        secrets.Remove("jira-acme");

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(config));
        Assert.Equal("secret 'jira-acme' not set; run: symphonist secret set jira-acme", ex.Message);
    }

    [Fact]
    public void Pull_request_url_points_at_bitbucket()
    {
        Assert.Equal(new Uri("https://bitbucket.org/acme/orders/pull-requests/42"),
            BitbucketSourceControl.PullRequestUrl((BitbucketRepoConfig)Project().Repo, 42));
    }
}
