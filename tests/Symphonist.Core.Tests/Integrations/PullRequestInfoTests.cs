using Symphonist.Core.Integrations;

namespace Symphonist.Core.Tests.Integrations;

public class PullRequestInfoTests
{
    private const string Full = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private static PullRequestInfo Pr(string? repo, string? commit) =>
        new(1, new Uri("https://scm.example/pr/1"), PullRequestState.Open, null, repo, commit);

    [Theory]
    [InlineData("acme/orders", "a1b2c3d4e5f6", true)] // Bitbucket abbreviates the source commit
    [InlineData("ACME/Orders", Full, true)]
    [InlineData("mallory/orders", "a1b2c3d4e5f6", false)] // a fork
    [InlineData(null, "a1b2c3d4e5f6", false)]
    [InlineData("acme/orders", "ffffffffffff", false)]
    [InlineData("acme/orders", "a1b2c3", false)] // too short to identify the commit
    [InlineData("acme/orders", null, false)]
    public void Would_merge_only_the_given_commit_of_the_given_repository(string? repo, string? commit, bool expected) =>
        Assert.Equal(expected, Pr(repo, commit).WouldMerge("acme/orders", Full));
}
