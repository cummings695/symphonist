using System.Net;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.GitHub;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.GitHub;

public class GitHubDiscoveryTests
{
    private const string GraphQl = "/graphql";
    private const string Token = "ghp_secret";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static StubHttpHandler Board(string? discover = null) => new StubHttpHandler()
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, discover ?? Fixture("gh-project-discover.json"), "query Discover")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-items-page2.json"), "\"after\":\"items-p2\"")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-items-page1.json"), "query Items");

    private static Task<TrackerDiscovery> Discover(StubHttpHandler stub, ProjectConfig? draft = null) =>
        new GitHubProjectsProvider().DiscoverAsync(draft ?? GitHubProjectsProviderTests.Project(), new HttpClient(stub),
            Token, CancellationToken.None);

    [Fact]
    public async Task Discover_reads_viewer_and_status_options_in_board_order()
    {
        var stub = Board();

        var result = await Discover(stub);

        Assert.Equal("octo-dev", result.AccountName);
        Assert.Equal("GitHub board acme #7 (Status)", result.StatusSource);
        Assert.Equal(["Backlog", "Planned", "In Progress", "In Review", "Done"], result.Statuses.Select(s => s.Name));
        Assert.All(result.Statuses, s => Assert.Equal(StatusCategory.Unknown, s.Category));
        Assert.Equal("Plan approved", result.Statuses[1].Description);
        Assert.Contains("GitHub columns have no category; the proposal infers it from names", result.Notes);

        using var discover = JsonDocument.Parse(Assert.Single(stub.Requests,
            r => r.Body!.Contains("query Discover", StringComparison.Ordinal)).Body!);
        var variables = discover.RootElement.GetProperty("variables");
        Assert.Equal("acme", variables.GetProperty("owner").GetString());
        Assert.Equal(7, variables.GetProperty("number").GetInt32());
        Assert.Equal("Status", variables.GetProperty("field").GetString());
        Assert.All(stub.Authorizations, a => Assert.Equal("Bearer " + Token, a));
    }

    [Fact]
    public async Task Discover_counts_labelled_assigned_open_issues_ignoring_status()
    {
        // Page one: draft, PR, closed, someone else's and unlabelled items, and another repository's issue, are left
        // out; the open issues in "Done" and with no status count. Page two: one more.
        var result = await Discover(Board());
        Assert.Equal(3, result.MatchingTickets);

        // A column the pipeline does not name still counts: discovery runs before the mapping is settled.
        var icebox = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-project-discover.json"), "query Discover")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, GitHubProjectsTrackerTests.ItemsPage(
                GitHubProjectsTrackerTests.Item("1", "acme", "orders", 1, status: "Icebox")), "query Items");
        Assert.Equal(1, (await Discover(icebox)).MatchingTickets);
    }

    [Fact]
    public async Task Discover_count_failure_is_null()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-project-discover.json"), "query Discover")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.BadGateway, "{}", "query Items");

        var result = await Discover(stub);

        Assert.Null(result.MatchingTickets);
        Assert.Equal(5, result.Statuses.Count);
    }

    [Fact]
    public async Task Discover_missing_status_field_throws_naming_board()
    {
        var noField = """{"data":{"repositoryOwner":{"projectV2":{"id":"PVT_kwDOA1","title":"Orders","field":null}}}}""";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Discover(Board(noField)));

        Assert.Equal("board acme #7 has no single-select field 'Status'", ex.Message);
    }

    [Fact]
    public async Task Discover_missing_board_throws_naming_it()
    {
        var noBoard = """{"data":{"repositoryOwner":{"projectV2":null}}}""";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Discover(Board(noBoard)));

        Assert.Contains("acme #7", ex.Message);
    }

    [Fact]
    public async Task Discover_sends_no_mutations()
    {
        var stub = Board();

        await Discover(stub);

        Assert.NotEmpty(stub.Requests);
        Assert.All(stub.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal(GraphQl, r.PathAndQuery);
            using var body = JsonDocument.Parse(r.Body!);
            Assert.StartsWith("query", body.RootElement.GetProperty("query").GetString()!.TrimStart(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task GitHub_repo_discover_reads_full_name_default_branch_and_production(HttpStatusCode branch, bool exists)
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/repos/acme/orders/branches/production", branch, Fixture("gh-branch.json"))
            .On(HttpMethod.Get, "/repos/acme/orders", HttpStatusCode.OK, Fixture("gh-repo.json"));

        var result = await new GitHubRepoProvider().DiscoverAsync(GitHubProjectsProviderTests.Project(),
            new HttpClient(stub), Token, CancellationToken.None);

        Assert.Equal(new RepositoryDiscovery("Acme/Orders", "main", exists), result);
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task GitHub_repo_discover_throws_when_branch_check_fails()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/repos/acme/orders/branches/production", HttpStatusCode.InternalServerError, "{}")
            .On(HttpMethod.Get, "/repos/acme/orders", HttpStatusCode.OK, Fixture("gh-repo.json"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubRepoProvider().DiscoverAsync(
            GitHubProjectsProviderTests.Project(), new HttpClient(stub), Token, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Actions_discover_lists_workflows_and_flags_missing_configured_ones()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/repos/acme/orders/actions/workflows?per_page=100", HttpStatusCode.OK,
                Fixture("gh-workflows.json"));
        var project = GitHubProjectsProviderTests.Project() with
        {
            Build = new GitHubActionsConfig("github-actions", 45, ["ci.yml", "nope.yml"], ["deploy.yml"]),
        };

        var result = await new GitHubActionsProvider().DiscoverAsync(project, new HttpClient(stub), Token,
            CancellationToken.None);

        Assert.Equal(["ci.yml", "deploy.yml"], result.Workflows);
        Assert.Equal("2 workflows", result.Summary);
        Assert.Equal(["workflow 'nope.yml' not found in .github/workflows"], result.Notes);
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }
}
