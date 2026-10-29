using System.Globalization;
using System.Text;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;
using Symphonist.Host.Notifications;

namespace Symphonist.Host.Orchestration;

/// <summary>A tracked ticket whose merge is on main but not in production, so the release ships it too.</summary>
/// <param name="Finishes">Whether it joins the batch and finishes with it (skipping its stages up to the release). False
/// for a ticket at an agent stage (it is being reworked), a done ticket, and a ticket finishing its own release PR.</param>
public sealed record AlsoShipped(string Key, string StageId, StageStatus Status, bool Finishes);

/// <summary>What Release now would ship, shown to the developer to confirm.</summary>
/// <param name="Ready">Keys of the tickets ready to release (ordinal order).</param>
/// <param name="OutsideCommits">Commits on main, not in production, that match no tracked ticket.</param>
/// <param name="MainHead">The main commit the release branch is created at.</param>
/// <param name="Unavailable">Why Release now is unavailable (nothing to release, an open release, ...); null when it is.</param>
/// <param name="ProductionBranch">The branch the release goes to (shown in the confirmation).</param>
public sealed record ReleasePreview(string ProjectId, IReadOnlyList<string> Ready, IReadOnlyList<AlsoShipped> AlsoShipped,
    int OutsideCommits, string? MainHead, string? Unavailable, string? ProductionBranch = null);

public sealed record ReleaseStartResult(bool Started, string Message, string? ReleaseKey);

/// <summary>
/// Release batches: previews what a release would ship, starts one as a release ticket with a frozen release branch
/// (pushed at the previewed main head, so later merges into main wait for the next release), and computes what happens
/// to its members when it merges or is cancelled. The caller (the orchestrator, under its lock) saves and applies the
/// returned outcomes.
/// </summary>
public sealed class ReleaseCoordinator(ITicketStore store, IGitWorkspace git, TimeProvider time, NotificationCenter? notifications = null)
{
    /// <summary>The workspace Release now reads main and production in.</summary>
    public const string PreviewWorkspace = "_release";

    public const string Changed = "the release changed; review it again";

    /// <summary>Per project: whether tickets were ready at the last check; absent until the first check.</summary>
    private readonly Dictionary<string, bool> readyAtLastCheck = new(StringComparer.Ordinal);
    private readonly Lock sync = new();

    public async Task<ReleasePreview> PreviewAsync(ProjectConfig p, CancellationToken ct) => (await ComputeAsync(p, ct)).Preview;

