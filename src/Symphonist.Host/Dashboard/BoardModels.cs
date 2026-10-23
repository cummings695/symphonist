using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;

namespace Symphonist.Host.Dashboard;

public sealed record BoardProject(string Id, string Name, bool Paused, Isolation Isolation, string? Problem, string Source);

public sealed record BoardCard(string Project, string Key, string Title, string StageId, StageStatus Status,
    bool WaitingOnYou, string? BlockedReason, int Attempts, int? MaxAttempts, DateTimeOffset? Since,
    decimal? CostUsd, Uri? IssueUrl, Uri? PrUrl, string Source);

public sealed record BoardColumn(string Id, string Title, IReadOnlyList<BoardCard> Cards, int HiddenCount);

public sealed record BoardCounts(int WaitingOnYou, int Running, int Blocked);

public sealed record BoardModel(IReadOnlyList<BoardProject> Projects, string? ProjectFilter,
    IReadOnlyList<BoardColumn> Columns, BoardCounts Counts);
