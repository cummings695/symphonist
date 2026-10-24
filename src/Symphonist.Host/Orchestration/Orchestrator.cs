using System.Collections.Concurrent;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

/// <param name="TickAfterCommands">An approve / send-back / unblock that changed a ticket starts a tick right away
/// (coalesced) instead of waiting for the next poll.</param>
public sealed record OrchestratorOptions(TimeSpan PollInterval, int MaxConcurrentAgentRuns, bool TickAfterCommands = true)
{
    public OrchestratorOptions() : this(TimeSpan.FromMinutes(2), 1) { }
}

/// <summary>
/// Syncs tracker tickets into the store, drives each ticket through the pipeline, and mirrors state back to the tracker.
/// Ticks, commands (approve / send back / unblock) and agent-run completions all run under one lock, and every one of
/// them loads the ticket fresh from the store, so no two writers ever save a ticket from stale copies.
/// </summary>
public sealed class Orchestrator(ProjectRegistry projects, ITicketStore store, IIntegrationFactory integrations,
    StageExecutor executor, TimeProvider time, OrchestratorOptions options, ILogger<Orchestrator> log) : IDisposable
{
    private const string LeftQuery = "no longer matches tracker query";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Task> inFlight = new();
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

    private readonly Dictionary<string, (ProjectConfig Config, ProjectIntegrations Io)> io = []; // used under gate only
    private readonly Dictionary<string, Dictionary<string, SourceTicket>> latestSources = []; // used under gate only

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
    public Task<TransitionOutcome> ApproveAsync(string projectId, string key, CancellationToken ct,
        string? expectedStageId = null) =>
        CommandAsync(projectId, key, (p, t, now) => expectedStageId is not null && t.StageId != expectedStageId
            ? new TransitionOutcome(false, null, $"{expectedStageId} is not awaiting approval")
            : PipelineEngine.Approve(p, t, now), ct);

    public Task<TransitionOutcome> SendBackAsync(string projectId, string key, string feedback, string? toStageId,
        CancellationToken ct) =>
        CommandAsync(projectId, key, (p, t, now) => PipelineEngine.SendBack(p, t, feedback, toStageId, now), ct);

    public Task<TransitionOutcome> UnblockAsync(string projectId, string key, CancellationToken ct) =>
        CommandAsync(projectId, key, (_, t, now) => PipelineEngine.Unblock(t, now), ct);

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
        var p = projects.Get(projectId);
        TransitionOutcome outcome;
        await gate.WaitAsync(ct);
        try
        {
            var t = await store.FindAsync(projectId, key, ct)
                    ?? throw new KeyNotFoundException($"Ticket '{key}' not found in project '{projectId}'");
            outcome = apply(p, t, time.GetUtcNow());
            if (outcome.Changed)
            {
                await store.SaveAsync(t, ct);
                // Resolved inside ApplyAsync: a missing secret must not undo (or hide) a command that took effect.
                await ApplyAsync(() => IntegrationsFor(p), t, outcome, ct);
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
            if (Same(t.LastSourceStatus, s.Status))
            {
                await RetryTrackerMoveAsync(io, t, ct); // first: a @claude send-back's own move replaces the retry
                await HandleClaudeCommentsAsync(p, io, t, s, now, ct); // only acts at a gate or block, never on Done
                continue;
            }

            if (t.Status == StageStatus.Done)
            {
                t.LastSourceStatus = s.Status;
                await store.SaveAsync(t, ct);
                await AddEventAsync(t,
                    $"card moved to '{s.Status}' after the ticket was done; Symphonist does not reopen done tickets", ct);
                continue;
            }

            t.TrackerRetryStatus = null; // the developer moved the card; never move it back to the old target
            var outcome = PipelineEngine.SourceStatusChanged(p, t, s.Status, LatestFeedback(s, t), now);
            await store.SaveAsync(t, ct); // LastSourceStatus changes even when the outcome doesn't
            await ApplyAsync(io, t, outcome, ct);
        }

        foreach (var t in known.Values.Where(t => IsActive(t) && !sources.ContainsKey(t.Key)))
        {
            var outcome = PipelineEngine.Block(t, LeftQuery, now);
            await store.SaveAsync(t, ct);
            await ApplyAsync(io, t, outcome, ct);
        }
    }

    /// <summary>
    /// Retries a tracker move that was skipped or threw, while the card is still where Symphonist last saw it. A failure
    /// is recorded once, not on every tick of an outage.
    /// </summary>
    private async Task RetryTrackerMoveAsync(ProjectIntegrations io, Ticket t, CancellationToken ct)
    {
        if (t.TrackerRetryStatus is not { } target || t.Status == StageStatus.Done) return;
        bool moved;
        try
        {
            moved = await io.Tracker.TransitionAsync(t.Key, target, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Retried transition of {Key} to {Status} failed", t.Key, target);
            await AddEventOnceAsync(t, $"tracker transition to '{target}' failed: {e.Message}", ct);
            return;
        }

        if (moved) t.LastSourceStatus = target;
        t.TrackerRetryStatus = null;
        await store.SaveAsync(t, ct);
        await AddEventAsync(t, moved ? $"tracker moved to '{target}' on retry" : $"no transition to '{target}' in tracker", ct);
    }

    /// <summary>
    /// Developer "@claude" comments made since the stage started rerun it, with the comments as feedback, once it waits
    /// at its gate or is blocked; comments made while it is pending or running wait for that.
    /// </summary>
    private async Task HandleClaudeCommentsAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket s,
        DateTimeOffset now, CancellationToken ct)
    {
        if (t.Status is not (StageStatus.AwaitingApproval or StageStatus.Blocked)) return;
        var since = t.ClaudeCommentsHandledAt is { } handled && (t.StageStartedAt is not { } started || handled > started)
            ? handled
            : t.StageStartedAt; // the later of the two: entering a stage leaves earlier comments behind
        var comments = s.Comments
            .Where(c => c.IsMine && !TrackerComments.IsSymphonist(c.Body) && (since is null || c.Created > since))
            .Select(c => (c.Created, Request: TrackerComments.ClaudeRequest(c.Body)))
            .Where(c => c.Request is not null)
            .OrderBy(c => c.Created)
            .ToList();
        if (comments.Count == 0) return;

        var requests = comments.Select(c => c.Request!).Where(r => r.Length > 0).ToList();
        var feedback = requests.Count > 0 ? string.Join("\n\n", requests) : "(no details given)";
        var outcome = PipelineEngine.SendBack(p, t, feedback, t.StageId, now);
        t.ClaudeCommentsHandledAt = comments[^1].Created;
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, outcome, ct);
        await AddEventAsync(t, $"@claude comment: rerunning {t.StageId}", ct);
    }

    private async Task AdvanceAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        var agentStage = StageExecutor.IsAgentStage(p.Stage(t.StageId).Kind);
        switch (t.Status)
        {
            case StageStatus.Pending when agentStage:
                if (inFlight.Count >= options.MaxConcurrentAgentRuns) return;
                t.Status = StageStatus.Running;
                t.UpdatedAt = time.GetUtcNow();
                await store.SaveAsync(t, ct);
                inFlight[t.Id] = RunAgentStageAsync(p, io, t.Clone(), source);
                return;

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
                if (outcome is not null) await ApplyAsync(io, t, outcome, ct);
                return;
            }

            case StageStatus.Approved:
            {
                var result = await executor.PostApprovalAsync(p, io, t, ct);
                var now = time.GetUtcNow();
                TransitionOutcome? outcome = null;
                switch (result.Status)
                {
                    case ExecutionStatus.Completed:
                        outcome = PipelineEngine.PostApprovalCompleted(p, t, now);
                        break;
                    case ExecutionStatus.Failed:
                        // Count the attempt, but retry the post-approval action (merge) rather than rerunning the stage.
                        outcome = PipelineEngine.StageFailed(p, t, result.Error ?? "post-approval action failed", now);
                        if (t.Status == StageStatus.Pending) t.Status = StageStatus.Approved;
                        break;
                }
                await store.SaveAsync(t, ct); // persists the merge commit
                if (outcome is not null) await ApplyAsync(io, t, outcome, ct);
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
            if (result is not null) await CompleteAgentStageAsync(p, io, run, result, CancellationToken.None);
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
        // The newest query result, not the run's start snapshot: a deferred send-back's comment came during the run.
        var source = latestSources.GetValueOrDefault(p.Id)?.GetValueOrDefault(t.Key);
        var outcome = result.Status == ExecutionStatus.Completed
            ? PipelineEngine.StageSucceeded(p, t, now, source is null ? null : LatestFeedback(source, t))
            : PipelineEngine.StageFailed(p, t, result.Error ?? "agent stage did not complete", now);
        await store.SaveAsync(t, ct);
        await ApplyAsync(io, t, outcome, ct);
    }

    private Task ApplyAsync(ProjectIntegrations io, Ticket t, TransitionOutcome outcome, CancellationToken ct) =>
        ApplyAsync(() => io, t, outcome, ct);

    /// <summary>
    /// Records the outcome and mirrors its status to the tracker. Integrations that cannot be created (missing secret)
    /// and a missing or failing transition never throw; they are recorded as events, and a skipped or failed move is
    /// kept in <see cref="Ticket.TrackerRetryStatus"/> for the next ticks to retry.
    /// </summary>
    private async Task ApplyAsync(Func<ProjectIntegrations> io, Ticket t, TransitionOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Changed) return;
        await AddEventAsync(t, outcome.Message, ct);

        if (outcome.SourceStatusToSet is not { } target) return;
        if (t.TrackerRetryStatus is not null)
        {
            t.TrackerRetryStatus = null; // this move replaces the one still waiting to be retried
            await store.SaveAsync(t, ct);
        }
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
            await SaveRetryAsync(t, target, ct);
        }
    }

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

    private ProjectIntegrations IntegrationsFor(ProjectConfig p)
    {
        if (io.TryGetValue(p.Id, out var cached) && ReferenceEquals(cached.Config, p)) return cached.Io;
        var created = integrations.Create(p);
        io[p.Id] = (p, created);
        return created;
    }

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

    private static bool IsActive(Ticket t) => t.Status is not (StageStatus.Done or StageStatus.Blocked);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
