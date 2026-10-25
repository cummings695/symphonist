using Symphonist.Core.Config;
using Symphonist.Integrations;

namespace Symphonist.Host;

/// <summary>Links from a ticket to its issue and pull request, shared by the API and the dashboard.</summary>
public static class TicketLinks
{
    public static Uri? Issue(ProjectConfig? p, string key) =>
        p is null ? null : BuiltInProviders.Registry.Tracker(p.Tracker.Type).IssueUrl(p, key);

    public static Uri? PullRequest(ProjectConfig? p, long? id) =>
        p is not null && id is { } prId ? BuiltInProviders.Registry.Repo(p.Repo.Type).PullRequestUrl(p, prId) : null;
}
