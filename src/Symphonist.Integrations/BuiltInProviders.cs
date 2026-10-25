using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.GitHub;
using Symphonist.Integrations.Jira;

namespace Symphonist.Integrations;

/// <summary>The providers compiled into Symphonist.</summary>
public static class BuiltInProviders
{
    public static ProviderRegistry Registry { get; } = new(
        [new JiraProvider()],
        [new BitbucketProvider(), new GitHubRepoProvider()],
        [new BitbucketPipelinesProvider()]);
}
