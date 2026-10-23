using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Dashboard;

/// <summary>Builds the board the dashboard renders: tickets grouped into stage columns, with links, cost and counts.</summary>
public sealed class BoardQuery(ProjectRegistry projects, ITicketStore store, Orchestrator orchestrator)
{
    public const string UnknownColumnId = "?unknown";
    public const int DoneLimit = 20;

    private const string UnknownSource = "Unknown";

    public async Task<BoardModel> LoadBoardAsync(string? projectFilter, CancellationToken ct)
    {
        var configured = projects.Projects;
        var filter = configured.FirstOrDefault(p => p.Id == projectFilter);
        var tickets = await store.ListAsync(filter?.Id, ct);
        var costs = await store.AgentCostsAsync(ct);

        var byId = configured.ToDictionary(p => p.Id);
        var cards = tickets.Select(t => (Ticket: t, Card: ToCard(t, byId.GetValueOrDefault(t.ProjectId), costs))).ToList();

        var boardProjects = configured.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new BoardProject(p.Id, p.Name, projects.IsPaused(p.Id), p.Agent.Isolation,
                orchestrator.ProblemOf(p.Id), SourceName(p)))
            .ToList();

        var columns = new List<BoardColumn>();
        var columnDefs = filter is not null
            ? filter.Pipeline.Select(s => (Id: s.Id, Title: s.Id, s.Kind)).ToList()
            : configured.SelectMany(p => p.Pipeline).Select(s => s.Kind).Distinct().Order()
                .Select(k => (Id: k.ToString(), Title: k.ToString(), Kind: k)).ToList();

        foreach (var def in columnDefs)
        {
            var inColumn = cards.Where(c => ColumnOf(c.Card, byId.GetValueOrDefault(c.Card.Project), filter) == def.Id)
                .Select(c => c.Card);
            columns.Add(Column(def.Id, def.Title, inColumn, def.Kind == StageKind.Done, tickets));
        }

        var unknown = cards.Where(c => ColumnOf(c.Card, byId.GetValueOrDefault(c.Card.Project), filter) is null)
            .Select(c => c.Card).ToList();
        if (unknown.Count > 0) columns.Add(Column(UnknownColumnId, "Unknown stage", unknown, false, tickets));

        var counts = new BoardCounts(
            cards.Count(c => c.Card.Status == StageStatus.AwaitingApproval),
            cards.Count(c => c.Card.Status == StageStatus.Running),
            cards.Count(c => c.Card.Status == StageStatus.Blocked));
        return new BoardModel(boardProjects, filter?.Id, columns, counts);
    }

    /// <summary>One ticket with its runs and events (newest first), or null when it is not in the store.</summary>
    public async Task<TicketDetailModel?> LoadTicketAsync(string projectId, string key, CancellationToken ct)
    {
        if (await store.FindAsync(projectId, key, ct) is not { } t) return null;
        var runs = (await store.AgentRunsAsync(t.Id, ct)).OrderByDescending(r => r.StartedAt).ToList();
        var events = (await store.EventsAsync(t.Id, ct)).OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToList();
        var project = projects.Projects.FirstOrDefault(p => p.Id == t.ProjectId);

        var costs = runs.Where(r => r.CostUsd is not null).ToList();
        var card = ToCard(t, project, costs.Count == 0
            ? new Dictionary<Guid, decimal>()
            : new Dictionary<Guid, decimal> { [t.Id] = costs.Sum(r => r.CostUsd!.Value) });

        // Without its project or stage in the configuration the page is read-only.
        var stageIndex = project?.Pipeline.ToList().FindIndex(s => s.Id == t.StageId) ?? -1;
        var known = stageIndex >= 0;
        var targets = known
            ? project!.Pipeline.Take(stageIndex + 1).Reverse().Where(s => s.Kind != StageKind.Done).Select(s => s.Id).ToList()
            : [];
        var sendBack = known && t.Status is (StageStatus.AwaitingApproval or StageStatus.Approved or StageStatus.Blocked);
        return new TicketDetailModel(card, t.Branch, t.SpecPath, t.PlanPath,
            TicketLinks.PullRequest(project, t.ReleasePullRequestId), t.Feedback, runs, events, targets,
            known && t.Status == StageStatus.AwaitingApproval, sendBack, known && t.Status == StageStatus.Blocked);
    }

    private static BoardColumn Column(string id, string title, IEnumerable<BoardCard> cards, bool isDone,
        IReadOnlyList<Ticket> tickets)
    {
        if (!isDone)
            return new BoardColumn(id, title, cards.OrderBy(StatusRank).ThenBy(c => c.Since is null).ThenBy(c => c.Since)
                .ThenBy(c => c.Key, StringComparer.Ordinal).ToList(), 0);

        var updated = tickets.ToDictionary(t => (t.ProjectId, t.Key), t => t.UpdatedAt);
        var ordered = cards.OrderByDescending(c => updated[(c.Project, c.Key)]).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        return new BoardColumn(id, title, ordered.Take(DoneLimit).ToList(), Math.Max(0, ordered.Count - DoneLimit));
    }

    /// <summary>The column id a card belongs in, or null when its project or stage is not in the configuration.</summary>
    private static string? ColumnOf(BoardCard card, ProjectConfig? project, ProjectConfig? filter)
    {
        var stage = project?.Pipeline.FirstOrDefault(s => s.Id == card.StageId);
        if (stage is null) return null;
        return filter is not null ? stage.Id : stage.Kind.ToString();
    }

    private static int StatusRank(BoardCard c) => c.Status switch
    {
        StageStatus.AwaitingApproval => 0,
        StageStatus.Blocked => 1,
        StageStatus.Running => 2,
        StageStatus.Approved => 3,
        StageStatus.Pending => 4,
        _ => 5,
    };

    private static BoardCard ToCard(Ticket t, ProjectConfig? p, IReadOnlyDictionary<Guid, decimal> costs) =>
        new(t.ProjectId, t.Key, t.Title, t.StageId, t.Status, t.Status == StageStatus.AwaitingApproval, t.BlockedReason,
            t.Attempts, p?.MaxAttemptsPerStage, Since(t), costs.TryGetValue(t.Id, out var cost) ? cost : null,
            TicketLinks.Issue(p, t.Key), TicketLinks.PullRequest(p, t.PullRequestId), p is null ? UnknownSource : SourceName(p));

    private static DateTimeOffset? Since(Ticket t) => t.Status switch
    {
        StageStatus.Pending or StageStatus.Running => t.StageStartedAt,
        StageStatus.AwaitingApproval => t.WaitingSince,
        _ => t.UpdatedAt,
    };

    private static string SourceName(ProjectConfig p) =>
        p.Tracker.Type.Length == 0 ? UnknownSource : char.ToUpperInvariant(p.Tracker.Type[0]) + p.Tracker.Type[1..];
}
