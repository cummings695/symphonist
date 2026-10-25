using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeBuildWatcher : IBuildWatcher
{
    /// <summary>Status per commit hash; unknown hashes report NotFound.</summary>
    public Dictionary<string, BuildStatus> Statuses { get; } = [];
    public List<(string Commit, BuildPurpose Purpose)> Calls { get; } = [];

    /// <summary>Scripted statuses per commit, reported one per call; the last one sticks (overrides <see cref="Statuses"/>).</summary>
    public Dictionary<string, Queue<BuildStatus>> Scripts { get; } = [];

    public void Script(string commitHash, params BuildStatus[] statuses) => Scripts[commitHash] = new(statuses);

    public Task<BuildStatus> GetStatusAsync(string commitHash, BuildPurpose purpose, CancellationToken ct)
    {
        Calls.Add((commitHash, purpose));
        if (Scripts.TryGetValue(commitHash, out var script) && script.Count > 0)
            return Task.FromResult(script.Count > 1 ? script.Dequeue() : script.Peek());
        return Task.FromResult(Statuses.GetValueOrDefault(commitHash, BuildStatus.NotFound));
    }
}
