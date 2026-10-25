using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Orchestration;

/// <summary>
/// Prepares Docker before the orchestrator starts, only when some project uses container isolation: removes agent
/// containers left over from a previous run, then makes sure every image those projects use exists (building the
/// default image and any <c>agent.packages</c> images when missing or stale). When Docker or an image is unavailable
/// the affected agent runs fail with a clear error instead of the host failing to start. Registered before
/// <see cref="OrchestratorService"/>, so no agent run starts until this has finished.
/// </summary>
public sealed class DockerStartup(ProjectRegistry projects, DockerLauncher docker, IsolationLauncher launcher,
    ILogger<DockerStartup> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // One entry per distinct image; the review agent runs in the same image as the project's agent.
        var images = projects.Projects
            .Where(p => p.Agent.Isolation == Isolation.Container)
            .Select(p => p.Agent)
            .DistinctBy(TryImageFor, StringComparer.Ordinal)
            .ToList();
        if (images.Count == 0) return;

        if (!await docker.IsAvailableAsync(cancellationToken))
        {
            log.LogError("Docker is not available (is Docker running?); agent stages of container-isolated projects will fail with \"{Error}\"",
                IsolationLauncher.DockerNotAvailable);
            launcher.DisableContainers(IsolationLauncher.DockerNotAvailable);
            // A container check later re-enables containers for every project, but only the image it ensured: the
            // others stay refused rather than running a stale local image (old firewall or entrypoint).
            foreach (var settings in images)
            {
                try { launcher.DisableImage(DockerLauncher.ImageFor(settings), IsolationLauncher.ImageNotPrepared); }
                catch (ArgumentException) { } // invalid image name: EnsureImage reports it once Docker is back
            }
            return;
        }

        try
        {
            var removed = await docker.RemoveLeftoverContainersAsync(cancellationToken);
            if (removed > 0) log.LogInformation("Removed {Count} leftover agent container(s)", removed);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(e, "Removing leftover agent containers failed");
        }

        foreach (var settings in images)
        {
            var image = TryImageFor(settings);
            try
            {
                log.LogInformation("Checking agent image {Image} (first build can take several minutes)", image);
                await docker.EnsureImageAsync(settings, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogError(e, "Agent image {Image} is not available", image);
                launcher.DisableImage(image, e.Message);
            }
        }
    }

    /// <summary>The run image, or the configured name when it is invalid (EnsureImage then reports why).</summary>
    private static string TryImageFor(AgentSettings settings)
    {
        try { return DockerLauncher.ImageFor(settings); }
        catch (ArgumentException) { return settings.Image ?? DockerLauncher.DefaultImage; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
