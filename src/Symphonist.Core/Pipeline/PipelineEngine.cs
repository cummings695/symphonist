using Symphonist.Core.Config;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Pipeline;

public static class PipelineEngine
{
    public static Ticket Create(ProjectConfig p, string key, string title, string sourceStatus, DateTimeOffset now)
    {
        var stage = p.Pipeline.FirstOrDefault(s => s.EntryStatuses.Any(e => Same(e, sourceStatus))) ?? p.Pipeline[0];
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ProjectId = p.Id,
            Key = key,
            Title = title,
            StageId = stage.Id,
            Status = StageStatus.Pending,
            LastSourceStatus = sourceStatus,
            Branch = $"symphonist/{key}",
            CreatedAt = now,
            UpdatedAt = now,
            StageStartedAt = now,
        };
    }

    /// <summary>
    /// The stage's work is done: wait at its gate (human or auto; a review may be owed) or advance. A source move
    /// deferred while the stage ran (<see cref="Ticket.PendingSourceStatus"/>) is applied now: its gate's approval
    /// status approves, an earlier stage's status sends back with <paramref name="latestFeedback"/>.
    /// </summary>
    public static TransitionOutcome StageSucceeded(ProjectConfig p, Ticket t, DateTimeOffset now, string? latestFeedback = null)
    {
        var stage = p.Stage(t.StageId);
        var pending = t.PendingSourceStatus;
        t.PendingSourceStatus = null;
        t.Feedback = null;

        // Apply the decision only while the card still says so; a later move (e.g. back again) overrides it.
        string? discarded = null;
        if (pending is not null && !Same(t.LastSourceStatus, pending))
        {
            discarded = $"discarded source move to '{pending}' (card is now '{t.LastSourceStatus}'); ";
            pending = null;
        }

        if (pending is not null && EarlierStageFor(p, t.StageId, pending) is { } target)
            return Deferred(pending, SendBackTo(t, target, latestFeedback, now));

        TransitionOutcome outcome;
        if (stage.Gate != GatePolicy.None)
        {
            t.Status = StageStatus.AwaitingApproval;
            t.WaitingSince = ToStoredPrecision(now);
            t.UpdatedAt = now;
            ClearApprovedHeads(t); // a new round
            if (pending is not null && Same(stage.ApprovedWhenStatus, pending)) return Deferred(pending, Approve(p, t, now));
            t.ReviewDue = p.Reviews(stage);
            outcome = new(true, stage.OnGateStatus, $"{stage.Id} awaiting approval");
        }
        else
        {
            outcome = Advance(p, t, stage, now);
        }
        return discarded is null ? outcome : outcome with { Message = discarded + outcome.Message };
    }

    public static TransitionOutcome StageFailed(ProjectConfig p, Ticket t, string error, DateTimeOffset now)
    {
        t.Attempts++;
        t.UpdatedAt = now;
        if (t.Attempts >= p.MaxAttemptsPerStage)
        {
            t.Status = StageStatus.Blocked;
            t.PendingSourceStatus = null; // a decision about this run must not outlive the block
            t.BlockedReason = $"{t.StageId} failed {t.Attempts} times: {error}";
            return new(true, null, $"blocked: {t.BlockedReason}");
        }
        t.Status = StageStatus.Pending;
        return new(true, null, $"{t.StageId} failed (attempt {t.Attempts}/{p.MaxAttemptsPerStage}): {error}");
    }

    public static TransitionOutcome Approve(ProjectConfig p, Ticket t, DateTimeOffset now)
    {
        if (t.Status != StageStatus.AwaitingApproval) return NoChange($"{t.StageId} is not awaiting approval");
        var stage = p.Stage(t.StageId);
        var decision = DecisionOn(t, GateDecision.Approved);
        t.ReviewDue = false;
        ClearApprovedHeads(t); // a human approval pins no commit; an auto-approval sets them afterwards
        if (HasPostApprovalAction(stage.Kind))
        {
            t.Status = StageStatus.Approved;
            t.Attempts = 0; // the post-approval action (merge) gets its own retry budget
            t.UpdatedAt = now;
            return new(true, null, $"{stage.Id} approved", decision);
        }
        return Advance(p, t, stage, now) with { Decision = decision };
    }

    /// <summary>
    /// A low verdict would approve this round on its own: the round (<paramref name="waitingSince"/>) is still waiting,
    /// its review is owed, the gate is auto and review is active.
    /// </summary>
    public static bool WouldAutoApprove(ProjectConfig p, Ticket t, GateVerdict v, DateTimeOffset waitingSince) =>
        t.WaitingSince == waitingSince && WaitsOnReviewer(p, t) && v.Risk == RiskLevel.Low;

    /// <summary>The gate isn't waiting on the developer yet: an active auto gate whose review is still owed.</summary>
    public static bool WaitsOnReviewer(ProjectConfig p, Ticket t) =>
        t.Status == StageStatus.AwaitingApproval && t.ReviewDue
        && p.Stage(t.StageId).Gate == GatePolicy.AutoWhenLowRisk && p.Review.Mode == ReviewMode.Active;

    /// <summary>
    /// The reviewer judged the round (<paramref name="waitingSince"/>): record the verdict and, for a low verdict at an
    /// active auto gate, approve when <paramref name="allowAutoApprove"/>. A verdict on a round already decided is not applied.
    /// </summary>
    public static TransitionOutcome ReviewCompleted(ProjectConfig p, Ticket t, GateVerdict v, DateTimeOffset waitingSince,
        bool allowAutoApprove, DateTimeOffset now)
    {
        if (t.Status != StageStatus.AwaitingApproval || t.WaitingSince != waitingSince)
            return NoChange($"review of {t.StageId} finished after the gate was decided; not applied");

        var stageId = t.StageId;
        var auto = allowAutoApprove && WouldAutoApprove(p, t, v, waitingSince);
        t.Risk = v.Risk;
        t.ReviewDue = false;
        t.UpdatedAt = now;
        if (auto)
        {
            var approved = Approve(p, t, now);
            return approved with { Message = $"auto-approved {stageId}: low risk", Decision = approved.Decision! with { Auto = true } };
        }

        var risk = v.Risk == RiskLevel.Low ? "low" : "high";
        var message =
            p.Review.Mode == ReviewMode.Shadow ? $"reviewer (shadow): {risk} risk — {v.Summary}"
            : v.Risk == RiskLevel.High ? $"{t.StageId} needs you: high risk — {v.Reasons.FirstOrDefault() ?? v.Summary}"
            : $"reviewer: {risk} risk — {v.Summary}";
        return new(true, null, message);
    }

    /// <summary>
    /// The merge of an auto-approved round must not go ahead (the reviewed branch moved, or could not be checked): the
    /// gate waits again as a new round, with a review owed per configuration.
    /// </summary>
    public static TransitionOutcome MergeHeld(ProjectConfig p, Ticket t, string reason, DateTimeOffset now)
    {
        if (t.Status != StageStatus.Approved) return NoChange($"{t.StageId} is not approved");
        var stage = p.Stage(t.StageId);
        var since = ToStoredPrecision(now);
        // A different round key even when the clock has not moved past the approved round.
        if (t.WaitingSince is { } previous && since <= previous) since = previous.AddMilliseconds(1);
        t.Status = StageStatus.AwaitingApproval;
        t.WaitingSince = since;
        t.ReviewDue = p.Reviews(stage);
        t.Attempts = 0;
        t.UpdatedAt = now;
        ClearApprovedHeads(t);
        return new(true, stage.OnGateStatus, $"merge held: {reason}");
    }

    public static TransitionOutcome PostApprovalCompleted(ProjectConfig p, Ticket t, DateTimeOffset now)
    {
        if (t.Status != StageStatus.Approved) return NoChange($"{t.StageId} is not approved");
        return Advance(p, t, p.Stage(t.StageId), now);
    }

    public static TransitionOutcome SendBack(ProjectConfig p, Ticket t, string? feedback, string? toStageId, DateTimeOffset now)
    {
        if (t.Status is not (StageStatus.AwaitingApproval or StageStatus.Approved or StageStatus.Blocked))
            return NoChange($"cannot send back from {t.Status}");
        // Only a send-back from the waiting gate decides a round; one from Approved or Blocked does not.
        var decision = t.Status == StageStatus.AwaitingApproval ? DecisionOn(t, GateDecision.SentBack) : null;
        return SendBackTo(t, p.Stage(toStageId ?? t.StageId), feedback, now) with { Decision = decision };
    }

    public static TransitionOutcome SourceStatusChanged(ProjectConfig p, Ticket t, string newStatus, string? latestFeedback, DateTimeOffset now)
    {
        var unchanged = Same(t.LastSourceStatus, newStatus);
        t.LastSourceStatus = newStatus;
        if (unchanged) return NoChange("source status unchanged");

        var stage = p.Stage(t.StageId);
        if (t.Status is StageStatus.Pending or StageStatus.Running)
        {
            // The stage's result isn't in yet: remember the developer's latest decision and apply it when it is.
            if (Same(stage.ApprovedWhenStatus, newStatus) || EarlierStageFor(p, t.StageId, newStatus) is not null)
            {
                t.PendingSourceStatus = newStatus;
                return new(true, null,
                    $"source moved to '{newStatus}' while {stage.Id} is {t.Status}; will apply when the stage finishes");
            }
            t.PendingSourceStatus = null;
            return NoChange($"source status '{newStatus}' ignored");
        }

        if (t.Status == StageStatus.AwaitingApproval && Same(stage.ApprovedWhenStatus, newStatus))
            return Approve(p, t, now);

        // Card moved back to the stage's own status (e.g. In Review -> In Progress at the implement gate): rerun it.
        if (t.Status == StageStatus.AwaitingApproval && Same(stage.OnEnterStatus, newStatus) && !Same(stage.OnGateStatus, newStatus))
            return SendBack(p, t, latestFeedback, stage.Id, now);

        if (EarlierStageFor(p, t.StageId, newStatus) is { } target)
            return SendBack(p, t, latestFeedback, target.Id, now);
        return NoChange($"source status '{newStatus}' ignored");
    }

    public static TransitionOutcome Block(Ticket t, string reason, DateTimeOffset now)
    {
        t.Status = StageStatus.Blocked;
        t.BlockedReason = reason;
        t.PendingSourceStatus = null;
        t.WaitingSince = null; // the gate round ends: a late review or click of it must not apply
        t.ReviewDue = false;
        t.UpdatedAt = now;
        return new(true, null, $"blocked: {reason}");
    }

    public static TransitionOutcome Unblock(Ticket t, DateTimeOffset now)
    {
        if (t.Status != StageStatus.Blocked) return NoChange("ticket is not blocked");
        t.Status = StageStatus.Pending;
        t.Attempts = 0;
        t.BlockedReason = null;
        t.PendingSourceStatus = null;
        t.StageStartedAt = now; // a fresh start: build/deploy timeouts count from here, not from the original entry
        t.UpdatedAt = now;
        return new(true, null, "unblocked");
    }

    public static bool HasPostApprovalAction(StageKind kind) => kind is StageKind.Implement or StageKind.Release;

    private static TransitionOutcome Advance(ProjectConfig p, Ticket t, StageDefinition current, DateTimeOffset now)
    {
        var next = p.NextStage(current.Id);
        if (next is null)
        {
            t.Status = StageStatus.Done;
            t.UpdatedAt = now;
            return new(true, null, $"{current.Id} completed");
        }
        EnterStage(t, next, now);
        return new(true, next.OnEnterStatus, $"advanced to {next.Id}");
    }

    private static TransitionOutcome SendBackTo(Ticket t, StageDefinition target, string? feedback, DateTimeOffset now)
    {
        EnterStage(t, target, now);
        t.Feedback = feedback;
        return new(true, target.OnEnterStatus, $"sent back to {target.Id}");
    }

    private static TransitionOutcome Deferred(string status, TransitionOutcome applied) =>
        applied with { Message = $"applying source move to '{status}': {applied.Message}" };

    /// <summary>The latest stage before <paramref name="stageId"/> that <paramref name="status"/> maps to (a send-back move).</summary>
    private static StageDefinition? EarlierStageFor(ProjectConfig p, string stageId, string status)
    {
        for (var i = IndexOf(p, stageId) - 1; i >= 0; i--)
        {
            var s = p.Pipeline[i];
            if (s.EntryStatuses.Any(e => Same(e, status)) || Same(s.OnEnterStatus, status)) return s;
        }
        return null;
    }

    private static void EnterStage(Ticket t, StageDefinition stage, DateTimeOffset now)
    {
        t.StageId = stage.Id;
        t.Status = stage.Kind == StageKind.Done ? StageStatus.Done : StageStatus.Pending;
        t.Attempts = 0;
        t.BlockedReason = null;
        t.Feedback = null;
        t.WaitingSince = null;
        t.ReviewDue = false;
        t.PendingSourceStatus = null;
        t.StageStartedAt = now;
        t.UpdatedAt = now;
        ClearApprovedHeads(t);
    }

    private static void ClearApprovedHeads(Ticket t)
    {
        t.ApprovedBranch = null;
        t.ApprovedHead = null;
        t.ApprovedBaseHead = null;
    }

    private static int IndexOf(ProjectConfig p, string stageId)
    {
        for (var i = 0; i < p.Pipeline.Count; i++)
            if (p.Pipeline[i].Id == stageId) return i;
        throw new KeyNotFoundException($"Stage '{stageId}' not found in project '{p.Id}'");
    }

    /// <summary>The SQLite converter keeps milliseconds and WaitingSince is the gate-round key, so it must not carry finer ticks.</summary>
    private static DateTimeOffset ToStoredPrecision(DateTimeOffset t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, t.Offset);

    private static TransitionOutcome NoChange(string message) => new(false, null, message);

    /// <summary>
    /// The developer's decision on the waiting round, taken before the transition clears <see cref="Ticket.WaitingSince"/>;
    /// null when there is no round to identify (no waiting time).
    /// </summary>
    private static GateDecisionInfo? DecisionOn(Ticket t, GateDecision decision) =>
        t.WaitingSince is { } since ? new(t.StageId, since, decision, Auto: false) : null;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
