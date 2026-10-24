using System.Net;
using System.Text;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.Bitbucket;

public class BitbucketTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly RepoConfig Repo = new("bitbucket", "acme", "orders", "/work/orders", "main", "production",
        "dev@example.com", "bb-token", "squash");

    private const string Token = "secret-token";
    private const string RepoPrefix = "/2.0/repositories/acme/orders";

    private static BitbucketSourceControl Scm(StubHttpHandler stub) => new(new HttpClient(stub), Repo, Token);

    [Fact]
    public async Task Open_pr_posts_branches_and_maps_response()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests", HttpStatusCode.Created, Fixture("bb-pr-create.json"));

        var pr = await Scm(stub).OpenPullRequestAsync("symphonist/ORD-1", "main", "ORD-1: Add login throttling", "desc", CancellationToken.None);

        Assert.Equal(42, pr.Id);
        Assert.Equal(PullRequestState.Open, pr.State);
        Assert.Equal(new Uri("https://bitbucket.org/acme/orders/pull-requests/42"), pr.WebUrl);
        Assert.Null(pr.MergeCommitHash);

        var req = Assert.Single(stub.Requests);
        using var body = JsonDocument.Parse(req.Body!);
        var root = body.RootElement;
        Assert.Equal("ORD-1: Add login throttling", root.GetProperty("title").GetString());
        Assert.Equal("desc", root.GetProperty("description").GetString());
        Assert.Equal("symphonist/ORD-1", root.GetProperty("source").GetProperty("branch").GetProperty("name").GetString());
        Assert.Equal("main", root.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString());
        Assert.False(root.GetProperty("close_source_branch").GetBoolean());

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dev@example.com:" + Token));
        Assert.Equal(expected, stub.Authorizations[0]);
    }

    [Fact]
    public async Task Open_pr_without_changes_throws_no_changes_exception()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests", HttpStatusCode.BadRequest,
            Fixture("bb-pr-no-changes.json"));

        var ex = await Assert.ThrowsAsync<NoChangesToPullException>(() =>
            Scm(stub).OpenPullRequestAsync("main", "production", "Release ORD-1", "d", CancellationToken.None));

        Assert.Equal("There are no changes to be pulled", ex.Message);
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"message\":\"source branch not found\"}}")]
    [InlineData("{\"type\":\"error\",\"error\":{\"message\":\"Rejected: there are no changes to be pulled from fork\"}}")]
    [InlineData("{\"error\":{\"message\":\"There are no changes to be pulled\"}}")] // not Bitbucket's error shape
    [InlineData("There are no changes to be pulled")] // not JSON
    public async Task Open_pr_other_400_still_throws_http_error(string body)
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests", HttpStatusCode.BadRequest, body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Scm(stub).OpenPullRequestAsync("x", "main", "t", "d", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Open_pr_no_changes_message_with_other_status_is_not_no_changes()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests", HttpStatusCode.Conflict,
            Fixture("bb-pr-no-changes.json"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Scm(stub).OpenPullRequestAsync("main", "production", "t", "d", CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"pagelen\":1,\"values\":[]}", true)]
    [InlineData("{\"pagelen\":1,\"values\":[{\"hash\":\"abc\"}],\"next\":\"https://x\"}", false)]
    public async Task Branch_contains_checks_for_commits_missing_from_it(string body, bool expected)
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/commits?", HttpStatusCode.OK, body);

        Assert.Equal(expected, await Scm(stub).BranchContainsAsync("production", "main", CancellationToken.None));
        var query = Assert.Single(stub.Requests).PathAndQuery;
        Assert.Contains("include=main", query);
        Assert.Contains("exclude=production", query);
    }

    [Fact]
    public async Task Branch_contains_throws_on_unexpected_response()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/commits?", HttpStatusCode.OK, "{\"pagelen\":1}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Scm(stub).BranchContainsAsync("production", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Branch_head_returns_target_hash()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/refs/branches/production", HttpStatusCode.OK,
            "{\"name\":\"production\",\"target\":{\"type\":\"commit\",\"hash\":\"0123abcd\"}}");

        Assert.Equal("0123abcd", await Scm(stub).GetBranchHeadAsync("production", CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(stub.Requests).Method);
    }

    [Fact]
    public async Task Find_open_pr_encodes_query()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests?state=OPEN", HttpStatusCode.OK, Fixture("bb-pr-list.json"));

        var pr = await Scm(stub).FindOpenPullRequestAsync("symphonist/ORD-1", "main", CancellationToken.None);

        Assert.Equal(42, pr!.Id);
        Assert.Equal(("acme/orders", "a1b2c3d4e5f6"), (pr.SourceRepository, pr.SourceCommit));
        Assert.Contains("q=source.repository.full_name%3D%22acme%2Forders%22", stub.Requests[0].PathAndQuery);
        Assert.Contains("source.branch.name%3D%22symphonist%2FORD-1%22", stub.Requests[0].PathAndQuery);
        Assert.Contains("destination.branch.name%3D%22main%22", stub.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Find_open_pr_ignores_a_fork_with_the_same_branch_names()
    {
        // Also when the host's query does not filter them out: a fork's PR, and one whose source repository is unknown.
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests?state=OPEN", HttpStatusCode.OK,
            Fixture("bb-pr-list-fork.json"));

        Assert.Null(await Scm(stub).FindOpenPullRequestAsync("symphonist/ORD-1", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Find_open_pr_matches_the_repository_ignoring_case()
    {
        var json = Fixture("bb-pr-list.json").Replace("\"full_name\": \"acme/orders\"", "\"full_name\": \"ACME/Orders\"");
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests?state=OPEN", HttpStatusCode.OK, json);

        Assert.Equal(42, (await Scm(stub).FindOpenPullRequestAsync("symphonist/ORD-1", "main", CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Find_open_pr_returns_null_when_none()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests", HttpStatusCode.OK,
            "{\"pagelen\":10,\"page\":1,\"size\":0,\"values\":[]}");

        Assert.Null(await Scm(stub).FindOpenPullRequestAsync("a", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Get_pr_maps_merged_state_and_hash()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests/42", HttpStatusCode.OK, Fixture("bb-pr-merged.json"));

        var pr = await Scm(stub).GetPullRequestAsync(42, CancellationToken.None);

        Assert.Equal(PullRequestState.Merged, pr.State);
        Assert.Equal("9c8b7a6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b", pr.MergeCommitHash);
        Assert.Equal(("acme/orders", "a1b2c3d4e5f6"), (pr.SourceRepository, pr.SourceCommit));
    }

    [Fact]
    public async Task Get_pr_without_source_repository_or_commit_maps_them_as_unknown()
    {
        var json = "{\"id\":42,\"state\":\"OPEN\",\"source\":{\"branch\":{\"name\":\"x\"},\"repository\":null},"
                   + "\"links\":{\"html\":{\"href\":\"https://bitbucket.org/acme/orders/pull-requests/42\"}}}";
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests/42", HttpStatusCode.OK, json);

        var pr = await Scm(stub).GetPullRequestAsync(42, CancellationToken.None);

        Assert.Equal((null, null), (pr.SourceRepository, pr.SourceCommit));
        Assert.False(pr.WouldMerge("acme/orders", "a1b2c3d4e5f6"));
    }

    [Theory]
    [InlineData("DECLINED", PullRequestState.Declined)]
    [InlineData("SUPERSEDED", PullRequestState.Declined)]
    public async Task Get_pr_maps_declined_and_superseded(string state, PullRequestState expected)
    {
        var json = Fixture("bb-pr-create.json").Replace("\"state\": \"OPEN\"", $"\"state\": \"{state}\"");
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests/42", HttpStatusCode.OK, json);

        Assert.Equal(expected, (await Scm(stub).GetPullRequestAsync(42, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Merge_success_returns_hash()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests/42/merge", HttpStatusCode.OK, Fixture("bb-pr-merged.json"));

        var result = await Scm(stub).MergePullRequestAsync(42, CancellationToken.None);

        Assert.True(result.Merged);
        Assert.Equal("9c8b7a6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b", result.MergeCommitHash);
        Assert.Null(result.Error);
        Assert.Contains("\"merge_strategy\":\"squash\"", stub.Requests[0].Body);
    }

    [Fact]
    public async Task Merge_refused_returns_error_not_throw()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests/42/merge", HttpStatusCode.BadRequest,
            "{\"type\":\"error\",\"error\":{\"message\":\"Requires 2 approvals\"}}");

        var result = await Scm(stub).MergePullRequestAsync(42, CancellationToken.None);

        Assert.False(result.Merged);
        Assert.Null(result.MergeCommitHash);
        Assert.Equal("Requires 2 approvals", result.Error);
    }

    [Fact]
    public async Task Merge_4xx_without_error_message_falls_back_to_body_excerpt()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests/42/merge", HttpStatusCode.Conflict, "plain conflict text");

        var result = await Scm(stub).MergePullRequestAsync(42, CancellationToken.None);

        Assert.False(result.Merged);
        Assert.Equal("plain conflict text", result.Error);
    }

    [Fact]
    public async Task Merge_accepted_means_in_progress()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests/42/merge", HttpStatusCode.Accepted,
            "{\"task_status\":\"PENDING\",\"links\":{\"self\":{\"href\":\"https://api.bitbucket.org/2.0/repositories/acme/orders/pullrequests/42/merge/task-status/abc\"}}}");

        var result = await Scm(stub).MergePullRequestAsync(42, CancellationToken.None);

        Assert.False(result.Merged);
        Assert.Equal("merge in progress", result.Error);
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Merge_5xx_throws_without_token()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pullrequests/42/merge", HttpStatusCode.BadGateway, "upstream down");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).MergePullRequestAsync(42, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Contains("upstream down", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Get_pr_throws_on_http_error()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests/7", HttpStatusCode.NotFound,
            "{\"type\":\"error\",\"error\":{\"message\":\"No pull request\"}}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).GetPullRequestAsync(7, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("No pull request", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Does_not_set_default_authorization_header()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pullrequests/42", HttpStatusCode.OK, Fixture("bb-pr-merged.json"));
        var http = new HttpClient(stub);
        await new BitbucketSourceControl(http, Repo, Token).GetPullRequestAsync(42, CancellationToken.None);

        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(new Uri("https://api.bitbucket.org/2.0/"), http.BaseAddress);
    }

    [Fact]
    public void Branch_file_url()
    {
        var url = Scm(new StubHttpHandler()).BranchFileUrl("symphonist/ORD-1", "docs/plan.md");
        Assert.Equal(new Uri("https://bitbucket.org/acme/orders/src/symphonist/ORD-1/docs/plan.md"), url);
    }

    [Theory]
    [InlineData("abc123", BuildStatus.Succeeded)]
    [InlineData("def456", BuildStatus.Running)]
    [InlineData("eee777", BuildStatus.Running)]
    [InlineData("bad999", BuildStatus.Failed)]
    [InlineData("zzz000", BuildStatus.NotFound)]
    [InlineData("abc1230000aabbccddeeff00112233445566778899aabbcc", BuildStatus.Succeeded)] // longer than pipeline hash
    public async Task Pipeline_status_by_commit(string hash, BuildStatus expected)
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pipelines/", HttpStatusCode.OK, Fixture("bb-pipelines.json"));
        var watcher = new BitbucketPipelinesWatcher(new HttpClient(stub), Repo, Token);

        Assert.Equal(expected, await watcher.GetStatusAsync(hash, CancellationToken.None));
        Assert.Contains("sort=-created_on", stub.Requests[0].PathAndQuery);
        Assert.Contains("pagelen=50", stub.Requests[0].PathAndQuery);
    }
}