    /// <summary>
    /// Starts the release <paramref name="confirmed"/> describes, unless what it would ship changed since (then nothing
    /// happens): creates and pushes <c>symphonist/release-{n}</c> at the confirmed main head, the release ticket
    /// <c>_release-{n}</c>, and links its members.
    /// </summary>
    public async Task<ReleaseStartResult> StartAsync(ProjectConfig p, ReleasePreview confirmed, CancellationToken ct)
    {
        if (confirmed.ProjectId != p.Id) return new(false, Changed, null);
        var current = await ComputeAsync(p, ct);
        var preview = current.Preview;
        if (preview.Unavailable is { } unavailable) return new(false, unavailable, null);
        if (!SameRelease(preview, confirmed)) return new(false, Changed, null);

        var n = 1 + current.Tickets.Where(t => t.Kind == TicketKind.Release).Select(t => NumberOf(t.Key)).DefaultIfEmpty(0).Max();
        var main = p.Repo.MainBranch;
        try
        {
            // A release branch already on origin (a forgotten release, or an attempt that failed after pushing) is never
            // reused: its number is skipped. The preview workspace has just fetched every branch.
            while (await git.ResolveCommitAsync(current.Workspace!, $"refs/remotes/origin/symphonist/release-{n}", ct) is not null)
                n++;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new(false, $"could not read the repository: {e.Message}", null);
        }
        var key = $"_release-{n}";
        var branch = $"symphonist/release-{n}";
        string head;
        try
        {
            // A workspace left by an earlier attempt keeps its local branch where it was; start from a fresh clone so the
            // branch starts at origin's main as just fetched. It must be exactly the head the developer confirmed.
            await git.RemoveAsync(p, key, ct);
            var ws = await git.PrepareAsync(p, key, branch, ct);
            var resolved = await git.ResolveCommitAsync(ws, "HEAD", ct);
            if (resolved is null || !SameCommit(resolved, preview.MainHead))
            {
                // Main moved since the preview's fetch, or the branch appeared on origin meanwhile.
                var mainNow = await git.ResolveCommitAsync(ws, $"refs/remotes/origin/{main}", ct);
                return new(false, !SameCommit(mainNow, preview.MainHead)
                    ? Changed
                    : $"{branch} is at {resolved ?? "no commit"}, not at {main}'s {preview.MainHead}; the release was not started", null);
            }
            head = resolved;
            await git.PushAsync(ws, branch, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new(false, $"could not create {branch}: {e.Message}", null);
        }

        var now = time.GetUtcNow();
        var members = current.Members;
        var keys = members.Select(m => m.Key).ToList();
        var title = keys.Count == 0 ? $"Release {n}" : $"Release {n}: {string.Join(", ", keys)}";
        if (title.Length > MaxTitle) title = title[..(MaxTitle - 1)] + "…";
        var release = PipelineEngine.CreateRelease(p, n, title, branch, Summary(current), now);
        release.ReleaseBaseHead = head; // the merge waits while the branch is anywhere else
        await store.SaveAsync(release, ct);
        await store.AddEventAsync(release.Id, release.StageId,
            $"release {n} started from {main} at {preview.MainHead}: {(keys.Count == 0 ? "no tickets" : string.Join(", ", keys))}",
            now, ct);
        foreach (var m in members)
        {
            m.ReleaseTicketId = release.Id;
            m.UpdatedAt = now;
            await store.SaveAsync(m, ct);
            await store.AddEventAsync(m.Id, m.StageId, $"in release {n}", now, ct);
        }
        return new(true, $"{key} started" + (keys.Count == 0 ? "" : $" with {string.Join(", ", keys)}"), key);
    }

    /// <summary>
    /// The release merged (<see cref="Ticket.ReleaseMergeCommit"/>): every member still in it moves past the release
    /// stage, except a member being reworked at an agent stage (its card was moved back while the release was open),
    /// which only leaves the release. Empty when the merge commit or the release stage is missing.
    /// </summary>
    public async Task<IReadOnlyList<(Ticket Member, TransitionOutcome Outcome)>> CompleteAsync(ProjectConfig p, Ticket release,
        CancellationToken ct)
    {
        if (p.ReleaseStage is null || release.ReleaseMergeCommit is not { } merge) return [];
        var now = time.GetUtcNow();
        return (await MembersAsync(p, release, ct)).Select(m =>
        {
            if (p.Pipeline.FirstOrDefault(s => s.Id == m.StageId) is { } stage && StageExecutor.IsAgentStage(stage.Kind))
            {
                m.ReleaseTicketId = null;
                m.UpdatedAt = now;
                return (m, new TransitionOutcome(true, null, $"{release.Key} merged; {m.Key} stays at {m.StageId} for its rework"));
            }
            return (m, PipelineEngine.ReleaseCompleted(p, m, merge, now));
        }).ToList();
    }

    /// <summary>The release was cancelled: every member still in it leaves it (ready to release again, or its own progress).</summary>
    public async Task<IReadOnlyList<(Ticket Member, TransitionOutcome Outcome)>> CancelAsync(ProjectConfig p, Ticket release,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return (await MembersAsync(p, release, ct)).Select(m => (m, PipelineEngine.ReleaseCancelled(p, m, release.Key, now))).ToList();
    }

    /// <summary>
    /// Proposes a release: posts ReleaseReady when the project's ready set goes from empty to non-empty. While a release
    /// is open nothing counts as ready (Release now is unavailable). The first check (after a start) only records.
    /// </summary>
    public async Task CheckReadyAsync(ProjectConfig p, CancellationToken ct)
    {
        var tickets = await store.ListAsync(p.Id, ct);
        var ready = tickets.Any(t => t.Kind == TicketKind.Release && t.Status != StageStatus.Done)
            ? 0
            : tickets.Count(t => PipelineEngine.IsReadyToRelease(p, t));
        bool? before;
        lock (sync)
        {
            before = readyAtLastCheck.TryGetValue(p.Id, out var b) ? b : null;
            readyAtLastCheck[p.Id] = ready > 0;
        }
        if (before == false && ready > 0)
            notifications?.Post(new NotificationEvent(NotificationKind.ReleaseReady, p.Id, null,
                ready == 1 ? "1 ticket ready to release" : $"{ready} tickets ready to release"));
    }

    private const int MaxTitle = 120;

    /// <param name="Members">Ready tickets, then the also-shipped ones that finish with the release.</param>
    /// <param name="Workspace">The preview workspace, as just fetched; null when the release is unavailable.</param>
    private sealed record Computed(ReleasePreview Preview, IReadOnlyList<Ticket> Tickets, IReadOnlyList<Ticket> Members,
        IReadOnlyList<Ticket> ReadyTickets, string? Workspace = null);

    private async Task<Computed> ComputeAsync(ProjectConfig p, CancellationToken ct)
    {
        Computed Unavailable(string why, IReadOnlyList<Ticket>? tickets = null) =>
            new(new ReleasePreview(p.Id, [], [], 0, null, why, p.Repo.ProductionBranch), tickets ?? [], [], []);

        if (p.ReleaseStage is not { } releaseStage) return Unavailable("the pipeline has no release stage");
        var tickets = await store.ListAsync(p.Id, ct);
        if (tickets.FirstOrDefault(t => t.Kind == TicketKind.Release && t.Status != StageStatus.Done) is { } open)
            return Unavailable($"{open.Key} is still open; newly ready tickets wait for the next release", tickets);

        var (main, production) = (p.Repo.MainBranch, p.Repo.ProductionBranch);
        string? mainHead;
        IReadOnlyList<string> commits;
        string ws;
        try
        {
            ws = await git.PrepareAsync(p, PreviewWorkspace, main, ct);
            // Full ref names: a tag or local branch called "origin/main" must not shadow the remote-tracking branch.
            mainHead = await git.ResolveCommitAsync(ws, $"refs/remotes/origin/{main}", ct);
            var productionHead = await git.ResolveCommitAsync(ws, $"refs/remotes/origin/{production}", ct);
            if (mainHead is null) return Unavailable($"cannot resolve origin/{main}", tickets);
            if (productionHead is null) return Unavailable($"cannot resolve origin/{production}", tickets);
            commits = await git.FirstParentCommitsAsync(ws, $"{productionHead}..{mainHead}", ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Unavailable($"could not read the repository: {e.Message}", tickets);
        }

        var ready = tickets.Where(t => PipelineEngine.IsReadyToRelease(p, t)).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var readyIds = ready.Select(t => t.Id).ToHashSet();
        var shipped = new Dictionary<Guid, Ticket>();
        var outside = 0;
        foreach (var commit in commits)
        {
            var matches = tickets.Where(t => t.Kind == TicketKind.Work && t.MergeCommit is { } m && SameCommit(m, commit)).ToList();
            if (matches.Count == 0) outside++;
            foreach (var t in matches.Where(t => !readyIds.Contains(t.Id))) shipped.TryAdd(t.Id, t);
        }
        if (commits.Count == 0 && ready.Count == 0)
            return Unavailable($"nothing to release: {production} already contains {main}", tickets);

        var releaseIndex = IndexOf(p, releaseStage.Id);
        var also = shipped.Values.OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => (Ticket: t, Finishes: Finishes(p, t, releaseIndex))).ToList();
        var members = ready.Concat(also.Where(a => a.Finishes).Select(a => a.Ticket)).ToList();
        var preview = new ReleasePreview(p.Id, ready.Select(t => t.Key).ToList(),
            also.Select(a => new AlsoShipped(a.Ticket.Key, a.Ticket.StageId, a.Ticket.Status, a.Finishes)).ToList(),
            outside, mainHead, null, production);
        return new Computed(preview, tickets, members, ready, ws);
    }

    /// <summary>
    /// An also-shipped ticket joins the batch only when the release can finish it: a work ticket, not done, in no batch,
    /// without a release PR of its own, at a stage up to the release stage that no agent works on.
    /// </summary>
    private static bool Finishes(ProjectConfig p, Ticket t, int releaseIndex) =>
        t is { Kind: TicketKind.Work, ReleaseTicketId: null, ReleasePullRequestId: null } && t.Status != StageStatus.Done
        && p.Pipeline.FirstOrDefault(s => s.Id == t.StageId) is { } stage
        && !StageExecutor.IsAgentStage(stage.Kind) && stage.Kind != StageKind.Done
        && IndexOf(p, stage.Id) <= releaseIndex;

    private static string Summary(Computed c)
    {
        var sb = new StringBuilder();
        if (c.ReadyTickets.Count == 0) sb.Append("Tickets: none ready to release.\n");
        else
        {
            sb.Append("Tickets:\n");
            foreach (var t in c.ReadyTickets) sb.Append("- ").Append(t.Key).Append(": ").Append(OneLine(t.Title)).Append('\n');
        }
        var also = c.Preview.AlsoShipped;
        if (also.Count > 0 || c.Preview.OutsideCommits > 0)
        {
            sb.Append("\nAlso shipped:\n");
            foreach (var a in also) sb.Append("- ").Append(a.Key).Append(" (still at ").Append(a.StageId).Append(")\n");
            if (c.Preview.OutsideCommits > 0)
                sb.Append("- ").Append(c.Preview.OutsideCommits.ToString(CultureInfo.InvariantCulture))
                    .Append(" commit(s) from outside Symphonist\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Equal when the same tickets would ship the same way from the same main head.</summary>
    private static bool SameRelease(ReleasePreview a, ReleasePreview b) =>
        a.Ready.SequenceEqual(b.Ready, StringComparer.Ordinal)
        && a.AlsoShipped.Select(x => (x.Key, x.Finishes)).SequenceEqual(b.AlsoShipped.Select(x => (x.Key, x.Finishes)))
        && a.OutsideCommits == b.OutsideCommits
        && a.MainHead is not null && SameCommit(a.MainHead, b.MainHead);

    private async Task<List<Ticket>> MembersAsync(ProjectConfig p, Ticket release, CancellationToken ct) =>
        (await store.ListAsync(p.Id, ct)).Where(t => t.ReleaseTicketId == release.Id).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();

    /// <summary>A full hash or an abbreviation of at least 7 characters of the same commit, ignoring case.</summary>
    private static bool SameCommit(string? a, string? b) =>
        a is { Length: >= 7 } && b is { Length: >= 7 }
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    private static int NumberOf(string key) =>
        key.StartsWith("_release-", StringComparison.Ordinal)
        && int.TryParse(key["_release-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static int IndexOf(ProjectConfig p, string stageId)
    {
        for (var i = 0; i < p.Pipeline.Count; i++)
            if (p.Pipeline[i].Id == stageId) return i;
        return -1;
    }
}
