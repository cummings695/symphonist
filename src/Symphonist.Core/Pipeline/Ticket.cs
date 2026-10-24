namespace Symphonist.Core.Pipeline;

public enum StageStatus { Pending, Running, AwaitingApproval, Approved, Blocked, Done }

public sealed class Ticket
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string StageId { get; set; } = "";
    public StageStatus Status { get; set; }
    public string? BlockedReason { get; set; }
    public string? Feedback { get; set; }
    public int Attempts { get; set; }
    public string? LastSourceStatus { get; set; }

    /// <summary>
    /// A source move made while the stage was Pending/Running (an approval or a send-back), applied when the stage
    /// finishes instead of being lost.
    /// </summary>
    public string? PendingSourceStatus { get; set; }
    public string? Branch { get; set; }
    public string? SpecPath { get; set; }
    public string? PlanPath { get; set; }
    public long? PullRequestId { get; set; }
    public string? MergeCommit { get; set; }
    public long? ReleasePullRequestId { get; set; }
    public string? ReleaseMergeCommit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StageStartedAt { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>The Created time of the newest developer "@claude" comment acted on; older ones are never acted on again.</summary>
    public DateTimeOffset? ClaudeCommentsHandledAt { get; set; }

    /// <summary>A copy of every property (all are values or immutable strings, so a memberwise copy is complete).</summary>
    public Ticket Clone() => (Ticket)MemberwiseClone();
}
