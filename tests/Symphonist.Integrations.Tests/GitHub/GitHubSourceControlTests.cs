using System.Net;
using System.Text.Json;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.GitHub;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.GitHub;

public class GitHubSourceControlTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static GitHubRepoConfig Repo(string merge = "merge") =>
        new("github", "/work/orders", "main", "production", "gh-token", "acme", "orders", merge);

    private const string Token = "ghp_secret";
    private const string RepoPrefix = "/repos/acme/orders";

    private static GitHubSourceControl Scm(StubHttpHandler stub, string merge = "merge") =>
        new(new HttpClient(stub), Repo(merge), Token);

    [Fact]
    public async Task Open_posts_head_and_base_and_maps_pr()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pulls", HttpStatusCode.Created, Fixture("gh-pr-create.json"));

        var pr = await Scm(stub).OpenPullRequestAsync("symphonist/orders-1", "main", "ORD-1: Throttle", "desc", CancellationToken.None);

        Assert.Equal(12, pr.Id);
        Assert.Equal(PullRequestState.Open, pr.State);
        Assert.Equal(new Uri("https://github.com/acme/orders/pull/12"), pr.WebUrl);
        Assert.Null(pr.MergeCommitHash);
        Assert.Equal("acme/orders", pr.SourceRepository);
        Assert.Equal("9f2c4e1a7b8d0c3e5f6a1b2c3d4e5f6a7b8c9d0e", pr.SourceCommit);
        var body = Assert.Single(stub.Requests).Body!;
        Assert.Contains("\"head\":\"symphonist/orders-1\"", body);
        Assert.Contains("\"base\":\"main\"", body);
        Assert.Contains("\"title\":\"ORD-1: Throttle\"", body);
        Assert.Contains("\"body\":\"desc\"", body);
    }

    [Fact]
    public async Task Open_with_no_commits_throws_NoChangesToPull()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pulls", HttpStatusCode.UnprocessableEntity,
            Fixture("gh-pr-no-commits.json"));

        var ex = await Assert.ThrowsAsync<NoChangesToPullException>(() =>
            Scm(stub).OpenPullRequestAsync("main", "production", "t", "d", CancellationToken.None));

        Assert.Equal("No commits between main and symphonist/orders-1", ex.Message);
    }

    [Fact]
    public async Task Open_other_422_throws_http_error()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Post, RepoPrefix + "/pulls", HttpStatusCode.UnprocessableEntity,
            "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"A pull request already exists for acme:x.\"}]}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Scm(stub).OpenPullRequestAsync("x", "main", "t", "d", CancellationToken.None));

        Assert.StartsWith("GitHub POST repos/acme/orders/pulls failed:", ex.Message);
    }

    [Fact]
    public async Task Find_returns_own_open_pr()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pulls?", HttpStatusCode.OK, Fixture("gh-pr-list.json"));

        var pr = await Scm(stub).FindOpenPullRequestAsync("symphonist/orders-1", "main", CancellationToken.None);

        Assert.Equal(12, pr!.Id);
        var query = Assert.Single(stub.Requests).PathAndQuery;
        Assert.Contains("state=open", query);
        Assert.Contains("head=acme%3Asymphonist%2Forders-1", query);
        Assert.Contains("base=main", query);
    }

    [Fact]
    public async Task Find_ignores_fork_with_same_branch()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pulls?", HttpStatusCode.OK, Fixture("gh-pr-list-fork.json"));

        Assert.Null(await Scm(stub).FindOpenPullRequestAsync("symphonist/orders-1", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Get_maps_merged_and_closed()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, RepoPrefix + "/pulls/12", HttpStatusCode.OK, Fixture("gh-pr-merged.json"))
            .On(HttpMethod.Get, RepoPrefix + "/pulls/13", HttpStatusCode.OK, Fixture("gh-pr-closed.json"));

        var merged = await Scm(stub).GetPullRequestAsync(12, CancellationToken.None);
        var closed = await Scm(stub).GetPullRequestAsync(13, CancellationToken.None);

        Assert.Equal(PullRequestState.Merged, merged.State);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4", merged.MergeCommitHash);
        Assert.Equal(PullRequestState.Declined, closed.State);
        Assert.Null(closed.MergeCommitHash);
    }

    [Fact]
    public async Task Merge_sends_method_and_returns_sha()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Put, RepoPrefix + "/pulls/12/merge", HttpStatusCode.OK, Fixture("gh-merge-ok.json"));

        var result = await Scm(stub, "squash").MergePullRequestAsync(12, CancellationToken.None);

        Assert.True(result.Merged);
        Assert.Equal("6dcb09b5b57875f334f61aebed695e2e4193db5e", result.MergeCommitHash);
        Assert.Contains("\"merge_method\":\"squash\"", Assert.Single(stub.Requests).Body);
    }

    [Fact]
    public async Task Merge_405_is_a_refusal_with_message()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Put, RepoPrefix + "/pulls/12/merge", HttpStatusCode.MethodNotAllowed,
            Fixture("gh-merge-405.json"));

        var result = await Scm(stub).MergePullRequestAsync(12, CancellationToken.None);

        Assert.False(result.Merged);
        Assert.False(result.Accepted);
        using var doc = JsonDocument.Parse(Fixture("gh-merge-405.json"));
        Assert.Equal(doc.RootElement.GetProperty("message").GetString(), result.Error);
    }

    [Fact]
    public async Task Merge_5xx_throws()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Put, RepoPrefix + "/pulls/12/merge", HttpStatusCode.BadGateway, "bad gateway");

        await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).MergePullRequestAsync(12, CancellationToken.None));
    }

    [Fact]
    public async Task Rate_limit_throws_instead_of_refusing()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Put, RepoPrefix + "/pulls/12/merge", HttpStatusCode.Forbidden,
            "{\"message\":\"API rate limit exceeded\"}", headers: new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0" });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).MergePullRequestAsync(12, CancellationToken.None));

        Assert.StartsWith("GitHub PUT repos/acme/orders/pulls/12/merge failed:", ex.Message);
    }

    [Fact]
    public async Task Plain_403_is_a_refusal()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Put, RepoPrefix + "/pulls/12/merge", HttpStatusCode.Forbidden,
            "{\"message\":\"Resource not accessible by personal access token\"}");

        var result = await Scm(stub).MergePullRequestAsync(12, CancellationToken.None);

        Assert.False(result.Merged);
        Assert.Equal("Resource not accessible by personal access token", result.Error);
    }

    [Fact]
    public async Task Too_many_requests_throws()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pulls/12", (HttpStatusCode)429, "slow down");

        await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).GetPullRequestAsync(12, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"status\":\"identical\",\"ahead_by\":0,\"behind_by\":2}", true)]
    [InlineData("{\"status\":\"ahead\",\"ahead_by\":3,\"behind_by\":0}", false)]
    public async Task Branch_contains_when_ahead_by_zero(string body, bool expected)
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/compare/", HttpStatusCode.OK, body);

        Assert.Equal(expected, await Scm(stub).BranchContainsAsync("production", "main", CancellationToken.None));
        Assert.Equal(RepoPrefix + "/compare/production...main", Assert.Single(stub.Requests).PathAndQuery);
    }

    [Fact]
    public async Task Branch_contains_throws_without_ahead_by()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/compare/", HttpStatusCode.OK,
            Fixture("gh-compare.json").Replace("ahead_by", "x"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Scm(stub).BranchContainsAsync("production", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Branch_head_reads_commit_sha()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/branches/production", HttpStatusCode.OK, Fixture("gh-branch.json"));

        Assert.Equal("0123abcd4567ef890123abcd4567ef890123abcd", await Scm(stub).GetBranchHeadAsync("production", CancellationToken.None));
    }

    [Fact]
    public async Task Sends_bearer_and_api_version_headers()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pulls/12", HttpStatusCode.OK, Fixture("gh-pr-create.json"));

        await Scm(stub).GetPullRequestAsync(12, CancellationToken.None);

        Assert.Equal("Bearer " + Token, stub.Authorizations[0]);
        var headers = stub.RequestHeaders[0];
        Assert.Equal("application/vnd.github+json", headers["Accept"]);
        Assert.Equal("2022-11-28", headers["X-GitHub-Api-Version"]);
        Assert.Equal("Symphonist", headers["User-Agent"]);
    }

    [Fact]
    public async Task Uses_api_github_com_when_no_base_address()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RepoPrefix + "/pulls/12", HttpStatusCode.OK, Fixture("gh-pr-create.json"));
        var http = new HttpClient(stub);

        await new GitHubSourceControl(http, Repo(), Token).GetPullRequestAsync(12, CancellationToken.None);

        Assert.Equal(new Uri("https://api.github.com/"), http.BaseAddress);
    }

    [Fact]
    public void Branch_file_url_points_at_blob() =>
        Assert.Equal(new Uri("https://github.com/acme/orders/blob/symphonist/orders-1/docs/spec.md"),
            Scm(new StubHttpHandler()).BranchFileUrl("symphonist/orders-1", "docs/spec.md"));
}
