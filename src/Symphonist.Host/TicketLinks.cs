using Symphonist.Core.Config;
using Symphonist.Integrations.Bitbucket;

namespace Symphonist.Host;

/// <summary>Links from a ticket to its issue and pull request, shared by the API and the dashboard.</summary>
public static class TicketLinks
{
    public static Uri? Issue(ProjectConfig? p, string key) =>
        p is { Tracker.Type: "jira" } ? new Uri(p.Tracker.Site, "browse/" + Uri.EscapeDataString(key)) : null;

    public static Uri? PullRequest(ProjectConfig? p, long? id) =>
        p is { Repo.Type: "bitbucket" } && id is { } prId ? BitbucketSourceControl.PullRequestUrl(p.Repo, prId) : null;
}
