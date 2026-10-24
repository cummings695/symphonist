using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Dashboard;

public sealed record BoardProject(string Id, string Name, bool Paused, Isolation Isolation, string? Problem, string Source);

/// <param name="Known">The ticket's project and stage are in the configuration; otherwise the card offers no commands.</param>
public sealed record BoardCard(string Project, string Key, string Title, string StageId, StageStatus Status,
    bool WaitingOnYou, string? BlockedReason, int Attempts, int? MaxAttempts, DateTimeOffset? Since,
    decimal? CostUsd, Uri? IssueUrl, Uri? PrUrl, string Source, bool Known);

public sealed record BoardColumn(string Id, string Title, IReadOnlyList<BoardCard> Cards, int HiddenCount);

public sealed record BoardCounts(int WaitingOnYou, int Running, int Blocked);

public sealed record BoardModel(IReadOnlyList<BoardProject> Projects, string? ProjectFilter,
    IReadOnlyList<BoardColumn> Columns, BoardCounts Counts);

public sealed record TicketDetailModel(BoardCard Card, string? Branch, string? SpecPath, string? PlanPath,
    Uri? ReleasePrUrl, string? Feedback, IReadOnlyList<AgentRunRecord> Runs, IReadOnlyList<TicketEvent> Events,
    IReadOnlyList<string> SendBackTargets, bool CanApprove, bool CanSendBack, bool CanUnblock);
