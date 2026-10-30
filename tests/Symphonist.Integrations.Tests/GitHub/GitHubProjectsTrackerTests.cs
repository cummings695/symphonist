using System.Net;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Integrations.GitHub;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.GitHub;

public class GitHubProjectsTrackerTests
{
    private const string GraphQl = "/graphql";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static GitHubProjectsTracker Tracker(StubHttpHandler stub, ProjectConfig? project = null)
    {
        project ??= GitHubProjectsProviderTests.Project();
        return new GitHubProjectsTracker(new HttpClient(stub), (GitHubProjectsTrackerConfig)project.Tracker, "ghp_secret",
            GitHubProjectsProvider.PickupStatuses(project), GitHubProjectsProvider.RequiredStatuses(project),
            GitHubProjectsProvider.Repositories(project));
    }

    private static StubHttpHandler Board() => new StubHttpHandler()
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-project.json"), "query Project")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-items-page2.json"), "\"after\":\"items-p2\"")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-items-page1.json"), "query Items")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-comments-page2.json"), "\"after\":\"comments-p2\"")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-comments-page1.json"), "query Comments");

    private static List<JsonElement> Bodies(StubHttpHandler stub, string operation) =>
        stub.Requests.Where(r => r.Body?.Contains(operation, StringComparison.Ordinal) ?? false)
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.Clone()).ToList();

    internal static string Item(string id, string repoOwner, string repoName, int number, string status = "Planned") =>
        "{\"id\":\"PVTI_" + id + "\",\"fieldValueByName\":{\"name\":\"" + status + "\"},\"content\":{"
        + "\"__typename\":\"Issue\",\"id\":\"I_" + id + "\",\"number\":" + number + ",\"title\":\"T\",\"body\":\"B\","
        + "\"url\":\"https://github.com/" + repoOwner + "/" + repoName + "/issues/" + number + "\",\"state\":\"OPEN\","
        + "\"repository\":{\"name\":\"" + repoName + "\",\"nameWithOwner\":\"" + repoOwner + "/" + repoName + "\"},"
        + "\"assignees\":{\"nodes\":[{\"login\":\"octo-dev\"}]},\"labels\":{\"nodes\":[{\"name\":\"Claude\"}]}}}";

    internal static string ItemsPage(params string[] items) =>
        "{\"data\":{\"node\":{\"items\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":["
        + string.Join(',', items) + "]}}}}";

    internal const string NoComments =
        """{"data":{"node":{"comments":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}""";

    [Fact]
    public async Task Query_keeps_open_assigned_labelled_issues_in_pickup_statuses()
    {
        var tickets = await Tracker(Board()).QueryAsync(CancellationToken.None);

        Assert.Equal(["orders-12"], tickets.Select(t => t.Key));
    }

    [Fact]
    public async Task Query_maps_ticket_fields()
    {
        var ticket = Assert.Single(await Tracker(Board()).QueryAsync(CancellationToken.None));

        Assert.Equal("Add login throttling", ticket.Title);
        Assert.Equal("Throttle failed logins.\n\nAfter five attempts, lock for 15 minutes.", ticket.Description);
        Assert.Equal("Planned", ticket.Status);
        Assert.Equal(new Uri("https://github.com/acme/orders/issues/12"), ticket.WebUrl);
    }

    [Fact]
    public async Task Query_sends_github_headers_and_variables()
    {
        var stub = Board();
        await Tracker(stub).QueryAsync(CancellationToken.None);

        Assert.All(stub.Requests, r => Assert.Equal("/graphql", r.PathAndQuery));
        Assert.All(stub.Authorizations, a => Assert.Equal("Bearer ghp_secret", a));
        var project = Assert.Single(Bodies(stub, "query Project")).GetProperty("variables");
        Assert.Equal("acme", project.GetProperty("owner").GetString());
        Assert.Equal(7, project.GetProperty("number").GetInt32());
        Assert.Equal("Status", project.GetProperty("field").GetString());
        var items = Bodies(stub, "query Items")[0].GetProperty("variables");
        Assert.Equal("PVT_kwDOA1", items.GetProperty("project").GetString());
        Assert.Equal("I_12", Bodies(stub, "query Comments")[0].GetProperty("variables").GetProperty("issue").GetString());
    }

    [Fact]
    public async Task Query_reads_all_item_pages()
    {
        var stub = Board();
        await Tracker(stub).QueryAsync(CancellationToken.None);

        var pages = Bodies(stub, "query Items");
        Assert.Equal(2, pages.Count);
        Assert.Equal(JsonValueKind.Null, pages[0].GetProperty("variables").GetProperty("after").ValueKind);
        Assert.Equal("items-p2", pages[1].GetProperty("variables").GetProperty("after").GetString());
    }

    [Fact]
    public async Task Viewer_is_read_once()
    {
        var stub = Board();
        var tracker = Tracker(stub);
        await tracker.QueryAsync(CancellationToken.None);
        await tracker.QueryAsync(CancellationToken.None);

        Assert.Single(Bodies(stub, "query Viewer"));
        Assert.Equal(2, Bodies(stub, "query Project").Count);
    }

    [Fact]
    public async Task Comments_read_all_pages()
    {
        var stub = Board();
        var ticket = Assert.Single(await Tracker(stub).QueryAsync(CancellationToken.None));

        Assert.Equal(2, Bodies(stub, "query Comments").Count);
        Assert.Equal(101, ticket.Comments.Count);
        Assert.Equal(ticket.Comments.OrderBy(c => c.Created), ticket.Comments);
        var first = ticket.Comments[0];
        Assert.Equal("Earliest comment, on the last page.", first.Body);
        Assert.Equal("pat-product", first.Author);
        Assert.False(first.IsMine);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), first.Created);
        var mine = ticket.Comments.Single(c => c.Body == "Comment 2");
        Assert.True(mine.IsMine);
        Assert.Equal("octo-dev", mine.Author);
        var ghost = ticket.Comments.Single(c => c.Body == "Comment 50");
        Assert.Equal("ghost", ghost.Author);
        Assert.False(ghost.IsMine);
    }

    [Fact]
    public async Task Duplicate_keys_are_dropped()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-project.json"), "query Project")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, ItemsPage(
                Item("a5", "acme", "orders", 5), Item("o5", "other", "orders", 5), Item("a6", "acme", "orders", 6)),
                "query Items")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, NoComments, "query Comments");
        var project = GitHubProjectsProviderTests.Project(repo: new Symphonist.Integrations.Bitbucket.BitbucketRepoConfig(
            "bitbucket", "/work/orders", "main", "production", "t", "acme", "orders", "dev@example.com", "squash"));

        var tickets = await Tracker(stub, project).QueryAsync(CancellationToken.None);

        Assert.Equal(["orders-6"], tickets.Select(t => t.Key));
        Assert.Single(Bodies(stub, "query Comments"));
    }

    [Fact]
    public async Task No_pickup_statuses_throws_before_calling_github()
    {
        var stub = Board();
        var project = GitHubProjectsProviderTests.Project(pipeline:
        [
            new("triage", StageKind.Triage, GatePolicy.Human, [], null, null, null),
            new("done", StageKind.Done, GatePolicy.None, [], null, null, null),
        ]);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Tracker(stub, project).QueryAsync(CancellationToken.None));

        Assert.Equal("GitHub project acme/7 has no statuses to pick up: set tracker.statuses, or name board "
            + "statuses in the pipeline (entryStatuses, onEnterStatus, onGateStatus, approvedWhenStatus)", ex.Message);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Missing_status_option_throws_naming_it()
    {
        var project = GitHubProjectsProviderTests.Project(pipeline: GitHubProjectsProviderTests.OrdersPipeline("In Progres"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Tracker(Board(), project).QueryAsync(CancellationToken.None));

        Assert.Equal("GitHub project acme/7 field 'Status' has no option(s): In Progres", ex.Message);
    }

    [Fact]
    public async Task Missing_or_non_single_select_field_throws()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK,
                """{"data":{"repositoryOwner":{"projectV2":{"id":"PVT_1","field":{}}}}}""", "query Project");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Tracker(stub).QueryAsync(CancellationToken.None));

        Assert.Equal("GitHub project acme/7 has no single-select field 'Status'", ex.Message);
    }

    [Theory]
    [InlineData("""{"data":{"repositoryOwner":null}}""")]
    [InlineData("""{"data":{"repositoryOwner":{"projectV2":null}}}""")]
    [InlineData("""{"data":{"repositoryOwner":{"projectV2":null}},"errors":[{"type":"NOT_FOUND","path":["repositoryOwner","projectV2"],"message":"Could not resolve to a ProjectV2 with the number 7."}]}""")]
    public async Task Unknown_project_throws(string response)
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, response, "query Project");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Tracker(stub).QueryAsync(CancellationToken.None));

        Assert.Equal("GitHub project acme/7 not found or not accessible", ex.Message);
    }

    [Fact]
    public async Task GraphQl_errors_in_200_throw()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-graphql-error.json"), "query Project");

        var ex = await Assert.ThrowsAsync<GitHubGraphQlException>(() => Tracker(stub).QueryAsync(CancellationToken.None));

        Assert.Contains("Your token has not been granted the required scopes to execute this query.", ex.Message);
        Assert.Contains("Field 'projectV2' doesn't exist on type 'User'", ex.Message);
    }

    [Fact]
    public async Task Http_failure_throws()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Tracker(stub).QueryAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Failed_item_page_fails_the_query()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-viewer.json"), "query Viewer")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-project.json"), "query Project")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.BadGateway, "{}", "\"after\":\"items-p2\"")
            .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, Fixture("gh-items-page1.json"), "query Items");

        await Assert.ThrowsAsync<HttpRequestException>(() => Tracker(stub).QueryAsync(CancellationToken.None));
    }

    private const string MutationOk = """{"data":{"ok":{"clientMutationId":null}}}""";

    private static StubHttpHandler BoardWithMutations() => Board()
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, MutationOk, "mutation SetStatus")
        .On(HttpMethod.Post, GraphQl, HttpStatusCode.OK, MutationOk, "mutation AddComment");

    [Fact]
    public async Task Transition_sets_single_select_option()
    {
        var stub = BoardWithMutations();
        var tracker = Tracker(stub);
        await tracker.QueryAsync(CancellationToken.None);

        Assert.True(await tracker.TransitionAsync("orders-12", "in review", CancellationToken.None));

        var vars = Assert.Single(Bodies(stub, "mutation SetStatus")).GetProperty("variables");
        Assert.Equal("PVT_kwDOA1", vars.GetProperty("project").GetString());
        Assert.Equal("PVTI_12", vars.GetProperty("item").GetString());
        Assert.Equal("PVTSSF_status", vars.GetProperty("field").GetString());
        Assert.Equal("opt-review", vars.GetProperty("option").GetString());
    }

    [Fact]
    public async Task Transition_to_unknown_option_returns_false()
    {
        var stub = BoardWithMutations();
        var tracker = Tracker(stub);
        await tracker.QueryAsync(CancellationToken.None);

        Assert.False(await tracker.TransitionAsync("orders-12", "Nowhere", CancellationToken.None));
        Assert.Empty(Bodies(stub, "mutation SetStatus"));
    }

    [Fact]
    public async Task Transition_before_query_queries_first()
    {
        var stub = BoardWithMutations();

        Assert.True(await Tracker(stub).TransitionAsync("orders-12", "In Review", CancellationToken.None));

        Assert.Equal(2, Bodies(stub, "query Items").Count);
        Assert.Single(Bodies(stub, "mutation SetStatus"));
    }

    [Fact]
    public async Task Transition_of_unknown_key_requeries_once_then_throws()
    {
        var stub = BoardWithMutations();
        var tracker = Tracker(stub);
        await tracker.QueryAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.TransitionAsync("orders-99", "In Review", CancellationToken.None));

        Assert.Equal("ticket orders-99 is not on GitHub project acme/7", ex.Message);
        Assert.Equal(2, Bodies(stub, "query Project").Count);
        Assert.Empty(Bodies(stub, "mutation SetStatus"));
    }

    [Fact]
    public async Task Comment_posts_markdown_to_issue()
    {
        var stub = BoardWithMutations();
        var tracker = Tracker(stub);
        await tracker.QueryAsync(CancellationToken.None);

        const string markdown = "## Plan\n- step *one*";
        await tracker.CommentAsync("orders-12", markdown, CancellationToken.None);

        var vars = Assert.Single(Bodies(stub, "mutation AddComment")).GetProperty("variables");
        Assert.Equal("I_12", vars.GetProperty("subject").GetString());
        Assert.Equal(markdown, vars.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Comment_on_unknown_key_requeries_once_then_throws()
    {
        var stub = BoardWithMutations();
        var tracker = Tracker(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.CommentAsync("orders-99", "hi", CancellationToken.None));

        Assert.Equal("ticket orders-99 is not on GitHub project acme/7", ex.Message);
        Assert.Single(Bodies(stub, "query Project"));
        Assert.Empty(Bodies(stub, "mutation AddComment"));
    }

    [Fact]
    public void Issue_url_uses_repository_owner()
    {
        var project = GitHubProjectsProviderTests.Project(GitHubProjectsProviderTests.TrackerConfig(repositories: ["acme/orders", "Other/Billing"]));
        var provider = new GitHubProjectsProvider();

        Assert.Equal(new Uri("https://github.com/acme/orders/issues/12"), provider.IssueUrl(project, "orders-12"));
        Assert.Equal(new Uri("https://github.com/Other/Billing/issues/3"), provider.IssueUrl(project, "billing-3"));
        Assert.Equal(new Uri("https://github.com/acme/misc-tool/issues/4"), provider.IssueUrl(project, "misc-tool-4"));
        Assert.Null(provider.IssueUrl(project, "orders"));
        Assert.Null(provider.IssueUrl(project, "orders-x"));
        Assert.Null(provider.IssueUrl(project, "-5"));
    }
}
