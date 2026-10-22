using Symphonist.Core.Config;

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

    public static TransitionOutcome StageSucceeded(ProjectConfig p, Ticket t, DateTimeOffset now)
    {
        var stage = p.Stage(t.StageId);
        t.Feedback = null;
        if (stage.Gate == GatePolicy.Human)
        {
            t.Status = StageStatus.AwaitingApproval;
            t.WaitingSince = now;
            t.UpdatedAt = now;
            return new(true, stage.OnGateStatus, $"{stage.Id} awaiting approval");
        }
        return Advance(p, t, stage, now);
    }

    public static TransitionOutcome StageFailed(ProjectConfig p, Ticket t, string error, DateTimeOffset now)
    {
        t.Attempts++;
        t.UpdatedAt = now;
        if (t.Attempts >= p.MaxAttemptsPerStage)
        {
            t.Status = StageStatus.Blocked;
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
        if (HasPostApprovalAction(stage.Kind))
        {
            t.Status = StageStatus.Approved;
            t.Attempts = 0; // the post-approval action (merge) gets its own retry budget
            t.UpdatedAt = now;
            return new(true, null, $"{stage.Id} approved");
        }
        return Advance(p, t, stage, now);
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
        var target = p.Stage(toStageId ?? t.StageId);
        EnterStage(t, target, now);
        t.Feedback = feedback;
        return new(true, target.OnEnterStatus, $"sent back to {target.Id}");
    }

    public static TransitionOutcome SourceStatusChanged(ProjectConfig p, Ticket t, string newStatus, string? latestFeedback, DateTimeOffset now)
    {
        var unchanged = Same(t.LastSourceStatus, newStatus);
        t.LastSourceStatus = newStatus;
        if (unchanged) return NoChange("source status unchanged");

        var stage = p.Stage(t.StageId);
        if (t.Status == StageStatus.AwaitingApproval && Same(stage.ApprovedWhenStatus, newStatus))
            return Approve(p, t, now);

        for (var i = IndexOf(p, t.StageId) - 1; i >= 0; i--)
        {
            var s = p.Pipeline[i];
            if (s.EntryStatuses.Any(e => Same(e, newStatus)) || Same(s.OnEnterStatus, newStatus))
                return SendBack(p, t, latestFeedback, s.Id, now);
        }
        return NoChange($"source status '{newStatus}' ignored");
    }

    public static TransitionOutcome Block(Ticket t, string reason, DateTimeOffset now)
    {
        t.Status = StageStatus.Blocked;
        t.BlockedReason = reason;
        t.UpdatedAt = now;
        return new(true, null, $"blocked: {reason}");
    }

    public static TransitionOutcome Unblock(Ticket t, DateTimeOffset now)
    {
        if (t.Status != StageStatus.Blocked) return NoChange("ticket is not blocked");
        t.Status = StageStatus.Pending;
        t.Attempts = 0;
        t.BlockedReason = null;
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

    private static void EnterStage(Ticket t, StageDefinition stage, DateTimeOffset now)
    {
        t.StageId = stage.Id;
        t.Status = stage.Kind == StageKind.Done ? StageStatus.Done : StageStatus.Pending;
        t.Attempts = 0;
        t.BlockedReason = null;
        t.Feedback = null;
        t.WaitingSince = null;
        t.StageStartedAt = now;
        t.UpdatedAt = now;
    }

    private static int IndexOf(ProjectConfig p, string stageId)
    {
        for (var i = 0; i < p.Pipeline.Count; i++)
            if (p.Pipeline[i].Id == stageId) return i;
        throw new KeyNotFoundException($"Stage '{stageId}' not found in project '{p.Id}'");
    }

    private static TransitionOutcome NoChange(string message) => new(false, null, message);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
