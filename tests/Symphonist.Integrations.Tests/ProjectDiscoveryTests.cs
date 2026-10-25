using System.Net;
using System.Text;
using Symphonist.Core.Config;
using Symphonist.Integrations.GitHub;
using Symphonist.Integrations.Jira;
using Symphonist.Tests.Http;
using static Symphonist.Integrations.Tests.IntegrationFactoryTests;

namespace Symphonist.Integrations.Tests;

public class ProjectDiscoveryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private readonly StubHttpHandler jira = new StubHttpHandler()
        .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
        .On(HttpMethod.Get, "/rest/api/3/project/ACME/statuses", HttpStatusCode.OK, Fixture("jira-project-statuses.json"))
        .On(HttpMethod.Post, "/rest/api/3/search/approximate-count", HttpStatusCode.OK, Fixture("jira-approximate-count.json"))
        .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, "{\"issues\":[]}");

    private readonly StubHttpHandler github = new StubHttpHandler()
        .On(HttpMethod.Get, "/repos/acme/orders/branches/production", HttpStatusCode.OK, Fixture("gh-branch.json"))
        .On(HttpMethod.Get, "/repos/acme/orders/actions/workflows", HttpStatusCode.OK, Fixture("gh-workflows.json"))
        .On(HttpMethod.Get, "/repos/acme/orders", HttpStatusCode.OK, Fixture("gh-repo.json"));

    private readonly FakeHttpClientFactory http = new();
    private readonly DictionarySecretStore secrets = new();

    public ProjectDiscoveryTests()
    {
        http.ByName["jira"] = jira;
        http.ByName["github"] = github;
    }

    private static ProjectConfig Project() => new("orders", "Orders",
        new JiraTrackerConfig("jira", "jira-orders", new Uri("https://acme.atlassian.net"), "dev@example.com",
            "project = ACME", "ACME"),
        new GitHubRepoConfig("github", "/src/orders", "main", "production", "github-orders", "acme", "orders", "merge"),
        new GitHubActionsConfig("github-actions", 45, ["ci.yml"], []),
        new AgentSettings("claude-code", Isolation.Container, [], [], 60, null, null),
        3, [new StageDefinition("done", StageKind.Done, GatePolicy.None, [], null, null, null)],
        new Dictionary<StageKind, string>(), "/cfg");

    private ProjectDiscovery Discovery() => new(http, secrets, BuiltInProviders.Registry);

    [Fact]
    public async Task Discovery_uses_each_slots_provider_client_and_secret()
    {
        secrets.Set("jira-orders", "j-token");
        secrets.Set("github-orders", "g-token");
        var discovery = Discovery();

        var tracker = await discovery.DiscoverTrackerAsync(Project(), CancellationToken.None);
        var repo = await discovery.DiscoverRepositoryAsync(Project(), CancellationToken.None);
        var build = await discovery.DiscoverBuildAsync(Project(), CancellationToken.None);

        Assert.Equal(["jira", "github", "github"], http.Names);
        Assert.Equal("Dana Developer", tracker.AccountName);
        Assert.Equal("Acme/Orders", repo.FullName);
        Assert.Equal(["ci.yml", "deploy.yml"], build.Workflows);
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dev@example.com:j-token"));
        Assert.All(jira.Authorizations, a => Assert.Equal(basic, a));
        Assert.Equal(3, github.Requests.Count);
        Assert.All(github.Authorizations, a => Assert.Equal("Bearer g-token", a));
    }

    [Fact]
    public async Task Missing_secret_message_matches_factory()
    {
        var factory = new IntegrationFactory(http, secrets, BuiltInProviders.Registry);
        var expected = Assert.Throws<InvalidOperationException>(() => factory.Create(Project())).Message;

        var tracker = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Discovery().DiscoverTrackerAsync(Project(), CancellationToken.None));
        Assert.Equal(expected, tracker.Message);
        Assert.Equal("secret 'jira-orders' not set; run: symphonist secret set jira-orders", tracker.Message);

        var repo = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Discovery().DiscoverRepositoryAsync(Project(), CancellationToken.None));
        Assert.Equal("secret 'github-orders' not set; run: symphonist secret set github-orders", repo.Message);
        var build = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Discovery().DiscoverBuildAsync(Project(), CancellationToken.None));
        Assert.Equal(repo.Message, build.Message);
        Assert.Empty(jira.Requests);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Discovery_never_populates_the_integration_cache()
    {
        secrets.Set("jira-orders", "j-token");
        secrets.Set("github-orders", "g-token");
        var project = Project();
        var discovery = Discovery();
        await discovery.DiscoverTrackerAsync(project, CancellationToken.None);
        await discovery.DiscoverTrackerAsync(project, CancellationToken.None);
        Assert.Equal(2, jira.Requests.Count(r => r.PathAndQuery == "/rest/api/3/myself")); // nothing cached between calls

        var io = new IntegrationFactory(http, secrets, BuiltInProviders.Registry).Create(project);
        await io.Tracker.QueryAsync(CancellationToken.None);

        Assert.Equal(["jira", "jira", "jira", "github", "github"], http.Names);
        Assert.Equal(3, jira.Requests.Count(r => r.PathAndQuery == "/rest/api/3/myself"));
    }
}
