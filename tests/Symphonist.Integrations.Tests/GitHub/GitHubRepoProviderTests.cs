using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.GitHub;

namespace Symphonist.Tests.GitHub;

public class GitHubRepoProviderTests
{
    private static RepoSection Section(params (string Key, object? Value)[] extra)
    {
        var values = new Dictionary<string, object?>
        {
            ["type"] = "github", ["tokenSecret"] = "github-acme", ["localPath"] = "src/orders",
        };
        foreach (var (key, value) in extra) values[key] = value;
        return new RepoSection(values, Path.GetFullPath("/cfg"));
    }

    [Fact]
    public void Parses_owner_repo_and_default_merge()
    {
        var provider = new GitHubRepoProvider();
        var repo = Assert.IsType<GitHubRepoConfig>(provider.Parse(Section(("owner", "acme"), ("repo", "orders"))));

        Assert.Equal("acme/orders", repo.FullName);
        Assert.Equal("merge", repo.MergeStrategy);
        Assert.Equal("github-acme", repo.TokenSecret);
        Assert.Equal("main", repo.MainBranch);
        Assert.Equal("github", provider.Type);
        Assert.Equal("github", provider.HttpClientName);
    }

    [Theory]
    [InlineData("squash")]
    [InlineData("rebase")]
    public void Accepts_squash_and_rebase(string strategy)
    {
        var repo = Assert.IsType<GitHubRepoConfig>(new GitHubRepoProvider().Parse(
            Section(("owner", "acme"), ("repo", "orders"), ("mergeStrategy", strategy))));

        Assert.Equal(strategy, repo.MergeStrategy);
    }

    [Fact]
    public void Rejects_unknown_merge_strategy()
    {
        var ex = Assert.Throws<ProjectConfigException>(() => new GitHubRepoProvider().Parse(
            Section(("owner", "acme"), ("repo", "orders"), ("mergeStrategy", "fast-forward"))));

        Assert.Equal("unknown repo.mergeStrategy 'fast-forward'", ex.Message);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("repo")]
    public void Requires_owner_and_repo(string missing)
    {
        var extra = new[] { ("owner", (object?)"acme"), ("repo", "orders") }.Where(e => e.Item1 != missing).ToArray();

        var ex = Assert.Throws<ProjectConfigException>(() => new GitHubRepoProvider().Parse(Section(extra)));

        Assert.Equal($"missing required 'repo.{missing}'", ex.Message);
    }

    [Fact]
    public void Pull_request_url_points_at_github()
    {
        var repo = (GitHubRepoConfig)new GitHubRepoProvider().Parse(Section(("owner", "acme"), ("repo", "orders")));

        Assert.Equal(new Uri("https://github.com/acme/orders/pull/12"), GitHubSourceControl.PullRequestUrl(repo, 12));
    }
}
