using System.Globalization;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Prompts;
using Symphonist.Core.Risk;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Onboarding;

/// <summary>
/// Runs the read-only onboarding agent once in a clone of the draft's repository and returns its proposal for the
/// status mapping, added stages and risk areas. Fails closed: every error gives <see cref="OnboardingProposal.Failed"/>.
/// Not a ticket agent run: nothing is stored, the raw log goes to <c>logs/{id}/_onboarding/</c>.
/// </summary>
public sealed class OnboardingProposer(IGitWorkspace git, IAgentRunners agents, TimeProvider time,
    ILogger<OnboardingProposer>? log = null)
{
    public const string WorkspaceKey = "_onboarding";     // no tracker key has this shape (Jira ABC-1, GitHub repo-1)

    /// <summary>Never throws for git or agent failures (returns <see cref="OnboardingProposal.Failed"/>); honours <paramref name="ct"/>.</summary>
    /// <param name="draft">A config that passed <see cref="ProjectConfigLoader.Parse"/> (validated id, site, owner).</param>
    public async Task<OnboardingProposal> ProposeAsync(ProjectConfig draft, ProjectForm form, TrackerDiscovery discovery,
        CancellationToken ct)
    {
        try
        {
            return await RunAsync(draft, form, discovery, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return OnboardingProposal.Failed(e.Message);
        }
    }

    private async Task<OnboardingProposal> RunAsync(ProjectConfig draft, ProjectForm form, TrackerDiscovery discovery,
        CancellationToken ct)
    {
        var runner = agents.For(draft.Review.Agent.Type);
        string workspace;
        try
        {
            workspace = await git.PrepareAsync(draft, WorkspaceKey, draft.Repo.MainBranch, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return OnboardingProposal.Failed($"cannot prepare the repository: {e.Message}");
        }

        // A random fence per run: tracker text inside cannot guess it, and forged markers inside are neutralised.
        var nonce = UntrustedFence.NewNonce();
        string Fence(string text) => UntrustedFence.Wrap(nonce, text);

        var values = new Dictionary<string, string?>
        {
            ["project.name"] = Fence(draft.Name),
            ["onboarding.tracker"] = discovery.StatusSource is { } source ? Fence(source) : "(no status source)",
            ["onboarding.statuses"] = discovery.Statuses.Count == 0
                ? "(no statuses: status source missing or unreadable)"
                : Fence(string.Join("\n", discovery.Statuses.Select(StatusLine))),
            ["onboarding.pipeline"] = string.Join("\n", form.Pipeline.Select(s => $"{s.Id} ({Lower(s.Kind)}, {Lower(s.Gate)})")),
            ["risk.areas"] = GateReviewer.RiskAreasText(form.RiskAreas
                .Select(a => new RiskArea(a.Name, a.Description, a.Paths.ToList())).ToList()),
            ["onboarding.fence_id"] = nonce,
        };
        var prompt = PromptRenderer.Render(PromptLibrary.GetOnboarding(), values);

        var startedAt = time.GetUtcNow();
        AgentRunResult result;
        try
        {
            result = await runner.RunAsync(new AgentRunRequest(workspace, prompt, draft.Review.Agent,
                OnboardingProposalSchema.Json, ReadOnlyWorkspace: true), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The runner itself failed (e.g. missing container image).
            result = new AgentRunResult(false, null, null, time.GetUtcNow() - startedAt, e.Message, "");
        }

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", draft.Id, WorkspaceKey, $"proposal-{stamp}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.RawLog, ct);
        log?.LogInformation("Onboarding proposal for {Project}: {Outcome} in {Duration}, cost {Cost}", draft.Id,
            result.Succeeded ? "succeeded" : "failed", result.Duration,
            result.CostUsd is { } cost ? cost.ToString("0.00##", CultureInfo.InvariantCulture) + " USD" : "unknown");

        // Structured output may be present on a failed run: only a successful run's output is read.
        return result.Succeeded
            ? OnboardingProposalParser.Parse(result.Structured, discovery.Statuses, form.Pipeline, form.RiskAreas)
            : OnboardingProposal.Failed(result.Error ?? "proposal run failed");
    }

    private static string StatusLine(SourceStatus s)
    {
        var category = s.Category switch
        {
            StatusCategory.ToDo => "to do",
            StatusCategory.InProgress => "in progress",
            StatusCategory.Done => "done",
            _ => "unknown",
        };
        return s.Description.Length == 0 ? $"{s.Name} [{category}]" : $"{s.Name} [{category}]: {s.Description}";
    }

    private static string Lower<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();
}
