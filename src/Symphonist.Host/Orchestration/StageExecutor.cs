using System.Globalization;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Storage;
using Symphonist.Integrations.Git;

namespace Symphonist.Host.Orchestration;

public enum ExecutionStatus { Completed, InProgress, Failed }

public sealed record ExecutionResult(ExecutionStatus Status, string? Error = null, string? Event = null);

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
        catch (Exception e) when (e is GitCommandException or HttpRequestException or PromptTemplateException)
        {
            return Failed(e.Message);
        }
    }

    public async Task<ExecutionResult> PostApprovalAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, CancellationToken ct)
    {
        try
        {
            return p.Stage(t.StageId).Kind switch
            {
                StageKind.Implement => await MergeAsync(io.SourceControl, t.PullRequestId, hash => t.MergeCommit = hash, ct),
                StageKind.Release => await MergeAsync(io.SourceControl, t.ReleasePullRequestId, hash => t.ReleaseMergeCommit = hash, ct),
                _ => Completed,
            };
        }
        catch (Exception e) when (e is HttpRequestException)
        {
            return Failed(e.Message);
        }
    }

    private async Task<ExecutionResult> TriageAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source,
        CancellationToken ct)
    {
        var run = await RunAgentAsync(p, t, source, StageKind.Triage, ct);
        if (run.Failure is not null) return run.Failure;

        await io.Tracker.CommentAsync(t.Key, run.Output!.CommentMarkdown ?? run.Output.Summary, ct);
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

        if (!await git.CommitAllAsync(run.Workspace, $"{t.Key}: spec and plan", ct)) return Failed("agent made no changes");
        await git.PushAsync(run.Workspace, run.Branch, ct);
        t.SpecPath = output.SpecPath;
        t.PlanPath = output.PlanPath;

        var sc = io.SourceControl;
        await io.Tracker.CommentAsync(t.Key,
            $"{output.Summary}\n\nSpec: {sc.BranchFileUrl(run.Branch, output.SpecPath)}\nPlan: {sc.BranchFileUrl(run.Branch, output.PlanPath)}",
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

        if (!await git.CommitAllAsync(run.Workspace, title, ct)) return Failed("agent made no changes");
        await git.PushAsync(run.Workspace, run.Branch, ct);

        if (t.PullRequestId is not null)
        {
            await io.Tracker.CommentAsync(t.Key, "PR updated", ct);
            return Completed;
        }

        var description = output.PrDescription ?? output.Summary;
        if (source is not null) description += $"\n\nTicket: {source.WebUrl}";
        var pr = await FindOrOpenAsync(io.SourceControl, run.Branch, p.Repo.MainBranch, title, description, ct);
        t.PullRequestId = pr.Id;
        await io.Tracker.CommentAsync(t.Key, $"PR ready for review: {pr.WebUrl}", ct);
        return Completed;
    }

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
        if (t.ReleasePullRequestId is not null) return Completed;

        var title = $"Release {t.Key}";
        var pr = await FindOrOpenAsync(io.SourceControl, p.Repo.MainBranch, p.Repo.ProductionBranch, title,
            $"{title}: {t.Title}", ct);
        t.ReleasePullRequestId = pr.Id;
        await io.Tracker.CommentAsync(t.Key, $"Release PR: {pr.WebUrl}", ct);
        return Completed;
    }

    private static async Task<PullRequestInfo> FindOrOpenAsync(ISourceControl sc, string sourceBranch, string destination,
        string title, string description, CancellationToken ct) =>
        await sc.FindOpenPullRequestAsync(sourceBranch, destination, ct)
        ?? await sc.OpenPullRequestAsync(sourceBranch, destination, title, description, ct);

    private static async Task<ExecutionResult> MergeAsync(ISourceControl sc, long? id, Action<string?> setMergeCommit,
        CancellationToken ct)
    {
        if (id is not { } prId) return Failed("no pull request");

        var pr = await sc.GetPullRequestAsync(prId, ct);
        switch (pr.State)
        {
            case PullRequestState.Merged:
                setMergeCommit(pr.MergeCommitHash);
                return Completed;
            case PullRequestState.Declined:
                return Failed($"pull request {prId} was declined");
        }

        var merge = await sc.MergePullRequestAsync(prId, ct);
        if (!merge.Merged) return new ExecutionResult(ExecutionStatus.InProgress, Event: $"waiting to merge: {merge.Error}");
        setMergeCommit(merge.MergeCommitHash);
        return Completed;
    }

    private sealed record AgentStep(string Workspace, string Branch, StageOutput? Output, ExecutionResult? Failure);

    private async Task<AgentStep> RunAgentAsync(ProjectConfig p, Ticket t, SourceTicket? source, StageKind kind,
        CancellationToken ct)
    {
        var branch = t.Branch ?? $"symphonist/{t.Key}";
        var workspace = await git.PrepareAsync(p, t.Key, branch, ct);
        var prompt = PromptRenderer.Render(PromptLibrary.Get(p, kind), PromptValues(p, t, source, branch));

        var startedAt = time.GetUtcNow();
        var result = await agent.RunAsync(new AgentRunRequest(workspace, prompt, p.Agent), ct);

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", p.Id, t.Key, $"{t.StageId}-{stamp}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.RawLog, ct);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, t.StageId, startedAt, result.Duration, result.Succeeded,
            result.CostUsd, result.Error, logPath), ct);

        ExecutionResult? failure =
            !result.Succeeded ? Failed(result.Error ?? "agent run failed")
            : result.Output is null ? Failed("agent returned no structured output")
            : null;
        return new AgentStep(workspace, branch, result.Output, failure);
    }

    private static Dictionary<string, string?> PromptValues(ProjectConfig p, Ticket t, SourceTicket? source, string branch) => new()
    {
        ["ticket.key"] = t.Key,
        ["ticket.title"] = t.Title,
        ["ticket.description"] = source?.Description ?? "",
        ["ticket.comments"] = source is null
            ? ""
            : string.Join("\n", source.Comments.OrderBy(c => c.Created).Select(c => $"{c.Author}: {c.Body}")),
        ["ticket.url"] = source?.WebUrl.ToString() ?? "",
        ["project.name"] = p.Name,
        ["branch"] = branch,
        ["feedback"] = t.Feedback ?? "",
        ["spec_path"] = t.SpecPath ?? "",
        ["plan_path"] = t.PlanPath ?? "",
    };

    private static ExecutionResult Failed(string error) => new(ExecutionStatus.Failed, error);
}
