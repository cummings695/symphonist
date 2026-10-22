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
}
