using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations.Providers;

public enum StatusCategory { ToDo, InProgress, Done, Unknown }

/// <summary>A status as the tracker names it. Name and description are untrusted tracker text.</summary>
public sealed record SourceStatus(string Name, StatusCategory Category, string Description);

/// <param name="StatusSource">Where the statuses come from, for messages: "Jira project ACME", "GitHub board acme #7 (Status)"; null when none were read.</param>
/// <param name="Statuses">Empty when the provider could not identify a status source (e.g. Jira without projectKey).</param>
/// <param name="MatchingTickets">Approximate count of tickets the tracker would pick up; null when the count call failed.</param>
public sealed record TrackerDiscovery(string AccountName, string? StatusSource, IReadOnlyList<SourceStatus> Statuses,
    int? MatchingTickets, IReadOnlyList<string> Notes);

public sealed record RepositoryDiscovery(string FullName, string MainBranch, bool ProductionBranchExists);

/// <param name="Workflows">Build definitions the host knows (GitHub workflow file names); empty when not applicable.</param>
public sealed record BuildDiscovery(string Summary, IReadOnlyList<string> Workflows, IReadOnlyList<string> Notes);

/// <summary>
/// Read-only discovery for the setup screen. Resolves provider, named client and secret per slot like
/// <c>IntegrationFactory</c>, never caches, never touches the factory. A missing secret throws
/// <see cref="InvalidOperationException"/> "secret '{name}' not set; run: symphonist secret set {name}".
/// </summary>
public interface IProjectDiscovery
{
    Task<TrackerDiscovery> DiscoverTrackerAsync(ProjectConfig draft, CancellationToken ct);
    Task<RepositoryDiscovery> DiscoverRepositoryAsync(ProjectConfig draft, CancellationToken ct);
    Task<BuildDiscovery> DiscoverBuildAsync(ProjectConfig draft, CancellationToken ct);
}
