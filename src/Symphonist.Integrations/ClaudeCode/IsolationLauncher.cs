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
    public const string ImageNotPrepared =
        "agent image not prepared since the host started; run the container check on the setup screen";

    private readonly ConcurrentDictionary<string, string> imageProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> preparedImages = new(StringComparer.Ordinal);
    private volatile string? containersProblem;
    private volatile bool onlyPreparedImages;

    /// <summary>
    /// Fail closed: from now on a container run fails with <see cref="ImageNotPrepared"/> unless its image was
    /// <see cref="EnableImage">ensured</see> since the host started. Used when startup could not prepare the images
    /// (Docker was down, or no project used containers yet), so a project added later never runs a stale local image.
    /// </summary>
    public void RequirePreparedImages() => onlyPreparedImages = true;

    /// <summary>Makes every container run fail with <paramref name="reason"/> (e.g. Docker is not running).</summary>
    public void DisableContainers(string reason) => containersProblem = reason;

    /// <summary>
    /// Makes container runs whose <see cref="DockerLauncher.ImageFor">image</see> is <paramref name="image"/> fail with
    /// <paramref name="reason"/>.
    /// </summary>
    public void DisableImage(string image, string reason) => imageProblems[image] = reason;

    /// <summary>Clears <see cref="DisableContainers"/> (e.g. Docker was started after the host and a check found it).</summary>
    public void EnableContainers() => containersProblem = null;

    /// <summary>
    /// Records <paramref name="image"/> as ensured (built or verified current) and clears <see cref="DisableImage"/>
    /// for it (e.g. startup or a check has built it).
    /// </summary>
    public void EnableImage(string image)
    {
        preparedImages[image] = 0;
        imageProblems.TryRemove(image, out _);
    }

    public bool ProvidesIsolation(AgentRunRequest request) => For(request).ProvidesIsolation(request);

    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request)
    {
        if (request.Settings.Isolation == Isolation.Container)
        {
            if (containersProblem is { } problem) throw new InvalidOperationException(problem);
            var image = DockerLauncher.ImageFor(request.Settings);
            if (imageProblems.TryGetValue(image, out var imageProblem))
                throw new InvalidOperationException(imageProblem);
            if (onlyPreparedImages && !preparedImages.ContainsKey(image))
                throw new InvalidOperationException(ImageNotPrepared);
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
