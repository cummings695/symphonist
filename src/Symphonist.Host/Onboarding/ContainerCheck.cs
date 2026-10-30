using System.Globalization;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Prompts;
using Symphonist.Core.Risk;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Onboarding;

/// <summary>
/// The optional container check of the setup screen: Docker answers, the agent image exists (built when missing or
/// stale), and an agent can build and test the repository in the container. The agent run is a normal writable
/// container run (firewall, read-only <c>.git</c>) in a throwaway clone that is deleted before and after; Symphonist
/// never commits or pushes from it. A passing step clears the matching startup problem in <see cref="IsolationLauncher"/>,
/// so container runs work again without restarting the host. Not a ticket agent run: nothing is stored, the raw log
/// goes to <c>logs/{id}/_onboarding/</c>.
/// </summary>
public sealed class ContainerCheck(DockerLauncher docker, IsolationLauncher launcher, IGitWorkspace git,
    IAgentRunners agents, TimeProvider time, ILogger<ContainerCheck>? log = null)
{
    public const string WorkspaceKey = "_container-check";  // no tracker key has this shape (Jira ABC-1, GitHub repo-1)

    private const string DockerStep = "Docker";
    private const string ImageStep = "Agent image";
    private const string BuildStep = "Build and tests in container";

    /// <summary>Steps in order: "Docker", "Agent image", "Build and tests in container". Never throws (failed steps instead); honours ct.</summary>
    /// <param name="draft">A config that passed <see cref="ProjectConfigLoader.Parse"/> (validated id and agent settings).</param>
    public async Task<IReadOnlyList<CheckStep>> RunAsync(ProjectConfig draft,
        IProgress<IReadOnlyList<CheckStep>>? progress, CancellationToken ct)
    {
        var steps = new[]
        {
            new CheckStep(DockerStep, CheckStatus.NotRun, ""),
            new CheckStep(ImageStep, CheckStatus.NotRun, ""),
            new CheckStep(BuildStep, CheckStatus.NotRun, ""),
        };
        void Set(int index, CheckStatus status, string detail)
        {
            steps[index] = steps[index] with { Status = status, Detail = detail };
            progress?.Report(steps.ToArray());
        }
        void SkipFrom(int index, string detail)
        {
            for (var i = index; i < steps.Length; i++) steps[i] = steps[i] with { Status = CheckStatus.Skipped, Detail = detail };
            progress?.Report(steps.ToArray());
        }

        if (draft.Agent.Isolation != Isolation.Container)
        {
            SkipFrom(0, "host isolation: no container to check");
            return steps;
        }

        Set(0, CheckStatus.Running, "");
        if (!await docker.IsAvailableAsync(ct))
        {
            steps[0] = steps[0] with { Status = CheckStatus.Failed, Detail = "Docker is not running or not installed" };
            SkipFrom(1, "Docker is not available");
            return steps;
        }
        launcher.EnableContainers();
        Set(0, CheckStatus.Passed, "Docker is running");

        Set(1, CheckStatus.Running, "checking the image; a first build can take several minutes");
        string image;
        try
        {
            image = DockerLauncher.ImageFor(draft.Agent);
            await docker.EnsureImageAsync(draft.Agent, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(e, "Container check of {Project}: agent image is not available", draft.Id);
            var problem = Bounded(e.Message);
            launcher.DisableImage(TryImageFor(draft.Agent), problem);
            steps[1] = steps[1] with { Status = CheckStatus.Failed, Detail = problem };
            SkipFrom(2, "the agent image is not available");
            return steps;
        }
        // Every provider some job runs needs its CLI in the project's image.
        foreach (var type in AgentTypes.All.Where(draft.AgentTypesInUse.Contains))
        {
            var command = AgentProviderInfo.Command(type);
            bool has;
            try
            {
                has = await docker.HasCommandAsync(image, command, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                has = false;
                log?.LogWarning(e, "Container check of {Project}: could not probe {Command} in {Image}", draft.Id, command, image);
            }
            if (has) continue;
            steps[1] = steps[1] with { Status = CheckStatus.Failed,
                Detail = $"image has no {command} CLI; rebuild on symphonist/agent:2" };
            SkipFrom(2, "the agent image is not usable");
            return steps;
        }
        launcher.EnableImage(image);
        Set(1, CheckStatus.Passed, image);

        Set(2, CheckStatus.Running, "an agent is building and testing the repository");
        var (passed, detail) = await BuildAsync(draft, ct);
        Set(2, passed ? CheckStatus.Passed : CheckStatus.Failed, detail);
        return steps;
    }

    private async Task<(bool Passed, string Detail)> BuildAsync(ProjectConfig draft, CancellationToken ct)
    {
        try
        {
            return await BuildInFreshWorkspaceAsync(draft, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (false, Bounded(e.Message));
        }
        finally
        {
            // Not kept between runs; a leftover is removed again before the next one.
            try
            {
                await git.RemoveAsync(draft, WorkspaceKey, CancellationToken.None);
            }
            catch (Exception e)
            {
                log?.LogWarning(e, "Could not remove the container check workspace of {Project}", draft.Id);
            }
        }
    }

    private async Task<(bool Passed, string Detail)> BuildInFreshWorkspaceAsync(ProjectConfig draft, CancellationToken ct)
    {
        string workspace;
        try
        {
            // Always a fresh clone of the current localPath and main branch.
            await git.RemoveAsync(draft, WorkspaceKey, ct);
            workspace = await git.PrepareAsync(draft, WorkspaceKey, draft.Repo.MainBranch, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (false, Bounded($"cannot prepare the repository: {e.Message}"));
        }

        var prompt = PromptRenderer.Render(PromptLibrary.GetContainerCheck(), new Dictionary<string, string?>
        {
            ["project.name"] = ReviewerText.Line(draft.Name, ReviewerText.MaxItemChars),
        });

        var runner = agents.For(draft.Agent.Type);
        var startedAt = time.GetUtcNow();
        AgentRunResult result;
        try
        {
            // Writable, in the project's own container settings: the timeout is agent.timeoutMinutes.
            result = await runner.RunAsync(new AgentRunRequest(workspace, prompt, draft.Agent,
                ContainerCheckSchema.Json, ReadOnlyWorkspace: false), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The runner itself failed (e.g. Docker stopped since the first step).
            result = new AgentRunResult(false, null, null, time.GetUtcNow() - startedAt, e.Message, "");
        }

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", draft.Id, OnboardingProposer.WorkspaceKey, $"container-check-{stamp}.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            // Not cancellable: a cancel right after the run must not lose its log.
            await File.WriteAllTextAsync(logPath, result.RawLog, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The check does not depend on its log.
            log?.LogWarning(e, "Could not write the container check log {Path}", logPath);
        }
        log?.LogInformation("Container check for {Project}: run {Outcome} in {Duration}, cost {Cost}", draft.Id,
            result.Succeeded ? "succeeded" : "failed", result.Duration,
            result.CostUsd is { } cost ? cost.ToString("0.00##", CultureInfo.InvariantCulture) + " USD" : "unknown");

        // Structured output may be present on a failed run: only a successful run's output is read.
        if (!result.Succeeded) return (false, Bounded(result.Error ?? "container check run failed"));
        if (ContainerCheckParser.Parse(result.Structured) is not { } output)
            return (false, "the agent returned no usable structured output");

        var tests = output.TestsPassed is { } testsPassed
            ? $"tests: {output.TestCommand ?? "(command not reported)"} ({Word(testsPassed)})"
            : "no tests found";
        var detail = $"build: {output.BuildCommand} ({Word(output.BuildPassed)}); {tests}. {output.Summary}".TrimEnd();
        return (output.BuildPassed && output.TestsPassed != false, detail);
    }

    /// <summary>Exception and error text as one bounded line for the screen.</summary>
    private static string Bounded(string text) => ReviewerText.Line(text, ReviewerText.MaxSummaryChars);

    private static string Word(bool passed) => passed ? "passed" : "failed";

    /// <summary>The run image, or the configured name when it is invalid (EnsureImage then reports why).</summary>
    private static string TryImageFor(AgentSettings settings)
    {
        try { return DockerLauncher.ImageFor(settings); }
        catch (ArgumentException) { return settings.Image ?? DockerLauncher.DefaultImage; }
    }
}
