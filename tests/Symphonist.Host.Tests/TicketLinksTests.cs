using Symphonist.Core.Config;
using Symphonist.Host;
using Symphonist.Integrations.GitHub;

namespace Symphonist.Host.Tests;

public class TicketLinksTests
{
    private static ProjectConfig GitHubProject() =>
        new("orders", "Orders",
            new GitHubProjectsTrackerConfig("github-projects", "github-acme", "acme", 7, "Claude", "Status", null, null),
            new GitHubRepoConfig("github", "/work/orders", "main", "production", "github-acme", "acme", "orders", "merge"),
            null!, null!, 3, [], new Dictionary<StageKind, string>(), "/cfg");

    [Fact]
    public void Ticket_links_for_github_project()
    {
        var project = GitHubProject();

        Assert.Equal(new Uri("https://github.com/acme/orders/issues/12"), TicketLinks.Issue(project, "orders-12"));
        Assert.Equal(new Uri("https://github.com/acme/orders/pull/34"), TicketLinks.PullRequest(project, 34));
        Assert.Null(TicketLinks.PullRequest(project, null));
    }
}
