using System.Collections.Concurrent;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Integrations;

/// <summary>
/// Builds a project's Jira tracker and Bitbucket source control / Pipelines watcher from the named HTTP clients
/// <see cref="JiraClient"/> and <see cref="BitbucketClient"/> and the project's secrets. Cached per project id until
/// that project's configuration object changes (a reload); a missing secret is reported, never cached.
/// </summary>
public sealed class IntegrationFactory(IHttpClientFactory http, ISecretStore secrets) : IIntegrationFactory
{
    public const string JiraClient = "jira";
    public const string BitbucketClient = "bitbucket";

    private readonly ConcurrentDictionary<string, (ProjectConfig Config, ProjectIntegrations Io)> cache = new(StringComparer.Ordinal);

    public ProjectIntegrations Create(ProjectConfig project)
    {
        if (cache.TryGetValue(project.Id, out var cached) && ReferenceEquals(cached.Config, project)) return cached.Io;

        var jiraToken = Secret(project.Tracker.TokenSecret);
        var bitbucketToken = Secret(project.Repo.TokenSecret);
        var io = new ProjectIntegrations(
            new JiraTracker(http.CreateClient(JiraClient), project.Tracker, jiraToken),
            new BitbucketSourceControl(http.CreateClient(BitbucketClient), project.Repo, bitbucketToken),
            new BitbucketPipelinesWatcher(http.CreateClient(BitbucketClient), project.Repo, bitbucketToken));
        cache[project.Id] = (project, io);
        return io;
    }

    private string Secret(string name) =>
        secrets.Get(name) ?? throw new InvalidOperationException($"secret '{name}' not set; run: symphonist secret set {name}");
}
