using System.Collections.Concurrent;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>
/// Picks the launcher for each run strictly from <c>request.Settings.Isolation</c>: <see cref="Isolation.Container"/>
/// goes to the container launcher, <see cref="Isolation.Host"/> to the host launcher. Every member delegates to the
/// same choice, so the isolation reported for a request is that of the launcher that actually runs it.
/// </summary>
public sealed class IsolationLauncher(IAgentLauncher host, IAgentLauncher container) : IAgentLauncher
{
    public const string DockerNotAvailable = "Docker not available";

    private readonly ConcurrentDictionary<string, string> imageProblems = new(StringComparer.Ordinal);
    private volatile string? containersProblem;

    /// <summary>Makes every container run fail with <paramref name="reason"/> (e.g. Docker is not running).</summary>
    public void DisableContainers(string reason) => containersProblem = reason;

    /// <summary>Makes container runs that use <paramref name="image"/> fail with <paramref name="reason"/>.</summary>
    public void DisableImage(string image, string reason) => imageProblems[image] = reason;

    public bool ProvidesIsolation(AgentRunRequest request) => For(request).ProvidesIsolation(request);

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        if (request.Settings.Isolation == Isolation.Container)
        {
            if (containersProblem is { } problem) throw new InvalidOperationException(problem);
            if (imageProblems.TryGetValue(request.Settings.Image ?? DockerLauncher.DefaultImage, out var imageProblem))
                throw new InvalidOperationException(imageProblem);
        }
        return For(request).Wrap(agentCommand, request);
    }

    public Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct) =>
        For(request).CleanupAsync(wrapped, request, ct);

    private IAgentLauncher For(AgentRunRequest request) => request.Settings.Isolation switch
    {
        Isolation.Container => container,
        Isolation.Host => host,
        var other => throw new InvalidOperationException($"unsupported isolation '{other}'"),
    };
}
