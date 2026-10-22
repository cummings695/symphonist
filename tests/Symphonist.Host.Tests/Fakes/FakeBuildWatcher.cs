using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeBuildWatcher : IBuildWatcher
{
    /// <summary>Status per commit hash; unknown hashes report NotFound.</summary>
    public Dictionary<string, BuildStatus> Statuses { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<BuildStatus> GetStatusAsync(string commitHash, CancellationToken ct)
    {
        Calls.Add(commitHash);
        return Task.FromResult(Statuses.GetValueOrDefault(commitHash, BuildStatus.NotFound));
    }
}
