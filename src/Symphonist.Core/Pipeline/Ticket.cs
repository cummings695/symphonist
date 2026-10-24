using Symphonist.Core.Risk;

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

    /// <summary>
    /// The target of a tracker move that was skipped (integration error) or threw; retried each tick until it goes
    /// through, the developer moves the card, or Symphonist makes another move.
    /// </summary>
    public string? TrackerRetryStatus { get; set; }

    /// <summary>A reviewer run is owed for the current gate round (<see cref="StageId"/>, <see cref="WaitingSince"/>).</summary>
    public bool ReviewDue { get; set; }

    /// <summary>The latest reviewer verdict (the card's badge); null until a review has finished.</summary>
    public RiskLevel? Risk { get; set; }

    /// <summary>
    /// For a round the reviewer auto-approved at an implement or release gate: the branch that will be merged (the ticket
    /// branch, or main) and the commit of it that was reviewed. The merge waits for a new round if the branch moved.
    /// Null for human approvals and every other round.
    /// </summary>
    public string? ApprovedBranch { get; set; }

    /// <inheritdoc cref="ApprovedBranch"/>
    public string? ApprovedHead { get; set; }

    /// <summary>For an auto-approved release: the production commit the change was reviewed against.</summary>
    public string? ApprovedBaseHead { get; set; }

    /// <summary>
    /// When the host accepted a merge it performs asynchronously (still open meanwhile); the merge is not requested again
    /// for a while. Cleared with <see cref="ApprovedHead"/>.
    /// </summary>
    public DateTimeOffset? MergeAcceptedAt { get; set; }

    /// <summary>
    /// How often an auto-approved merge at the current stage was held because what would merge moved after the review.
    /// From the third hold on, the gate waits on the developer instead of owing yet another review. Reset on a new stage.
    /// </summary>
    public int MergeHolds { get; set; }

    /// <summary>A copy of every property (all are values or immutable strings, so a memberwise copy is complete).</summary>
    public Ticket Clone() => (Ticket)MemberwiseClone();
}
