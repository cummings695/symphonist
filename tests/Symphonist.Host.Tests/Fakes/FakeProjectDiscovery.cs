using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Host.Tests.Fakes;

/// <summary>Returns settable results per slot (defaults: the sample project's Jira board), or throws its Error.</summary>
public sealed class FakeProjectDiscovery : IProjectDiscovery
{
    public TrackerDiscovery Tracker { get; set; } = new("Dev", "Jira project ACME",
    [
        new("Backlog", StatusCategory.ToDo, ""),
        new("Planned", StatusCategory.ToDo, ""),
        new("In Progress", StatusCategory.InProgress, ""),
        new("In Review", StatusCategory.InProgress, ""),
        new("Done", StatusCategory.Done, ""),
    ], 3, []);

    public RepositoryDiscovery Repository { get; set; } = new("Acme/orders", "main", true);

    public BuildDiscovery Build { get; set; } = new("latest pipeline #1 (COMPLETED)", [], []);

    public Exception? TrackerError { get; set; }
    public Exception? RepositoryError { get; set; }
    public Exception? BuildError { get; set; }

    /// <summary>Discover calls so far, all slots together.</summary>
    public int Calls { get; private set; }

    public Task<TrackerDiscovery> DiscoverTrackerAsync(ProjectConfig draft, CancellationToken ct) =>
        Result(Tracker, TrackerError);

    public Task<RepositoryDiscovery> DiscoverRepositoryAsync(ProjectConfig draft, CancellationToken ct) =>
        Result(Repository, RepositoryError);

    public Task<BuildDiscovery> DiscoverBuildAsync(ProjectConfig draft, CancellationToken ct) =>
        Result(Build, BuildError);

    private Task<T> Result<T>(T value, Exception? error)
    {
        Calls++;
        return error is null ? Task.FromResult(value) : Task.FromException<T>(error);
    }
}
