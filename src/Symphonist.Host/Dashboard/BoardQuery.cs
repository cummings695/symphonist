using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Orchestration;
using Symphonist.Integrations;

namespace Symphonist.Host.Dashboard;

/// <summary>Builds the board the dashboard renders: tickets grouped into stage columns, with links, cost and counts.</summary>
public sealed class BoardQuery(ProjectRegistry projects, ITicketStore store, Orchestrator orchestrator, ProviderLimits limits,
    TimeProvider? clock = null)
{
    /// <summary>How many weekly buckets the touch trend covers.</summary>
    public const int TrendWeeks = 8;

    public const string UnknownColumnId = "?unknown";
    public const int DoneLimit = 20;

    private const string UnknownSource = "Unknown";

    public async Task<BoardModel> LoadBoardAsync(string? projectFilter, CancellationToken ct)
    {
        var configured = projects.Projects;
        var filter = configured.FirstOrDefault(p => p.Id == projectFilter);
        var tickets = await store.ListAsync(filter?.Id, ct);
        var costs = await store.AgentCostsAsync(ct);
        var agreements = (await store.ReviewAgreementAsync(filter?.Id, ct)).ToLookup(a => a.ProjectId);
        var issues = await store.EvidenceIssuesAsync(ct);

        var byId = configured.ToDictionary(p => p.Id);
        var cards = tickets.Select(t => (Ticket: t,
            Card: ToCard(t, byId.GetValueOrDefault(t.ProjectId), costs, issues.GetValueOrDefault(t.Id)))).ToList();

        var pauses = limits.Pauses;
        var touchData = await LoadTouchDataAsync(filter?.Id, ct);
        var boardProjects = configured.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p =>
            {
                var own = touchData.ForProject(p.Id);
                return new BoardProject(p.Id, p.Name, projects.IsPaused(p.Id), p.Agent.Isolation,
                    orchestrator.ProblemOf(p.Id), SourceName(p), p.Review.Mode, agreements[p.Id].ToList(),
                    UsagePausesOf(p, pauses), own.Summary, own.Stages,
                    filter is null || filter.Id == p.Id ? ReleaseBannerOf(p, tickets) : null);
            })
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
            columns.Add(Column(def.Id, def.Title, inColumn, def.Kind == StageKind.Done, tickets, def.Kind));
        }

        var unknown = cards.Where(c => ColumnOf(c.Card, byId.GetValueOrDefault(c.Card.Project), filter) is null)
            .Select(c => c.Card).ToList();
        if (unknown.Count > 0) columns.Add(Column(UnknownColumnId, "Unknown stage", unknown, false, tickets));

        var counts = new BoardCounts(
            cards.Count(c => c.Card.WaitingOnYou),
            cards.Count(c => c.Card.Status == StageStatus.Running),
            cards.Count(c => c.Card.Status == StageStatus.Blocked));
        return new BoardModel(boardProjects, filter?.Id, columns, counts);
    }

    /// <summary>The release state of the project's tickets; null when the project has no release stage.</summary>
    private static ReleaseBanner? ReleaseBannerOf(ProjectConfig p, IReadOnlyList<Ticket> tickets)
    {
        if (p.ReleaseStage is null) return null;
        var own = tickets.Where(t => t.ProjectId == p.Id).ToList();
        var ready = own.Where(t => PipelineEngine.IsReadyToRelease(p, t)).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();
        var open = own.Where(t => t.Kind == TicketKind.Release && t.Status != StageStatus.Done)
            .OrderBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();
        return new ReleaseBanner(ready, open?.Key, open is null ? null : TicketLinks.PullRequest(p, open.ReleasePullRequestId));
    }

    /// <summary>Each project's stages with their touches over the trend window; one project when <paramref name="projectFilter"/> names one.</summary>
    public async Task<StatsModel> LoadStatsAsync(string? projectFilter, CancellationToken ct)
    {
        var filter = projects.Projects.FirstOrDefault(p => p.Id == projectFilter);
        var data = await LoadTouchDataAsync(filter?.Id, ct);
        var shown = filter is not null ? [filter] : projects.Projects.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var model = shown.Select(p =>
        {
            var byStage = data.ForProject(p.Id).Stages.ToDictionary(s => s.StageId);
            var stages = p.Pipeline.Where(s => s.Kind != StageKind.Done)
                .Select(s => new StatsStage(s.Id, byStage.GetValueOrDefault(s.Id)
                    ?? new StageTouches(p.Id, s.Id, 0, 0, 0, 0, 0, new int[TrendWeeks])))
                .ToList();
            return new StatsProject(p.Id, p.Name, stages);
        }).ToList();
        return new StatsModel(model, filter?.Id);
    }

    private sealed class TouchData(IReadOnlyList<TouchRecord> touches, IReadOnlyList<GateDecisionRecord> decisions,
        IReadOnlyList<GateReview> reviews, DateTimeOffset now, TimeZoneInfo zone)
    {
        public (TouchSummary Summary, IReadOnlyList<StageTouches> Stages) ForProject(string id)
        {
            var t = touches.Where(x => x.ProjectId == id).ToList();
            var d = decisions.Where(x => x.ProjectId == id).ToList();
            var r = reviews.Where(x => x.ProjectId == id).ToList();
            var stages = TouchStats.Compute(t, d, r, now, zone, TrendWeeks);
            return (TouchStats.Summarize(stages, t, d, r, now, zone), stages);
        }
    }

    /// <summary>The records the trend needs, loaded once for all projects (or the filtered one).</summary>
    private async Task<TouchData> LoadTouchDataAsync(string? projectId, CancellationToken ct)
    {
        var time = clock ?? TimeProvider.System;
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        var since = TouchStats.WeekStart(now, zone).AddDays(-7 * (TrendWeeks - 1));
        return new TouchData(await store.TouchesAsync(projectId, since, ct), await store.GateDecisionsAsync(projectId, since, ct),
            await store.GateReviewsSinceAsync(projectId, since, ct), now, zone);
    }

    /// <summary>Every configured project with its counts, and totals across all tickets; the app frame shows these on every page.</summary>
    public async Task<ShellModel> LoadShellAsync(CancellationToken ct)
    {
        var configured = projects.Projects;
        var byId = configured.ToDictionary(p => p.Id);
        var tickets = await store.ListAsync(null, ct);
        var noCosts = new Dictionary<Guid, decimal>();
        var cards = tickets.Select(t => (Ticket: t, Card: ToCard(t, byId.GetValueOrDefault(t.ProjectId), noCosts))).ToList();
        var byProject = cards.ToLookup(c => c.Card.Project);

        var shellProjects = configured.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p =>
            {
                var own = byProject[p.Id].Select(c => c.Card).ToList();
                return new ShellProject(p.Id, p.Name, projects.IsPaused(p.Id), orchestrator.ProblemOf(p.Id),
                    own.Count(c => c.WaitingOnYou),
                    own.Count(c => p.Pipeline.FirstOrDefault(s => s.Id == c.StageId)?.Kind != StageKind.Done));
            })
            .ToList();

        return new ShellModel(shellProjects, new BoardCounts(
            cards.Count(c => c.Card.WaitingOnYou),
            cards.Count(c => c.Card.Status == StageStatus.Running),
            cards.Count(c => c.Card.Status == StageStatus.Blocked)));
    }

    /// <summary>One ticket with its runs and events (newest first), or null when it is not in the store.</summary>
    public async Task<TicketDetailModel?> LoadTicketAsync(string projectId, string key, CancellationToken ct)
    {
        if (await store.FindAsync(projectId, key, ct) is not { } t) return null;
        var reviews = (await store.GateReviewsAsync(t.Id, ct)).OrderByDescending(r => r.At).ToList();
        var evidence = (await store.EvidenceAsync(t.Id, ct)).OrderByDescending(e => e.At).ToList();
        var runs = (await store.AgentRunsAsync(t.Id, ct)).OrderByDescending(r => r.StartedAt).ToList();
        var events = (await store.EventsAsync(t.Id, ct)).OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToList();
        var project = projects.Projects.FirstOrDefault(p => p.Id == t.ProjectId);

        var costs = runs.Where(r => r.CostUsd is not null).ToList();
        var card = ToCard(t, project, costs.Count == 0
            ? new Dictionary<Guid, decimal>()
            : new Dictionary<Guid, decimal> { [t.Id] = costs.Sum(r => r.CostUsd!.Value) },
            evidence.FirstOrDefault()?.HasIssues ?? false);

        // Without its project in the configuration the page is read-only (like the card on the board). A ticket whose
        // stage was removed (hand edit) can only be sent back, to any stage but done.
        var known = card.Known;
        var stageRemoved = !known && project is not null;
        var targets = known && t.Kind == TicketKind.Release ? [t.StageId]
            : known
            ? project!.Pipeline.Take(project.Pipeline.ToList().FindIndex(s => s.Id == t.StageId) + 1).Reverse().Where(s => s.Kind != StageKind.Done).Select(s => s.Id).ToList()
            : stageRemoved ? project!.Pipeline.Where(s => s.Kind != StageKind.Done).Select(s => s.Id).ToList()
            : [];
        var sendBack = (known || (stageRemoved && targets.Count > 0))
            && t.Status is (StageStatus.AwaitingApproval or StageStatus.Approved or StageStatus.Blocked);
        var (members, releaseOf) = await ReleaseLinksAsync(t, project, ct);
        return new TicketDetailModel(card, t.Branch, t.SpecPath, t.PlanPath,
            TicketLinks.PullRequest(project, t.ReleasePullRequestId), t.Feedback, runs, events, targets,
            known && t.Status == StageStatus.AwaitingApproval, sendBack, known && t.Status == StageStatus.Blocked, reviews,
            project?.Name ?? t.ProjectId,
            project?.Pipeline.Select(s => new PipelineStage(s.Id, s.Kind, project.GateOf(s))).ToList() ?? [], evidence,
            members, t.Kind == TicketKind.Release ? t.ReleaseSummary : null, releaseOf);
    }

    /// <summary>A release ticket's members (cards, key order), or a member's release key; both null for any other ticket.</summary>
    private async Task<(IReadOnlyList<BoardCard>? Members, string? ReleaseOf)> ReleaseLinksAsync(Ticket t, ProjectConfig? project,
        CancellationToken ct)
    {
        if (t.Kind != TicketKind.Release && t.ReleaseTicketId is null) return (null, null);
        var all = await store.ListAsync(t.ProjectId, ct);
        if (t.Kind != TicketKind.Release)
            return (null, all.FirstOrDefault(r => r.Kind == TicketKind.Release && r.Id == t.ReleaseTicketId)?.Key);
        var noCosts = new Dictionary<Guid, decimal>();
        return (all.Where(m => m.ReleaseTicketId == t.Id).OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => ToCard(m, project, noCosts)).ToList(), null);
    }

    private static BoardColumn Column(string id, string title, IEnumerable<BoardCard> cards, bool isDone,
        IReadOnlyList<Ticket> tickets, StageKind? kind = null)
    {
        if (!isDone)
            return new BoardColumn(id, title, cards.OrderBy(StatusRank).ThenBy(c => c.Since is null).ThenBy(c => c.Since)
                .ThenBy(c => c.Key, StringComparer.Ordinal).ToList(), 0, Kind: kind);

        var updated = tickets.ToDictionary(t => (t.ProjectId, t.Key), t => t.UpdatedAt);
        var ordered = cards.OrderByDescending(c => updated[(c.Project, c.Key)]).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        return new BoardColumn(id, title, ordered.Take(DoneLimit).ToList(), Math.Max(0, ordered.Count - DoneLimit), IsDone: true, Kind: kind);
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

    private static BoardCard ToCard(Ticket t, ProjectConfig? p, IReadOnlyDictionary<Guid, decimal> costs,
        bool evidenceIssues = false)
    {
        var stages = p?.Pipeline.ToList();
        var index = stages?.FindIndex(s => s.Id == t.StageId) ?? -1;
        var known = index >= 0;
        var placed = known && stages![index].Kind != StageKind.Done;
        return new(t.ProjectId, t.Key, t.Title, t.StageId, t.Status, t.BlockedReason,
            t.Attempts, p?.MaxAttemptsPerStage, Since(t), costs.TryGetValue(t.Id, out var cost) ? cost : null,
            TicketLinks.Issue(p, t.Key), TicketLinks.PullRequest(p, t.PullRequestId), p is null ? UnknownSource : SourceName(p),
            known, t.Risk, known && PipelineEngine.WaitsOnReviewer(p!, t),
            placed ? index + 1 : null, placed ? stages!.Count : null,
            known && PipelineEngine.WaitsOnEvidence(p!, t), evidenceIssues, t.PendingSourceStatus);
    }

    private static IReadOnlyList<ProviderPause> UsagePausesOf(ProjectConfig p, IReadOnlyList<ProviderPause> pauses)
    {
        var keys = UsageLimits.KeysInUse(p);
        return pauses.Where(x => keys.Contains(x.Key)).ToList();
    }

    private static DateTimeOffset? Since(Ticket t) => t.Status switch
    {
        StageStatus.Pending or StageStatus.Running => t.StageStartedAt,
        StageStatus.AwaitingApproval => t.WaitingSince,
        _ => t.UpdatedAt,
    };

    private static string SourceName(ProjectConfig p) => BuiltInProviders.Registry.Tracker(p.Tracker.Type).DisplayName;
}
