using System.Globalization;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

public enum ExecutionStatus { Completed, InProgress, Failed }

/// <param name="MergeStarted">Post-approval: the host accepted the merge request and is still merging.</param>
public sealed record ExecutionResult(ExecutionStatus Status, string? Error = null, string? Event = null,
    bool MergeStarted = false);

/// <summary>
/// Performs each stage's side effects through the plug-in interfaces. Only mutates the ticket's artifact fields
/// (spec/plan paths, PR ids, merge commits); the caller applies the engine transition for the returned result.
/// </summary>
public sealed class StageExecutor(IGitWorkspace git, IAgentRunner agent, ITicketStore store, TimeProvider time)
{
    private static readonly ExecutionResult Completed = new(ExecutionStatus.Completed);
    private static readonly ExecutionResult InProgress = new(ExecutionStatus.InProgress);

    public static bool IsAgentStage(StageKind kind) => kind is StageKind.Triage or StageKind.Plan or StageKind.Implement;

    public async Task<ExecutionResult> ExecuteAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source,
        CancellationToken ct)
    {
        try
        {
            return p.Stage(t.StageId).Kind switch
            {
                StageKind.Triage => await TriageAsync(p, io, t, source, ct),
                StageKind.Plan => await PlanAsync(p, io, t, source, ct),
                StageKind.Implement => await ImplementAsync(p, io, t, source, ct),
                StageKind.Build => await WatchBuildAsync(p, io, t, t.MergeCommit, ct),
                StageKind.Release => await ReleaseAsync(p, io, t, ct),
                StageKind.Deploy => await WatchBuildAsync(p, io, t, t.ReleaseMergeCommit, ct),
                _ => Completed, // Uat, Done
            };
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failed(e.Message);
        }
    }

    /// <param name="beforeMerge">
    /// Asked only right before a merge is requested, with the open pull request as just read; a non-null reason means
    /// no merge, and the result is InProgress with the reason as its event. A merged or declined pull request never asks it.
    /// </param>
    public async Task<ExecutionResult> PostApprovalAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, CancellationToken ct,
        Func<PullRequestInfo, CancellationToken, Task<string?>>? beforeMerge = null)
    {
        try
        {
            return p.Stage(t.StageId).Kind switch
            {
                StageKind.Implement => await MergeAsync(io.SourceControl, t.PullRequestId, hash => t.MergeCommit = hash,
                    beforeMerge, ct),
                // No release PR but a release commit: production already contained main, nothing to merge.
                StageKind.Release when t is { ReleasePullRequestId: null, ReleaseMergeCommit: not null } => Completed,
                StageKind.Release => await MergeAsync(io.SourceControl, t.ReleasePullRequestId, hash => t.ReleaseMergeCommit = hash,
                    beforeMerge, ct),
                _ => Completed,
            };
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failed(e.Message);
        }
    }

    private async Task<ExecutionResult> TriageAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source,
        CancellationToken ct)
    {
        var run = await RunAgentAsync(p, t, source, StageKind.Triage, ct);
        if (run.Failure is not null) return run.Failure;

        await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign(run.Output!.CommentMarkdown ?? run.Output.Summary), ct);
        return Completed;
    }

    private async Task<ExecutionResult> PlanAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source,
        CancellationToken ct)
    {
        var run = await RunAgentAsync(p, t, source, StageKind.Plan, ct);
        if (run.Failure is not null) return run.Failure;
        var output = run.Output!;
        if (string.IsNullOrWhiteSpace(output.SpecPath) || string.IsNullOrWhiteSpace(output.PlanPath))
            return Failed("agent did not report spec_path and plan_path");

        if (!await CommitAsync(p, run.Workspace, $"{t.Key}: spec and plan", ct)) return Failed("agent made no changes");
        await git.PushAsync(run.Workspace, run.Branch, ct);
        t.SpecPath = output.SpecPath;
        t.PlanPath = output.PlanPath;

        var sc = io.SourceControl;
        await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign(
            $"{output.Summary}\n\nSpec: {sc.BranchFileUrl(run.Branch, output.SpecPath)}\nPlan: {sc.BranchFileUrl(run.Branch, output.PlanPath)}"),
            ct);
        return Completed;
    }

    private async Task<ExecutionResult> ImplementAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source,
        CancellationToken ct)
    {
        var run = await RunAgentAsync(p, t, source, StageKind.Implement, ct);
        if (run.Failure is not null) return run.Failure;
        var output = run.Output!;
        var title = $"{t.Key}: {output.PrTitle ?? t.Title}";

        if (!await CommitAsync(p, run.Workspace, title, ct)) return Failed("agent made no changes");
        await git.PushAsync(run.Workspace, run.Branch, ct);

        if (t.PullRequestId is not null)
        {
            await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign("PR updated"), ct);
            return Completed;
        }

        var description = output.PrDescription ?? output.Summary;
        if (source is not null) description += $"\n\nTicket: {source.WebUrl}";
        var pr = await FindOrOpenAsync(io.SourceControl, run.Branch, p.Repo.MainBranch, title, description, ct);
        t.PullRequestId = pr.Id;
        await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign($"PR ready for review: {pr.WebUrl}"), ct);
        return Completed;
    }

    /// <summary>
    /// Commits the agent's changes. A clean tree still counts as work when the branch already has commits ahead of main
    /// (an earlier attempt committed, then failed to push / open the PR / comment), so that retry can finish the job.
    /// </summary>
    private async Task<bool> CommitAsync(ProjectConfig p, string workspace, string message, CancellationToken ct) =>
        await git.CommitAllAsync(workspace, message, ct)
        || await git.HasCommitsAheadAsync(workspace, p.Repo.MainBranch, ct);

    private async Task<ExecutionResult> WatchBuildAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, string? commit,
        CancellationToken ct)
    {
        if (commit is null) return Failed("no merge commit");
        return await io.BuildWatcher.GetStatusAsync(commit, ct) switch
        {
            BuildStatus.Succeeded => Completed,
            BuildStatus.Failed => Failed("build failed"),
            BuildStatus.Running => InProgress,
            _ when t.StageStartedAt is { } started
                   && time.GetUtcNow() - started >= TimeSpan.FromMinutes(p.Build.TimeoutMinutes)
                => Failed($"no pipeline ran for {commit}"),
            _ => InProgress,
        };
    }

    private static async Task<ExecutionResult> ReleaseAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, CancellationToken ct)
    {
        var sc = io.SourceControl;
        if (t.ReleasePullRequestId is { } existing && (await sc.GetPullRequestAsync(existing, ct)).State == PullRequestState.Open)
            return Completed;

        // First release, or re-entered (send-back / unblock) after an earlier release finished: start over.
        t.ReleasePullRequestId = null;
        t.ReleaseMergeCommit = null;

        var (main, production) = (p.Repo.MainBranch, p.Repo.ProductionBranch);
        var title = $"Release {t.Key}";
        PullRequestInfo pr;
        try
        {
            pr = await FindOrOpenAsync(sc, main, production, title, $"{title}: {t.Title}", ct);
        }
        catch (NoChangesToPullException e)
        {
            // Production already contains main (e.g. released together with another ticket): deploy watches its head.
            // Fail closed: completing here moves the ticket towards Done, so check the host's claim independently.
            string? doubt;
            try
            {
                doubt = await sc.BranchContainsAsync(production, main, ct) ? null : $"{production} does not contain {main}";
            }
            catch (Exception verify) when (!ct.IsCancellationRequested)
            {
                doubt = $"could not verify that {production} contains {main}: {verify.Message}";
            }
            if (doubt is not null) return Failed($"{e.Message} (but {doubt})");

            var head = await sc.GetBranchHeadAsync(production, ct);
            t.ReleaseMergeCommit = head;
            await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign($"Nothing to release: {production} already contains {main}."), ct);
            return new ExecutionResult(ExecutionStatus.Completed,
                Event: $"already in production: {production} already contains {main} (at {head})");
        }

        t.ReleasePullRequestId = pr.Id;
        await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign($"Release PR: {pr.WebUrl}"), ct);
        return Completed;
    }

    private static async Task<PullRequestInfo> FindOrOpenAsync(ISourceControl sc, string sourceBranch, string destination,
        string title, string description, CancellationToken ct) =>
        await sc.FindOpenPullRequestAsync(sourceBranch, destination, ct)
        ?? await sc.OpenPullRequestAsync(sourceBranch, destination, title, description, ct);

    private static async Task<ExecutionResult> MergeAsync(ISourceControl sc, long? id, Action<string> setMergeCommit,
        Func<PullRequestInfo, CancellationToken, Task<string?>>? beforeMerge, CancellationToken ct)
    {
        if (id is not { } prId) return Failed("no pull request");

        var pr = await sc.GetPullRequestAsync(prId, ct);
        switch (pr.State)
        {
            case PullRequestState.Merged:
                return MergedAs(pr.MergeCommitHash, setMergeCommit);
            case PullRequestState.Declined:
                return Failed($"pull request {prId} was declined");
        }

        if (beforeMerge is not null && await beforeMerge(pr, ct) is { } held)
            return new ExecutionResult(ExecutionStatus.InProgress, Event: held);
        var merge = await sc.MergePullRequestAsync(prId, ct);
        if (!merge.Merged)
            return new ExecutionResult(ExecutionStatus.InProgress, Event: $"waiting to merge: {merge.Error}",
                MergeStarted: merge.Accepted);
        return MergedAs(merge.MergeCommitHash, setMergeCommit);
    }

    private static ExecutionResult MergedAs(string? hash, Action<string> setMergeCommit)
    {
        if (string.IsNullOrEmpty(hash)) return Failed("merged but no merge commit hash");
        setMergeCommit(hash);
        return Completed;
    }

    private sealed record AgentStep(string Workspace, string Branch, StageOutput? Output, ExecutionResult? Failure);

    private async Task<AgentStep> RunAgentAsync(ProjectConfig p, Ticket t, SourceTicket? source, StageKind kind,
        CancellationToken ct)
    {
        var branch = t.Branch ?? $"symphonist/{t.Key}";
        var workspace = await git.PrepareAsync(p, t.Key, branch, ct);
        var prompt = PromptRenderer.Render(PromptLibrary.Get(p, kind), PromptValues.For(p, t, source, branch, t.Feedback ?? ""));

        var startedAt = time.GetUtcNow();
        AgentRunResult result;
        try
        {
            result = await agent.RunAsync(new AgentRunRequest(workspace, prompt, p.Agent), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The runner itself failed (e.g. missing container image): still record the attempt.
            result = new AgentRunResult(false, null, null, time.GetUtcNow() - startedAt, e.Message, "");
        }

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", p.Id, t.Key, $"{t.StageId}-{stamp}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.RawLog, ct);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, t.StageId, startedAt, result.Duration, result.Succeeded,
            result.CostUsd, result.Error, logPath, result.Output?.Summary), ct);

        ExecutionResult? failure =
            !result.Succeeded ? Failed(result.Error ?? "agent run failed")
            : result.Output is null ? Failed("agent returned no structured output")
            : null;
        return new AgentStep(workspace, branch, result.Output, failure);
    }

    private static ExecutionResult Failed(string error) => new(ExecutionStatus.Failed, error);
}
