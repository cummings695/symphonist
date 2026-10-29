using System.Collections.Concurrent;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Evidence;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;
using Symphonist.Host.Notifications;

namespace Symphonist.Host.Orchestration;

/// <param name="TickAfterCommands">An approve / send-back / unblock that changed a ticket starts a tick right away
/// (coalesced) instead of waiting for the next poll.</param>
public sealed record OrchestratorOptions(TimeSpan PollInterval, int MaxConcurrentAgentRuns, bool TickAfterCommands = true)
{
    public OrchestratorOptions() : this(TimeSpan.FromMinutes(2), 1) { }
}

public enum ForgetResult { Forgotten, NotFound, Running }

/// <summary>
/// Syncs tracker tickets into the store, drives each ticket through the pipeline, and mirrors state back to the tracker.
/// Ticks, commands (approve / send back / unblock) and agent-run and review completions all run under one lock, and
/// every one of them loads the ticket fresh from the store, so no two writers ever save a ticket from stale copies.
/// A job (stage run, review or evidence) starts only while its provider is not paused by a usage limit
/// (<see cref="ProviderLimits"/>); a run the limit stops pauses the provider and costs the ticket nothing.
/// </summary>
public sealed class Orchestrator(ProjectRegistry projects, ITicketStore store, IIntegrationFactory integrations,
    StageExecutor executor, GateReviewer reviewer, EvidenceRunner evidence, ProviderLimits limits, TimeProvider time,
    OrchestratorOptions options, ILogger<Orchestrator> log, NotificationCenter? notifications = null) : IDisposable
{
    private const string LeftQuery = "no longer matches tracker query";

    private static string StageRemoved(string stageId) =>
        $"stage '{stageId}' is no longer in the pipeline; send it back to an existing stage";

    /// <summary>
    /// How long a merge the host accepted (and performs asynchronously) is given to land before it is requested again,
    /// through the usual head check.
    /// </summary>
    public static readonly TimeSpan MergeAcceptedWait = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Task> inFlight = new();
    /// <summary>
    /// The evidence run of each ticket, with its gate round, cancelled once that round is decided. Added, cancelled and
    /// removed under gate only; its CTS is disposed only after removal, so a cancel never meets a disposed CTS.
    /// </summary>
    private readonly Dictionary<Guid, (DateTimeOffset WaitingSince, CancellationTokenSource Cts)> evidenceRuns = [];
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<string, string> problems = new(StringComparer.Ordinal);
    private CoalescedTrigger? tickTrigger;

    /// <summary>The background tick requested by commands (created on first use).</summary>
    private CoalescedTrigger TickTrigger => LazyInitializer.EnsureInitialized(ref tickTrigger, () => new CoalescedTrigger(async () =>
    {
        try
        {
            await TickAsync(shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }, log));

    /// <summary>Raised when a project's problem is set to a different value or cleared.</summary>
    public event Action? ProblemsChanged;

    /// <summary>Starts a background tick now (coalesced with ticks already requested).</summary>
    public void RequestTick() => TickTrigger.Request();

    private readonly Dictionary<string, Dictionary<string, SourceTicket>> latestSources = []; // used under gate only
    /// <summary>Tickets whose pending tracker move already has a failure event (one per retry episode); under gate only.</summary>
    private readonly HashSet<Guid> retryFailureRecorded = [];
    // Notified rounds and blocks, used under gate only. In memory: a restart may notify once more.
    private readonly Dictionary<Guid, DateTimeOffset> notifiedRounds = [];
    private readonly HashSet<Guid> notifiedBlocked = [];

    /// <summary>The last integration error of a project's tick (missing secret, failed tracker query); null once a tick succeeds.</summary>
    public string? ProblemOf(string projectId) => problems.GetValueOrDefault(projectId);

    private void SetProblem(string projectId, string problem)
    {
        var previous = problems.GetValueOrDefault(projectId);
        problems[projectId] = problem;
        if (previous != problem) ProblemsChanged?.Invoke();
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        var reset = await store.ResetInterruptedRunsAsync(IsAgentStage, time.GetUtcNow(), ct);
        if (reset > 0) log.LogInformation("Reset {Count} agent run(s) interrupted by a restart", reset);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ForgetRemovedProjects();
            foreach (var p in projects.Projects)
            {
                if (projects.IsPaused(p.Id)) continue;
                try
                {
                    await TickProjectAsync(p, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogError(e, "Tick failed for project {Project}", p.Id);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reloads the project files between ticks and commands, never during one, then starts a tick (when commands do) so
    /// added projects are polled right away. Agent runs in flight finish with the configuration they started with.
    /// </summary>
    public async Task<ProjectReload> ReloadProjectsAsync(CancellationToken ct)
    {
        var reload = await WithProjectsLockedAsync(() => Task.FromResult(projects.Reload()), ct);
        if (options.TickAfterCommands) RequestTick();
        return reload;
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the lock ticks and commands hold, so the projects and tickets it checks (e.g.
    /// before saving a project file and reloading) cannot change under it.
    /// </summary>
    public async Task<T> WithProjectsLockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drops the problems and query results of projects no longer loaded; under gate only.</summary>
    private void ForgetRemovedProjects()
    {
        var loaded = projects.Projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in latestSources.Keys.Where(id => !loaded.Contains(id)).ToList()) latestSources.Remove(id);
        var removed = false;
        foreach (var id in problems.Keys.Where(id => !loaded.Contains(id)).ToList()) removed |= problems.TryRemove(id, out _);
        if (removed) ProblemsChanged?.Invoke();
    }

    /// <summary>
    /// Completes once no agent runs are in flight, all their results have been applied, and no background tick
    /// (requested by a command) is running or pending.
    /// </summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            await (tickTrigger?.WhenIdleAsync() ?? Task.CompletedTask);
            if (inFlight.Values.ToArray() is not { Length: > 0 } running) return;
            await Task.WhenAll(running);
        }
    }

    /// <param name="expectedStageId">The stage the developer saw waiting (dashboard); when the ticket is at another stage
    /// nothing changes, so a stale view can never approve a later gate (e.g. a production deploy).</param>
    /// <param name="expectedWaitingSince">The gate round the developer saw; a different round changes nothing.</param>
    public Task<TransitionOutcome> ApproveAsync(string projectId, string key, CancellationToken ct,
        string? expectedStageId = null, DateTimeOffset? expectedWaitingSince = null) =>
        CommandAsync(projectId, key, (p, t, now) =>
            expectedStageId is not null && t.StageId != expectedStageId
                ? new TransitionOutcome(false, null, $"{expectedStageId} is not awaiting approval")
            : expectedWaitingSince is { } seen && t.Status == StageStatus.AwaitingApproval && t.WaitingSince != seen
                ? new TransitionOutcome(false, null, $"{t.StageId} is waiting on a newer round; reload to see it")
            : PipelineEngine.Approve(p, t, now), ct);

    public Task<TransitionOutcome> SendBackAsync(string projectId, string key, string feedback, string? toStageId,
        CancellationToken ct) =>
        CommandAsync(projectId, key, (p, t, now) => PipelineEngine.SendBack(p, t, feedback, toStageId, now), ct);

    public Task<TransitionOutcome> UnblockAsync(string projectId, string key, CancellationToken ct) =>
        CommandAsync(projectId, key, (_, t, now) => PipelineEngine.Unblock(t, now), ct);

    /// <summary>
    /// Removes a ticket (with its events and runs) from Symphonist only; the tracker, repo and workspace are untouched.
    /// Refused while an agent or review run for it is in flight, since the run's completion would write to the deleted
    /// ticket.
    /// </summary>
    public async Task<ForgetResult> ForgetAsync(string projectId, string key, CancellationToken ct)
    {
        projects.Get(projectId); // unknown project: KeyNotFoundException, like the other commands
        await gate.WaitAsync(ct);
        try
        {
            if (await store.FindAsync(projectId, key, ct) is not { } t) return ForgetResult.NotFound;
            if (inFlight.ContainsKey(t.Id)) return ForgetResult.Running;
            retryFailureRecorded.Remove(t.Id);
            return await store.DeleteAsync(projectId, key, ct) ? ForgetResult.Forgotten : ForgetResult.NotFound;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Cancels in-flight agent runs; their runners clean up (e.g. remove the container) before the runs complete. The
    /// tickets stay Running and are reset by <see cref="RecoverAsync"/> at the next start. Await
    /// <see cref="WhenIdleAsync"/> afterwards for the runs to finish.
    /// </summary>
    public void CancelRuns() => shutdown.Cancel();

    public void Dispose() => CancelRuns();

    private async Task<TransitionOutcome> CommandAsync(string projectId, string key,
        Func<ProjectConfig, Ticket, DateTimeOffset, TransitionOutcome> apply, CancellationToken ct)
    {
        TransitionOutcome outcome;
        await gate.WaitAsync(ct);
        try
        {
            var p = projects.Get(projectId); // under the lock: a reload never changes the project during a command
            var t = await store.FindAsync(projectId, key, ct)
                    ?? throw new KeyNotFoundException($"Ticket '{key}' not found in project '{projectId}'");
            var fromStageId = t.StageId;
            outcome = apply(p, t, time.GetUtcNow());
            if (outcome.Changed)
            {
                await store.SaveAsync(t, ct);
                // Resolved inside ApplyAsync: a missing secret must not undo (or hide) a command that took effect.
                await ApplyAsync(() => IntegrationsFor(p), t, fromStageId, outcome, ct);
            }
        }
        finally
        {
            gate.Release();
        }

        // Act on the change now (e.g. start the next stage's run) rather than at the next poll.
        if (outcome.Changed && options.TickAfterCommands) RequestTick();
        return outcome;
    }

    private async Task TickProjectAsync(ProjectConfig p, CancellationToken ct)
    {
        ProjectIntegrations io;
        try
        {
            io = IntegrationsFor(p);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // E.g. a missing secret: nothing can run for this project, so make it visible (symphonist projects).
            SetProblem(p.Id, e.Message);
            log.LogError("Project {Project} cannot run: {Error}", p.Id, e.Message);
            return;
        }

        var sources = new Dictionary<string, SourceTicket>(StringComparer.Ordinal);
        IReadOnlyList<SourceTicket>? results = null;
        try
        {
            results = await io.Tracker.QueryAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            SetProblem(p.Id, $"tracker query failed: {e.Message}");
            log.LogWarning(e, "Tracker query failed for project {Project}; skipping sync this tick", p.Id);
        }

        if (results is not null)
        {
            if (problems.TryRemove(p.Id, out _)) ProblemsChanged?.Invoke();
            foreach (var s in results) sources.TryAdd(s.Key, s);
            latestSources[p.Id] = sources;
            await SyncAsync(p, io, sources, ct);
        }

        foreach (var t in await store.ListAsync(p.Id, ct))
        {
            if (!IsActive(t) || inFlight.ContainsKey(t.Id)) continue;
            try
            {
                if (!HasStage(p, t))
                {
                    // A config edit removed its stage: nothing can run it; the developer sends it back to a stage.
                    var outcome = PipelineEngine.Block(t, StageRemoved(t.StageId), time.GetUtcNow());
                    await store.SaveAsync(t, ct);
                    await ApplyAsync(io, t, t.StageId, outcome, ct);
                    continue;
                }
                await AdvanceAsync(p, io, t, sources.GetValueOrDefault(t.Key), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError(e, "Advancing {Project}/{Key} failed", p.Id, t.Key);
                await TryAddEventOnceAsync(t, $"{t.StageId} error: {e.Message}");
            }
        }
    }

    private async Task SyncAsync(ProjectConfig p, ProjectIntegrations io, Dictionary<string, SourceTicket> sources,
        CancellationToken ct)
    {
        var known = (await store.ListAsync(p.Id, ct)).ToDictionary(t => t.Key, StringComparer.Ordinal);
        var now = time.GetUtcNow();

        foreach (var s in sources.Values)
        {
            var t = known.GetValueOrDefault(s.Key) ?? await store.FindAsync(p.Id, s.Key, ct);
            if (t is null)
            {
                t = PipelineEngine.Create(p, s.Key, s.Title, s.Status, now);
                await store.SaveAsync(t, ct);
                await AddEventAsync(t, $"picked up at {t.StageId}", ct);
                continue;
            }
            if (t.Status != StageStatus.Done && !HasStage(p, t))
            {
                // Its stage was removed by a config edit (the ticket is blocked for it): a card move or @claude comment
                // cannot be mapped from a stage that no longer exists, so it is noted as seen and ignored.
                if (!Same(t.LastSourceStatus, s.Status))
                {
                    t.LastSourceStatus = s.Status;
                    t.TrackerRetryStatus = null;
                    await store.SaveAsync(t, ct);
                }
                continue;
            }
            if (Same(t.LastSourceStatus, s.Status))
            {
                // A @claude rerun's own move replaces the retry, so the retry waits for a tick without one.
                if (!await HandleClaudeCommentsAsync(p, io, t, s, now, ct)) await RetryTrackerMoveAsync(io, t, ct);
                continue;
            }

            t.TrackerRetryStatus = null; // the developer moved the card; never move it back to the old target
            if (t.Status == StageStatus.Done)
            {
                t.LastSourceStatus = s.Status;
                await store.SaveAsync(t, ct);
                await AddEventAsync(t,
                    $"card moved to '{s.Status}' after the ticket was done; Symphonist does not reopen done tickets", ct);
                continue;
            }

            var fromStageId = t.StageId;
            var outcome = PipelineEngine.SourceStatusChanged(p, t, s.Status, LatestFeedback(s, t), now);
            await store.SaveAsync(t, ct); // LastSourceStatus changes even when the outcome doesn't
            await ApplyAsync(io, t, fromStageId, outcome, ct);
        }

        foreach (var t in known.Values.Where(t => IsActive(t) && !sources.ContainsKey(t.Key)))
        {
            var outcome = PipelineEngine.Block(t, LeftQuery, now);
            await store.SaveAsync(t, ct);
            await ApplyAsync(io, t, t.StageId, outcome, ct); // blocking keeps the stage
        }
    }

    /// <summary>
    /// Retries a tracker move that was skipped or threw, while the card is still where Symphonist last saw it. A failure
    /// is recorded once per move, not on every tick of an outage (whatever the error text).
    /// </summary>
    private async Task RetryTrackerMoveAsync(ProjectIntegrations io, Ticket t, CancellationToken ct)
    {
        if (t.TrackerRetryStatus is not { } target) return; // also a done ticket's final move
        bool moved;
        try
        {
            moved = await io.Tracker.TransitionAsync(t.Key, target, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Retried transition of {Key} to {Status} failed", t.Key, target);
            if (retryFailureRecorded.Add(t.Id))
                await AddEventAsync(t, $"tracker transition to '{target}' failed: {e.Message}", ct);
            return;
        }

        if (moved) t.LastSourceStatus = target;
        t.TrackerRetryStatus = null;
        retryFailureRecorded.Remove(t.Id);
        await store.SaveAsync(t, ct);
        await AddEventAsync(t, moved ? $"tracker moved to '{target}' on retry" : $"no transition to '{target}' in tracker", ct);
    }

    /// <summary>
    /// Developer comments starting with the project's comment marker made since the stage started rerun it, with the comments as feedback, once it waits
    /// at its gate or is blocked; comments made while it is pending or running wait for that. At implement, "@claude"
    /// comments on its pull request do too (from teammates as well when the project allows it), combined with the
    /// tracker's into one run. Returns whether it reran.
    /// </summary>
    private async Task<bool> HandleClaudeCommentsAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket s,
        DateTimeOffset now, CancellationToken ct)
    {
        if (t.Status is not (StageStatus.AwaitingApproval or StageStatus.Blocked)) return false;
        var since = Later(t.ClaudeCommentsHandledAt, t.StageStartedAt);
        var comments = s.Comments
            .Where(c => c.IsMine && !TrackerComments.IsSymphonist(c.Body) && (since is null || c.Created > since))
            .Select(c => (c.Created, Request: TrackerComments.ClaudeRequest(c.Body, p.CommentMarker)))
            .Where(c => c.Request is not null)
            .OrderBy(c => c.Created)
            .ToList();
        var prComments = await PrClaudeCommentsAsync(p, io, t, ct);
        if (comments.Count == 0 && prComments.Count == 0) return false;

        var requests = comments.Select(c => c.Request!).Where(r => r.Length > 0).ToList();
        var feedback = requests.Count > 0 ? string.Join("\n\n", requests)
            : comments.Count > 0 ? "(no details given)"
            : "";
        await AddEventAsync(t, prComments.Count == 0 // the cause before its effects
            ? $"{p.CommentMarker} comment: rerunning {t.StageId}"
            : $"{p.CommentMarker} PR comment from {string.Join(", ", prComments.Select(c => c.Author).Distinct(StringComparer.Ordinal))}: rerunning {t.StageId}",
            ct);
        var stageId = t.StageId;
        var outcome = PipelineEngine.SendBack(p, t, feedback, stageId, now);
        // Only the developer's own comments decide the gate round; a teammate's request reruns without deciding it.
        if (comments.Count == 0 && !prComments.Any(c => c.IsMine)) outcome = outcome with { Decision = null, Touch = null };
        if (prComments.Count > 0)
        {
            t.PrFeedbackJson = PrFeedback.ToJson(prComments.Select(c => PrFeedback.From(c, p.CommentMarker)).ToList()); // after the send-back clears it
            t.PrCommentsHandledAt = prComments[^1].Created;
        }
        if (comments.Count > 0) t.ClaudeCommentsHandledAt = comments[^1].Created;
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, stageId, outcome, ct);
        return true;
    }

    /// <summary>
    /// The marker comments on the implement stage's pull request not yet acted on, oldest first; none at other stages.
    /// A listing error is recorded once and counts as none.
    /// </summary>
    private async Task<IReadOnlyList<PullRequestComment>> PrClaudeCommentsAsync(ProjectConfig p, ProjectIntegrations io,
        Ticket t, CancellationToken ct)
    {
        if (p.Stage(t.StageId).Kind != StageKind.Implement || t.PullRequestId is not { } prId) return [];
        try
        {
            return PrFeedback.Select(await io.SourceControl.ListPullRequestCommentsAsync(prId, ct),
                Later(t.PrCommentsHandledAt, t.StageStartedAt), p.PrComments.FromOthers, p.CommentMarker);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Comments of pull request {Id} for {Key} could not be read", prId, t.Key);
            await AddEventOnceAsync(t, $"could not read PR comments: {e.Message}", ct);
            return [];
        }
    }

    /// <summary>
    /// After implement's pull request merged, tells each marker comment that arrived too late (while the stage was
    /// approved) that it was not addressed. Best effort: failures are only logged.
    /// </summary>
    private async Task ReplyMergedFirstAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, DateTimeOffset? since,
        CancellationToken ct)
    {
        if (t.PullRequestId is not { } prId) return;
        IReadOnlyList<PullRequestComment> late;
        try
        {
            late = PrFeedback.Select(await io.SourceControl.ListPullRequestCommentsAsync(prId, ct), since,
                p.PrComments.FromOthers, p.CommentMarker);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Comments of merged pull request {Id} for {Key} could not be read", prId, t.Key);
            return;
        }
        foreach (var c in late)
        {
            try
            {
                await io.SourceControl.ReplyToPullRequestCommentAsync(prId, c, TrackerComments.Sign(MergedFirst), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Reply to comment {Comment} on pull request {Id} for {Key} failed", c.Id, prId, t.Key);
            }
        }
        if (late.Count > 0) t.PrCommentsHandledAt = late[^1].Created;
    }

    private const string MergedFirst =
        "Merged before this could be addressed; open a follow-up ticket if it still needs a change.";

    /// <summary>The later of the two: entering a stage leaves earlier comments behind.</summary>
    private static DateTimeOffset? Later(DateTimeOffset? handled, DateTimeOffset? stageStarted) =>
        handled is { } h && (stageStarted is not { } started || h > started) ? h : stageStarted;

    private async Task AdvanceAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        var agentStage = StageExecutor.IsAgentStage(p.Stage(t.StageId).Kind);
        switch (t.Status)
        {
            case StageStatus.Pending when agentStage:
                if (inFlight.Count >= options.MaxConcurrentAgentRuns) return;
                // A paused provider leaves the ticket exactly as it is until the reset (or the next probe).
                if (!await limits.TryStartAsync(UsageLimits.KeyFor(p.AgentFor(t.StageId)), ct)) return;
                t.Status = StageStatus.Running;
                t.UpdatedAt = time.GetUtcNow();
                await store.SaveAsync(t, ct);
                inFlight[t.Id] = RunAgentStageAsync(p, io, t.Clone(), source);
                return;

            case StageStatus.AwaitingApproval
                when t is { ReviewDue: true, WaitingSince: not null } && p.Reviews(p.Stage(t.StageId)):
                // The round's review (also one interrupted by a restart): runs like an agent stage; nothing is saved.
                if (inFlight.Count >= options.MaxConcurrentAgentRuns) return;
                if (!await limits.TryStartAsync(UsageLimits.KeyFor(p.Review.Agent), ct)) return;
                inFlight[t.Id] = RunReviewAsync(p, io, t.Clone(), source);
                await AddEventOnceAsync(t, $"reviewing {t.StageId}", ct);
                return;

            case StageStatus.AwaitingApproval when PipelineEngine.WaitsOnEvidence(p, t):
            {
                // The round's evidence (also one interrupted by a restart), after its review; cancelled when the round
                // is decided (see ApplyAsync). Nothing is saved until it completes.
                if (inFlight.Count >= options.MaxConcurrentAgentRuns) return;
                if (!await limits.TryStartAsync(UsageLimits.KeyFor(p.Evidence.Agent), ct)) return;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                evidenceRuns[t.Id] = (t.WaitingSince!.Value, cts);
                inFlight[t.Id] = RunEvidenceAsync(p, io, t.Clone(), source, cts);
                await AddEventOnceAsync(t, $"gathering evidence for {t.StageId}", ct);
                return;
            }

            case StageStatus.Running when agentStage:
                // Not in flight (in-flight tickets are skipped), so its run's result was lost: retry like a restart.
                t.Status = StageStatus.Pending;
                t.Attempts++;
                t.UpdatedAt = time.GetUtcNow();
                await store.SaveAsync(t, ct);
                await AddEventAsync(t, "run lost; retrying", ct);
                return;

            case StageStatus.Pending or StageStatus.Running when !agentStage:
            {
                var fromStageId = t.StageId;
                var result = await executor.ExecuteAsync(p, io, t, source, ct);
                var now = time.GetUtcNow();
                var outcome = result.Status switch
                {
                    ExecutionStatus.Completed => PipelineEngine.StageSucceeded(p, t, now, source is null ? null : LatestFeedback(source, t)),
                    ExecutionStatus.Failed => PipelineEngine.StageFailed(p, t, result.Error ?? "stage failed", now),
                    _ => null,
                };
                if (outcome is null && t.Status != StageStatus.Running)
                {
                    t.Status = StageStatus.Running;
                    t.UpdatedAt = now;
                }
                await store.SaveAsync(t, ct); // also persists artifacts the executor recorded (e.g. release PR id)
                if (result.Event is not null) await AddEventOnceAsync(t, result.Event, ct);
                if (outcome is not null) await ApplyAsync(io, t, fromStageId, outcome, ct);
                return;
            }

            case StageStatus.Approved:
            {
                var fromStageId = t.StageId;
                // Checked only when a merge is about to be requested: a pull request already merged (by the host, by
                // hand, or before a crash) has moved the branches itself and completes normally.
                string? holdReason = null;
                var result = await executor.PostApprovalAsync(p, io, t, ct, async (pr, c) =>
                {
                    // An accepted merge still under way on the host: don't ask again yet (nor merge unchecked later).
                    if (t.MergeAcceptedAt is { } accepted && time.GetUtcNow() - accepted < MergeAcceptedWait)
                        return "waiting for the host to finish merging";
                    t.MergeAcceptedAt = null;
                    return holdReason = await MergeHeldAsync(p, io, t, pr, c);
                });
                var now = time.GetUtcNow();
                if (holdReason is not null)
                {
                    // The reviewed change is not what would be merged now: wait again, as a new round.
                    var held = PipelineEngine.MergeHeld(p, t, holdReason, now);
                    await store.SaveAsync(t, ct);
                    await ApplyAsync(io, t, fromStageId, held, ct);
                    return;
                }
                // The host is merging the commits just checked. The recorded heads stay: if the merge never lands, a later
                // request is checked against them again.
                if (result.MergeStarted) t.MergeAcceptedAt = now;
                TransitionOutcome? outcome = null;
                switch (result.Status)
                {
                    case ExecutionStatus.Completed:
                        if (p.Stage(fromStageId).Kind == StageKind.Implement)
                        {
                            // Before advancing: entering the next stage moves StageStartedAt, which the window starts at.
                            var since = Later(t.PrCommentsHandledAt, t.StageStartedAt);
                            await ReplyMergedFirstAsync(p, io, t, since, ct);
                        }
                        outcome = PipelineEngine.PostApprovalCompleted(p, t, now);
                        break;
                    case ExecutionStatus.Failed:
                        // Count the attempt, but retry the post-approval action (merge) rather than rerunning the stage.
                        outcome = PipelineEngine.StageFailed(p, t, result.Error ?? "post-approval action failed", now);
                        if (t.Status == StageStatus.Pending) t.Status = StageStatus.Approved;
                        break;
                }
                await store.SaveAsync(t, ct); // persists the merge commit
                if (outcome is not null) await ApplyAsync(io, t, fromStageId, outcome, ct);
                else await AddEventOnceAsync(t, result.Event ?? $"{t.StageId} waiting", ct);
                return;
            }
        }
    }

    /// <summary>Runs an agent stage on a private copy, then applies the result to the ticket re-loaded under the lock.</summary>
    private async Task RunAgentStageAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, SourceTicket? source)
    {
        await Task.Yield();
        ExecutionResult? result = null;
        try
        {
            result = await executor.ExecuteAsync(p, io, run, source, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            log.LogInformation("Agent run for {Project}/{Key} cancelled by shutdown", p.Id, run.Key);
        }
        catch (Exception e)
        {
            result = new ExecutionResult(ExecutionStatus.Failed, e.Message);
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (result is not null)
            {
                await NoteProviderAsync(UsageLimits.KeyFor(p.AgentFor(run.StageId)), result.Limit);
                await CompleteAgentStageAsync(p, io, run, result, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Applying agent run result for {Project}/{Key} failed", p.Id, run.Key);
            await TryAddEventOnceAsync(run, $"{run.StageId} run result could not be applied: {e.Message}");
        }
        finally
        {
            inFlight.TryRemove(run.Id, out _);
            gate.Release();
        }
    }

    private async Task CompleteAgentStageAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, ExecutionResult result,
        CancellationToken ct)
    {
        var t = await store.FindAsync(p.Id, run.Key, ct);
        if (t is null || t.Id != run.Id)
        {
            log.LogWarning("Ticket {Project}/{Key} disappeared during its agent run", p.Id, run.Key);
            return;
        }

        // Work produced by the run is kept even when the ticket has moved on.
        t.SpecPath = run.SpecPath ?? t.SpecPath;
        t.PlanPath = run.PlanPath ?? t.PlanPath;
        t.PullRequestId = run.PullRequestId ?? t.PullRequestId;

        if (t.Status != StageStatus.Running || t.StageId != run.StageId)
        {
            await store.SaveAsync(t, ct);
            await AddEventAsync(t, $"{run.StageId} run finished while ticket was {t.Status} at {t.StageId}; result not applied", ct);
            return;
        }

        var now = time.GetUtcNow();
        var fromStageId = t.StageId;
        // The newest query result, not the run's start snapshot: a deferred send-back's comment came during the run.
        var source = latestSources.GetValueOrDefault(p.Id)?.GetValueOrDefault(t.Key);
        var outcome = result.Status switch
        {
            ExecutionStatus.Completed => PipelineEngine.StageSucceeded(p, t, now, source is null ? null : LatestFeedback(source, t)),
            ExecutionStatus.UsageLimited =>
                PipelineEngine.StageLimited(t, $"{t.StageId} paused: {Describe(result.LimitKey!, result.Limit!)}", now),
            _ => PipelineEngine.StageFailed(p, t, result.Error ?? "agent stage did not complete", now),
        };
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, fromStageId, outcome, ct);
    }

    /// <summary>Reviews the gate round on a private copy, then applies the verdict to the ticket re-loaded under the lock.</summary>
    private async Task RunReviewAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, SourceTicket? source)
    {
        await Task.Yield();
        GateReviewOutcome? result = null;
        try
        {
            result = await reviewer.ReviewAsync(p, run, source, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Not a verdict: ReviewDue stays set, so the next start reviews the round again.
            log.LogInformation("Review of {Project}/{Key} cancelled by shutdown", p.Id, run.Key);
        }
        catch (Exception e)
        {
            result = new GateReviewOutcome(GateVerdict.Failed(e.Message), null);
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var key = UsageLimits.KeyFor(p.Review.Agent);
            if (result?.Limit is { } limit)
            {
                // Not a verdict: the review stays owed and runs again once the provider lets it.
                if (await NoteProviderAsync(key, limit))
                    await AddEventAsync(run, $"review of {run.StageId} paused: {Describe(key, limit)}", CancellationToken.None);
            }
            else if (result is not null)
            {
                await NoteProviderAsync(key, null);
                await CompleteReviewAsync(p, io, run, result, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Applying review result for {Project}/{Key} failed", p.Id, run.Key);
            await TryAddEventOnceAsync(run, $"{run.StageId} review result could not be applied: {e.Message}");
        }
        finally
        {
            inFlight.TryRemove(run.Id, out _);
            gate.Release();
        }
    }

    /// <summary>
    /// Records the verdict for its round (always, even when the gate was decided meanwhile) and applies it. A low verdict
    /// at an active auto gate approves only when the reviewed commits are still the heads (else it counts as high) and
    /// the auto-approval comment was posted; otherwise the gate waits on the developer.
    /// </summary>
    private async Task CompleteReviewAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, GateReviewOutcome result,
        CancellationToken ct)
    {
        var t = await store.FindAsync(p.Id, run.Key, ct);
        if (t is null || t.Id != run.Id)
        {
            log.LogWarning("Ticket {Project}/{Key} disappeared during its review", p.Id, run.Key);
            return;
        }

        var verdict = result.Verdict;
        var waitingSince = run.WaitingSince!.Value; // the round key as loaded from the store, never a fresh clock read
        var stage = p.Stage(run.StageId);
        if (PipelineEngine.WouldAutoApprove(p, t, verdict, waitingSince)
            && await UnconfirmedHeadAsync(p, io, t, stage, result, ct) is { } unconfirmed)
        {
            // A low verdict on a commit that is no longer the head. First, so the "needs you" event names it.
            verdict = verdict.WithForcedHighFirst(unconfirmed);
        }
        await store.AddGateReviewAsync(new GateReview(t.Id, t.ProjectId, run.StageId, waitingSince, time.GetUtcNow(),
            p.Review.Mode, stage.Gate, verdict), ct);

        var allowAutoApprove = true;
        if (PipelineEngine.WouldAutoApprove(p, t, verdict, waitingSince)
            && await AutoApprovalCommentFailedAsync(io, t, stage, verdict, ct) is { } held)
        {
            allowAutoApprove = false;
            await AddEventAsync(t, held, ct);
        }

        var fromStageId = t.StageId;
        var outcome = PipelineEngine.ReviewCompleted(p, t, verdict, waitingSince, allowAutoApprove, time.GetUtcNow());
        if (!outcome.Changed)
        {
            // ApplyAsync skips unchanged outcomes. Named after the reviewed stage: the ticket may have moved on since.
            await AddEventAsync(t, $"review of {run.StageId} finished after the gate was decided; not applied", ct);
            return;
        }
        if (outcome.Decision is { Auto: true } && stage.Kind is StageKind.Implement or StageKind.Release)
        {
            // The merge, on a later tick, must merge exactly what was reviewed.
            t.ApprovedBranch = result.ReviewedBranch;
            t.ApprovedHead = result.ReviewedHead;
            t.ApprovedBaseHead = stage.Kind == StageKind.Release ? result.ReviewedBase : null;
        }
        // One event for a failed review: the failure, not the high verdict it was turned into.
        if (verdict.Error is { } error) outcome = outcome with { Message = $"reviewer failed: {error}; waiting on you" };
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, fromStageId, outcome, ct);
    }

    /// <summary>
    /// Gathers the gate round's evidence on a private copy, then records and posts it for the ticket re-loaded under the
    /// lock. <paramref name="cts"/> (linked to shutdown) is cancelled when the round is decided meanwhile.
    /// </summary>
    private async Task RunEvidenceAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, SourceTicket? source,
        CancellationTokenSource cts)
    {
        await Task.Yield();
        EvidenceOutcome? outcome = null;
        var decided = false;
        try
        {
            outcome = await evidence.GatherAsync(p, run, source, cts.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Not gathered: EvidenceDue stays set, so the next start gathers the round's evidence again.
            log.LogInformation("Evidence for {Project}/{Key} cancelled by shutdown", p.Id, run.Key);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            decided = true;
        }
        catch (Exception e)
        {
            outcome = new EvidenceOutcome(EvidenceStatus.Failed, null, null, [], e.Message, $"evidence failed: {e.Message}");
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var key = UsageLimits.KeyFor(p.Evidence.Agent);
            if (decided)
                await AddEventAsync(run, $"evidence for {run.StageId} cancelled: the gate was decided", CancellationToken.None);
            else if (outcome is { Status: EvidenceStatus.UsageLimited, Limit: { } limit })
            {
                // Nothing recorded or posted: the evidence stays owed and is gathered once the provider lets it.
                if (await NoteProviderAsync(key, limit))
                    await AddEventAsync(run, $"evidence for {run.StageId} paused: {Describe(key, limit)}", CancellationToken.None);
            }
            else if (outcome is not null)
            {
                await NoteProviderAsync(key, null);
                await CompleteEvidenceAsync(p, io, run, outcome, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Applying evidence for {Project}/{Key} failed", p.Id, run.Key);
            await TryAddEventOnceAsync(run, $"{run.StageId} evidence could not be applied: {e.Message}");
        }
        finally
        {
            // Removed under the lock before it is disposed: ApplyAsync (also under the lock) cancels only entries present.
            evidenceRuns.Remove(run.Id);
            inFlight.TryRemove(run.Id, out _);
            gate.Release();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Records gathered (or failed) evidence and, while its round is still waiting, posts it to the pull request and a
    /// line to the tracker; posting is never retried. Evidence never decides the gate: it only stops being owed.
    /// </summary>
    private async Task CompleteEvidenceAsync(ProjectConfig p, ProjectIntegrations io, Ticket run, EvidenceOutcome outcome,
        CancellationToken ct)
    {
        var t = await store.FindAsync(p.Id, run.Key, ct);
        if (t is null || t.Id != run.Id)
        {
            log.LogWarning("Ticket {Project}/{Key} disappeared while its evidence was gathered", p.Id, run.Key);
            return;
        }

        var waitingSince = run.WaitingSince!.Value; // the round key as loaded from the store
        var current = t.Status == StageStatus.AwaitingApproval && t.WaitingSince == waitingSince;
        if (outcome.Status is EvidenceStatus.Gathered or EvidenceStatus.Failed)
        {
            var posted = false;
            if (!current)
                await AddEventAsync(t, $"evidence for {run.StageId} finished after the gate was decided; not posted", ct);
            else if (t.PullRequestId is { } prId)
                posted = await PostEvidenceAsync(p, io, t, prId, outcome, ct);
            await store.AddEvidenceAsync(new EvidenceRecord(t.Id, t.ProjectId, run.StageId, waitingSince, time.GetUtcNow(),
                outcome.Head, outcome.Report?.ToJson(), outcome.Report?.HasIssues ?? false, outcome.Error,
                outcome.Screenshots, posted), ct);
        }

        var fromStageId = t.StageId;
        var completed = PipelineEngine.EvidenceCompleted(p, t, waitingSince, outcome.Message, time.GetUtcNow());
        if (!completed.Changed) return; // another round: its own evidence is owed (or it was decided)
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, fromStageId, completed, ct);
    }

    /// <summary>
    /// Posts the signed report (or failure) to the pull request, then a signed line to the tracker. Returns whether the
    /// pull request comment was posted; a failure is recorded as an event.
    /// </summary>
    private async Task<bool> PostEvidenceAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, long prId,
        EvidenceOutcome outcome, CancellationToken ct)
    {
        var report = outcome.Report;
        var failed = EvidenceComment.Failed(outcome.Error ?? "no evidence report", p.CommentMarker);
        try
        {
            var body = report is null ? failed : EvidenceComment.Format(report, outcome.Screenshots.Count > 0, p.CommentMarker);
            await io.SourceControl.CommentOnPullRequestAsync(prId, TrackerComments.Sign(body), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Evidence comment on pull request {Id} for {Key} failed", prId, t.Key);
            await AddEventAsync(t, $"evidence comment failed: {e.Message}", ct);
            return false;
        }
        try
        {
            var line = report is null ? failed : EvidenceComment.TrackerLine(report, p.CommentMarker);
            await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign(line), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Evidence comment on {Key} failed", t.Key);
            await AddEventAsync(t, $"evidence comment failed: {e.Message}", ct);
        }
        return true;
    }

    /// <summary>
    /// Why the reviewed commits cannot be confirmed as the current heads, or null when they are (or the gate reviews no
    /// commit). An implement or release verdict covers only the reviewed commit of the branch that would be merged (the
    /// ticket branch, or main), and a release also its production base; the stage's pull request, when there is one,
    /// must merge exactly that commit from this repository. Any doubt makes the verdict high.
    /// </summary>
    private async Task<string?> UnconfirmedHeadAsync(ProjectConfig p, ProjectIntegrations io, Ticket t,
        StageDefinition stage, GateReviewOutcome result, CancellationToken ct)
    {
        if (stage.Kind is not (StageKind.Implement or StageKind.Release)) return null; // the ticket and its documents
        if (result is not { ReviewedBranch: { } branch, ReviewedHead: { } head })
            return "could not confirm the reviewed commit";
        if (await MovedAsync(io, t, branch, head, ct) is { } moved) return moved;
        if ((stage.Kind == StageKind.Release ? t.ReleasePullRequestId : t.PullRequestId) is { } prId)
        {
            PullRequestInfo pr;
            try
            {
                pr = await io.SourceControl.GetPullRequestAsync(prId, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Pull request {Id} for {Key} could not be checked", prId, t.Key);
                return $"could not confirm what pull request {prId} would merge: {e.Message}";
            }
            if (PullRequestDoubt(p, pr, head) is { } doubt) return doubt;
        }
        if (stage.Kind != StageKind.Release) return null;
        if (result.ReviewedBase is not { } baseHead)
            return $"could not confirm the reviewed commit of {p.Repo.ProductionBranch}";
        return await MovedAsync(io, t, p.Repo.ProductionBranch, baseHead, ct);
    }

    /// <summary>Null when <paramref name="branch"/> is still at <paramref name="expected"/>; otherwise why not.</summary>
    private async Task<string?> MovedAsync(ProjectIntegrations io, Ticket t, string branch, string expected,
        CancellationToken ct)
    {
        string current;
        try
        {
            current = await io.SourceControl.GetBranchHeadAsync(branch, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Head of {Branch} for {Key} could not be checked", branch, t.Key);
            return $"could not confirm the reviewed commit of {branch}: {e.Message}";
        }
        return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"reviewed {expected} but {branch} is now {current}";
    }

    /// <summary>Posts the signed auto-approval comment; returns the event holding the approval when it cannot be posted.</summary>
    private async Task<string?> AutoApprovalCommentFailedAsync(ProjectIntegrations io, Ticket t, StageDefinition stage,
        GateVerdict verdict, CancellationToken ct)
    {
        try
        {
            await io.Tracker.CommentAsync(t.Key, TrackerComments.Sign(AutoApprovalComment.Format(stage.Id, verdict)), ct);
            return null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Auto-approval comment on {Key} failed", t.Key);
            return $"auto-approval held: could not post comment: {e.Message}; waiting on you";
        }
    }

    /// <summary>
    /// For an auto-approved round, why the merge must not go ahead: the pull request (as just read) would not merge the
    /// reviewed commit from this repository, or the reviewed branch (or a release's production base) is no longer at the
    /// reviewed commit, or cannot be checked. Null for human approvals, which pin no commit.
    /// </summary>
    private async Task<string?> MergeHeldAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, PullRequestInfo pr,
        CancellationToken ct)
    {
        if (t.ApprovedHead is null && t.ApprovedBaseHead is null) return null;
        if (t is not { ApprovedBranch: { } branch, ApprovedHead: { } head }) return "the auto-approved commit is not recorded";
        var checks = new List<(string Branch, string Head)> { (branch, head) };
        if (p.Stage(t.StageId).Kind == StageKind.Release)
        {
            if (t.ApprovedBaseHead is not { } baseHead) return "the auto-approved production commit is not recorded";
            checks.Add((p.Repo.ProductionBranch, baseHead));
        }
        foreach (var (name, expected) in checks)
        {
            string current;
            try
            {
                current = await io.SourceControl.GetBranchHeadAsync(name, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Head of {Branch} for {Key} could not be checked before merging", name, t.Key);
                return $"could not check {name}: {e.Message}";
            }
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                return $"{name} moved since auto-approval ({expected} → {current})";
        }
        // The host merges the pull request's source, not our branch: e.g. a fork's PR with the same branch names.
        return PullRequestDoubt(p, pr, head);
    }

    /// <summary>Null when <paramref name="pr"/> would merge <paramref name="head"/> from this repository; otherwise why not.</summary>
    private static string? PullRequestDoubt(ProjectConfig p, PullRequestInfo pr, string head)
    {
        if (pr.WouldMerge(p.Repo.FullName, head)) return null;
        if (!string.Equals(pr.SourceRepository, p.Repo.FullName, StringComparison.OrdinalIgnoreCase))
            return $"pull request {pr.Id} is not from {p.Repo.FullName} (source: {pr.SourceRepository ?? "unknown"})";
        return $"pull request {pr.Id} would merge {pr.SourceCommit ?? "an unknown commit"}, not the reviewed {head}";
    }

    private Task ApplyAsync(ProjectIntegrations io, Ticket t, string fromStageId, TransitionOutcome outcome,
        CancellationToken ct) =>
        ApplyAsync(() => io, t, fromStageId, outcome, ct);

    /// <summary>
    /// Records the outcome, and the gate decision it made (whoever made it: CLI, API, dashboard, tracker move, @claude or
    /// the reviewer), then mirrors its status to the tracker. Integrations that cannot be created (missing secret) and a
    /// missing or failing transition never throw; they are recorded as events, and a skipped or failed move is kept in
    /// <see cref="Ticket.TrackerRetryStatus"/> for the next ticks to retry. A new move, or leaving
    /// <paramref name="fromStageId"/> (the stage before the outcome), cancels the move still waiting to be retried.
    /// </summary>
    private async Task ApplyAsync(Func<ProjectIntegrations> io, Ticket t, string fromStageId, TransitionOutcome outcome,
        CancellationToken ct)
    {
        if (!outcome.Changed) return;
        CancelDecidedEvidence(t);
        await AddEventAsync(t, outcome.Message, ct);
        Notify(t);
        if (outcome.Touch is { } touch)
            await store.AddTouchAsync(new TouchRecord(t.Id, t.ProjectId, fromStageId, touch, time.GetUtcNow()), ct);
        if (outcome.Decision is { } d)
            await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, t.ProjectId, d.StageId, d.WaitingSince, d.Decision,
                d.Auto, time.GetUtcNow()), ct);

        var target = outcome.SourceStatusToSet;
        if (t.TrackerRetryStatus is not null && (target is not null || t.StageId != fromStageId))
        {
            t.TrackerRetryStatus = null; // the old target belongs to a stage the ticket has left, or is replaced
            await store.SaveAsync(t, ct);
        }
        if (target is null) return;
        retryFailureRecorded.Remove(t.Id); // a new move: its own failure is recorded
        // Skip when the card is already there (e.g. the developer's move is what approved the stage).
        if (Same(target, t.LastSourceStatus)) return;
        ITracker tracker;
        try
        {
            tracker = io().Tracker;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Tracker sync of {Key} skipped: {Error}", t.Key, e.Message);
            await AddEventAsync(t, $"tracker sync skipped: {e.Message}", ct);
            await SaveRetryAsync(t, target, ct);
            return;
        }
        try
        {
            if (await tracker.TransitionAsync(t.Key, target, ct))
            {
                t.LastSourceStatus = target; // our own move, not a developer action to react to next tick
                await store.SaveAsync(t, ct);
            }
            else
            {
                await AddEventAsync(t, $"no transition to '{target}' in tracker", ct);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Transition of {Key} to {Status} failed", t.Key, target);
            await AddEventAsync(t, $"tracker transition to '{target}' failed: {e.Message}", ct);
            retryFailureRecorded.Add(t.Id);
            await SaveRetryAsync(t, target, ct);
        }
    }

    /// <summary>
    /// Tells the developer a ticket now waits on them (once per round) or is blocked (once per block); under gate only.
    /// Auto-approved rounds, and gates still waiting on the reviewer, say nothing.
    /// </summary>
    private void Notify(Ticket t)
    {
        if (notifications is null) return;
        if (t.Status != StageStatus.Blocked) notifiedBlocked.Remove(t.Id);
        if (t.Status != StageStatus.AwaitingApproval) notifiedRounds.Remove(t.Id);
        var p = projects.Projects.FirstOrDefault(x => x.Id == t.ProjectId);
        if (p is null) return;
        if (t.Status == StageStatus.AwaitingApproval && t.WaitingSince is { } since && !PipelineEngine.WaitsOnReviewer(p, t))
        {
            if (notifiedRounds.TryGetValue(t.Id, out var last) && last == since) return;
            notifiedRounds[t.Id] = since;
            var line = t.Risk is { } risk ? $"{t.StageId} · reviewer says {risk.ToString().ToLowerInvariant()} risk" : t.StageId;
            notifications.Post(new NotificationEvent(NotificationKind.WaitingOnYou, t.ProjectId, t.Key, line));
        }
        else if (t.Status == StageStatus.Blocked && notifiedBlocked.Add(t.Id))
        {
            notifications.Post(new NotificationEvent(NotificationKind.Blocked, t.ProjectId, t.Key, t.BlockedReason ?? "blocked"));
        }
    }

    /// <summary>
    /// Cancels the ticket's evidence run once its round is no longer the one waiting (approved, sent back, moved, rerun
    /// by @claude, auto-approved or blocked); under gate only, like every outcome applied.
    /// </summary>
    private void CancelDecidedEvidence(Ticket t)
    {
        if (!evidenceRuns.TryGetValue(t.Id, out var run)) return;
        if (t.Status == StageStatus.AwaitingApproval && t.WaitingSince == run.WaitingSince) return;
        try
        {
            run.Cts.Cancel();
        }
        catch (AggregateException e)
        {
            // A token callback (e.g. the runner's cleanup) threw; the decision itself must still be applied.
            log.LogWarning(e, "Cancelling the evidence run of {Project}/{Key} raised", t.ProjectId, t.Key);
        }
    }

    /// <summary>
    /// What a finished job says about its provider: a usage limit pauses <paramref name="key"/>, any other result lifts
    /// its pause. Returns whether this started a pause (so its event is posted once per pause, not per probe). Best
    /// effort: a failure is logged, and the job's own result is still applied.
    /// </summary>
    private async Task<bool> NoteProviderAsync(string key, UsageLimit? limit)
    {
        try
        {
            if (limit is null)
            {
                await limits.SucceededAsync(key, CancellationToken.None);
                return false;
            }
            var started = limits.Pauses.All(p => p.Key != key);
            await limits.PauseAsync(key, limit, CancellationToken.None);
            return started;
        }
        catch (Exception e)
        {
            log.LogError(e, "Recording the usage limit state of {Key} failed", key);
            return false;
        }
    }

    private string Describe(string key, UsageLimit limit) =>
        UsageLimitText.Describe(key, limit.ResetsAt, time.LocalTimeZone, time.GetUtcNow());

    private Task SaveRetryAsync(Ticket t, string target, CancellationToken ct)
    {
        t.TrackerRetryStatus = target;
        return store.SaveAsync(t, ct);
    }

    private Task AddEventAsync(Ticket t, string message, CancellationToken ct) =>
        store.AddEventAsync(t.Id, t.StageId, message, time.GetUtcNow(), ct);

    private async Task AddEventOnceAsync(Ticket t, string message, CancellationToken ct)
    {
        var events = await store.EventsAsync(t.Id, ct);
        if (events.Count > 0 && events[^1].Message == message) return;
        await AddEventAsync(t, message, ct);
    }

    /// <summary>Best effort: records an error event, never throws (the store itself may be what failed).</summary>
    private async Task TryAddEventOnceAsync(Ticket t, string message)
    {
        try
        {
            await AddEventOnceAsync(t, message, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError(e, "Recording event for {Project}/{Key} failed", t.ProjectId, t.Key);
        }
    }

    /// <summary>
    /// Not cached here: the factory re-reads the secrets on every call, so a corrected or removed secret takes effect at
    /// the next tick or command. A tick resolves them once and uses them throughout.
    /// </summary>
    private ProjectIntegrations IntegrationsFor(ProjectConfig p) => integrations.Create(p);

    private bool IsAgentStage(string projectId, string stageId)
    {
        var stage = projects.Projects.FirstOrDefault(p => p.Id == projectId)?.Pipeline.FirstOrDefault(s => s.Id == stageId);
        return stage is not null && StageExecutor.IsAgentStage(stage.Kind);
    }

    /// <summary>
    /// The developer's newest comment since the ticket started waiting (or entered its stage), excluding the comments
    /// Symphonist posted under the developer's account.
    /// </summary>
    private static string? LatestFeedback(SourceTicket s, Ticket t)
    {
        var since = t.WaitingSince ?? t.StageStartedAt;
        return s.Comments
            .Where(c => c.IsMine && !TrackerComments.IsSymphonist(c.Body) && (since is null || c.Created > since))
            .MaxBy(c => c.Created)?.Body;
    }

    private static bool HasStage(ProjectConfig p, Ticket t) => p.Pipeline.Any(s => s.Id == t.StageId);

    private static bool IsActive(Ticket t) => t.Status is not (StageStatus.Done or StageStatus.Blocked);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
