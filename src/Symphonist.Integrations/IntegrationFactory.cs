using System.Collections.Concurrent;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Integrations;

/// <summary>
/// Builds a project's Jira tracker and Bitbucket source control / Pipelines watcher from the named HTTP clients
/// <see cref="JiraClient"/> and <see cref="BitbucketClient"/> and the project's secrets. Cached per project id until
/// that project's configuration object or either token value changes; a missing secret is reported, never cached.
/// </summary>
public sealed class IntegrationFactory(IHttpClientFactory http, ISecretStore secrets) : IIntegrationFactory
{
    public const string JiraClient = "jira";
    public const string BitbucketClient = "bitbucket";

    private readonly ConcurrentDictionary<string, (ProjectConfig Config, string JiraToken, string BitbucketToken, ProjectIntegrations Io)> cache = new(StringComparer.Ordinal);

    public ProjectIntegrations Create(ProjectConfig project)
    {
        // Secrets are read on every call so a changed or removed secret takes effect without a restart.
        var jiraToken = Secret(project.Tracker.TokenSecret);
        var bitbucketToken = Secret(project.Repo.TokenSecret);
        if (cache.TryGetValue(project.Id, out var cached) && ReferenceEquals(cached.Config, project)
            && string.Equals(cached.JiraToken, jiraToken, StringComparison.Ordinal)
            && string.Equals(cached.BitbucketToken, bitbucketToken, StringComparison.Ordinal))
            return cached.Io;

        var io = new ProjectIntegrations(
            new JiraTracker(http.CreateClient(JiraClient), project.Tracker, jiraToken),
            new BitbucketSourceControl(http.CreateClient(BitbucketClient), project.Repo, bitbucketToken),
            new BitbucketPipelinesWatcher(http.CreateClient(BitbucketClient), project.Repo, bitbucketToken));
        cache[project.Id] = (project, jiraToken, bitbucketToken, io);
        return io;
    }

    private string Secret(string name) =>
        secrets.Get(name) ?? throw new InvalidOperationException($"secret '{name}' not set; run: symphonist secret set {name}");
}
