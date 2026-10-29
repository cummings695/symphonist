using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Dashboard;

/// <param name="UsagePauses">The usage-limit pauses of providers some job of this project runs on.</param>
/// <param name="Touches">This week's touches against last week's; null when not loaded.</param>
/// <param name="StageTouches">The project's per-stage touches over the trend window (the strip's hover text).</param>
public sealed record BoardProject(string Id, string Name, bool Paused, Isolation Isolation, string? Problem, string Source,
    ReviewMode Review = ReviewMode.Off, IReadOnlyList<StageAgreement>? Agreement = null,
    IReadOnlyList<ProviderPause>? UsagePauses = null, TouchSummary? Touches = null,
    IReadOnlyList<StageTouches>? StageTouches = null);

/// <summary>A pipeline stage's touches; all zero when the developer never stepped in there.</summary>
public sealed record StatsStage(string StageId, StageTouches Touches);

public sealed record StatsProject(string Id, string Name, IReadOnlyList<StatsStage> Stages);

/// <summary>What the Stats page shows: per project, the stages (pipeline order, Done-kind left out) with their touches.</summary>
public sealed record StatsModel(IReadOnlyList<StatsProject> Projects, string? ProjectFilter);

/// <param name="Reviewing">An active auto gate whose review is still owed: not waiting on the developer yet.</param>
/// <param name="StageNumber">1-based position in the card's own project pipeline; null for an unknown project or stage and for a Done-kind stage.</param>
/// <param name="StageCount">Length of that pipeline, null whenever <paramref name="StageNumber"/> is.</param>
/// <param name="Known">The ticket's project and stage are in the configuration; otherwise the card offers no commands.</param>
/// <param name="GatheringEvidence">Evidence for the waiting gate round is still owed; the card still waits on the developer.</param>
/// <param name="EvidenceIssues">The ticket's latest evidence record found issues.</param>
public sealed record BoardCard(string Project, string Key, string Title, string StageId, StageStatus Status,
    string? BlockedReason, int Attempts, int? MaxAttempts, DateTimeOffset? Since,
    decimal? CostUsd, Uri? IssueUrl, Uri? PrUrl, string Source, bool Known, RiskLevel? Risk = null, bool Reviewing = false,
    int? StageNumber = null, int? StageCount = null, bool GatheringEvidence = false, bool EvidenceIssues = false)
{
    /// <summary>Waiting on the developer: at a gate and not still with the reviewer.</summary>
    public bool WaitingOnYou => Status == StageStatus.AwaitingApproval && !Reviewing;
}

/// <param name="Kind">The stage kind the column stands for; null for the unknown column.</param>
/// <param name="IsDone">The column holds the Done-kind stage (finished tickets); the board starts it folded on phones.</param>
public sealed record BoardColumn(string Id, string Title, IReadOnlyList<BoardCard> Cards, int HiddenCount, bool IsDone = false,
    StageKind? Kind = null);

public sealed record BoardCounts(int WaitingOnYou, int Running, int Blocked);

public sealed record BoardModel(IReadOnlyList<BoardProject> Projects, string? ProjectFilter,
    IReadOnlyList<BoardColumn> Columns, BoardCounts Counts);

public sealed record TicketDetailModel(BoardCard Card, string? Branch, string? SpecPath, string? PlanPath,
    Uri? ReleasePrUrl, string? Feedback, IReadOnlyList<AgentRunRecord> Runs, IReadOnlyList<TicketEvent> Events,
    IReadOnlyList<string> SendBackTargets, bool CanApprove, bool CanSendBack, bool CanUnblock,
    IReadOnlyList<GateReview> Reviews, string ProjectName, IReadOnlyList<PipelineStage> Pipeline,
    IReadOnlyList<EvidenceRecord> Evidence);

/// <summary>A project as the app frame lists it. <paramref name="Active"/> counts tickets not in a Done-kind stage.</summary>
public sealed record ShellProject(string Id, string Name, bool Paused, string? Problem, int WaitingOnYou, int Active);

/// <summary>What the app frame needs on every page: the projects with their counts, and totals across all tickets.</summary>
public sealed record ShellModel(IReadOnlyList<ShellProject> Projects, BoardCounts Totals);

/// <summary>One stage of a project's pipeline, as the ticket page's stepper shows it.</summary>
public sealed record PipelineStage(string Id, StageKind Kind, GatePolicy Gate);
