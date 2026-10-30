using Symphonist.Core.Config;
using Symphonist.Integrations;

namespace Symphonist.Host;

/// <summary>Links from a ticket to its issue and pull request, shared by the API and the dashboard.</summary>
public static class TicketLinks
{
    /// <summary>Null for a release ticket (key <c>_release-{n}</c>): it has no tracker issue.</summary>
    public static Uri? Issue(ProjectConfig? p, string key) =>
        p is null || key.StartsWith('_') ? null : BuiltInProviders.Registry.Tracker(p.Tracker.Type).IssueUrl(p, key);

    public static Uri? PullRequest(ProjectConfig? p, long? id) =>
        p is not null && id is { } prId ? BuiltInProviders.Registry.Repo(p.Repo.Type).PullRequestUrl(p, prId) : null;
}
