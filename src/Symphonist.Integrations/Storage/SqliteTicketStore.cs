using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Integrations.Storage;

/// <summary>
/// SQLite-backed store. Long-lived and thread-safe: every operation uses a fresh DbContext, and
/// writes are serialized in-process (SQLite allows a single writer). Migrates lazily on first use.
/// </summary>
public sealed class SqliteTicketStore(string databasePath) : ITicketStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        DefaultTimeout = 30,
    }.ToString();

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _migrated;

    public async Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        return await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Key == key, ct);
    }

    public async Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var query = db.Tickets.AsNoTracking();
        if (projectId is not null) query = query.Where(t => t.ProjectId == projectId);
        return await query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Key).ToListAsync(ct);
    }

    public async Task SaveAsync(Ticket ticket, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            var exists = await db.Tickets.AnyAsync(t => t.Id == ticket.Id, ct);
            if (exists) db.Tickets.Update(ticket); else db.Tickets.Add(ticket);
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, StageId = stageId, Message = message, At = at });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> DeleteAsync(string projectId, string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Key == key, ct);
            if (ticket is null) return false;
            // Events and runs have no foreign key to the ticket, so remove them explicitly.
            db.TicketEvents.RemoveRange(await db.TicketEvents.Where(e => e.TicketId == ticket.Id).ToListAsync(ct));
            db.AgentRuns.RemoveRange(await db.AgentRuns.Where(r => r.TicketId == ticket.Id).ToListAsync(ct));
            db.GateReviews.RemoveRange(await db.GateReviews.Where(r => r.TicketId == ticket.Id).ToListAsync(ct));
            db.GateDecisions.RemoveRange(await db.GateDecisions.Where(d => d.TicketId == ticket.Id).ToListAsync(ct));
            db.Evidence.RemoveRange(await db.Evidence.Where(x => x.TicketId == ticket.Id).ToListAsync(ct));
            db.Touches.RemoveRange(await db.Touches.Where(x => x.TicketId == ticket.Id).ToListAsync(ct));
            db.Tickets.Remove(ticket);
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        return await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct);
    }

    public async Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.AgentRuns.Add(new AgentRunEntity
            {
                TicketId = run.TicketId, StageId = run.StageId, StartedAt = run.StartedAt, Duration = run.Duration,
                Succeeded = run.Succeeded, CostUsd = run.CostUsd, Error = run.Error, RawLogPath = run.RawLogPath,
                Summary = run.Summary, IsReview = run.IsReview, IsEvidence = run.IsEvidence,
            });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<AgentRunRecord>> AgentRunsAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.TicketId == ticketId)
            .OrderBy(r => r.StartedAt).ThenBy(r => r.Id).ToListAsync(ct);
        return runs.Select(r => new AgentRunRecord(r.TicketId, r.StageId, r.StartedAt, r.Duration, r.Succeeded,
            r.CostUsd, r.Error, r.RawLogPath, r.Summary, r.IsReview, r.IsEvidence)).ToList();
    }

    public async Task AddGateReviewAsync(GateReview review, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            var v = review.Verdict;
            db.GateReviews.Add(new GateReviewEntity
            {
                TicketId = review.TicketId, ProjectId = review.ProjectId, StageId = review.StageId,
                WaitingSince = review.WaitingSince, At = review.At, Mode = review.Mode, Gate = review.Gate,
                Risk = v.Risk, Summary = v.Summary, ReasonsJson = JsonSerializer.Serialize(v.Reasons),
                CheckedJson = JsonSerializer.Serialize(v.Checked), AreasJson = JsonSerializer.Serialize(v.AreasTouched),
                Error = v.Error,
            });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task AddGateDecisionAsync(GateDecisionRecord decision, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.GateDecisions.Add(new GateDecisionEntity
            {
                TicketId = decision.TicketId, ProjectId = decision.ProjectId, StageId = decision.StageId,
                WaitingSince = decision.WaitingSince, Decision = decision.Decision, Auto = decision.Auto, At = decision.At,
            });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<GateReview>> GateReviewsAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var reviews = await db.GateReviews.AsNoTracking().Where(r => r.TicketId == ticketId).ToListAsync(ct);
        var decisions = (await db.GateDecisions.AsNoTracking().Where(d => d.TicketId == ticketId)
            .OrderBy(d => d.Id).ToListAsync(ct)).Select(ToRecord).ToList();
        // Ordered client-side: At is stored as a binary long, which only sorts as the converter's bit layout allows.
        return reviews.OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Select(r =>
        {
            var review = ToReview(r);
            return GateAgreement.Latest(decisions, review.TicketId, review.StageId, review.WaitingSince) is { } d
                ? review with { Decision = d.Decision, AutoDecided = d.Auto }
                : review;
        }).ToList();
    }

    public async Task<IReadOnlyList<StageAgreement>> ReviewAgreementAsync(string? projectId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var reviews = db.GateReviews.AsNoTracking();
        var decisions = db.GateDecisions.AsNoTracking();
        if (projectId is not null)
        {
            reviews = reviews.Where(r => r.ProjectId == projectId);
            decisions = decisions.Where(d => d.ProjectId == projectId);
        }
        return GateAgreement.Compute(
            (await reviews.OrderBy(r => r.Id).ToListAsync(ct)).Select(ToReview),
            (await decisions.OrderBy(d => d.Id).ToListAsync(ct)).Select(ToRecord));
    }

    private static GateDecisionRecord ToRecord(GateDecisionEntity d) =>
        new(d.TicketId, d.ProjectId, d.StageId, d.WaitingSince, d.Decision, d.Auto, d.At);

    private static GateReview ToReview(GateReviewEntity r) =>
        new(r.TicketId, r.ProjectId, r.StageId, r.WaitingSince, r.At, r.Mode, r.Gate,
            new GateVerdict(r.Risk, r.Summary, Strings(r.ReasonsJson), Strings(r.CheckedJson), Strings(r.AreasJson), r.Error));

    private static List<string> Strings(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    public async Task AddEvidenceAsync(EvidenceRecord record, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.Evidence.Add(new EvidenceEntity
            {
                TicketId = record.TicketId, ProjectId = record.ProjectId, StageId = record.StageId,
                WaitingSince = record.WaitingSince, At = record.At, Head = record.Head, ReportJson = record.ReportJson,
                HasIssues = record.HasIssues, Error = record.Error,
                ScreenshotsJson = JsonSerializer.Serialize(record.Screenshots), Posted = record.Posted,
            });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Evidence.AsNoTracking().Where(x => x.TicketId == ticketId).ToListAsync(ct);
        // Ordered client-side: At is stored as a binary long.
        return rows.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, bool>> EvidenceIssuesAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Evidence.AsNoTracking().Select(x => new { x.TicketId, x.At, x.Id, x.HasIssues }).ToListAsync(ct);
        return rows.GroupBy(x => x.TicketId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).First().HasIssues);
    }

    private static EvidenceRecord ToRecord(EvidenceEntity x) =>
        new(x.TicketId, x.ProjectId, x.StageId, x.WaitingSince, x.At, x.Head, x.ReportJson, x.HasIssues, x.Error,
            Strings(x.ScreenshotsJson), x.Posted);

    public async Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        // SQLite stores decimal as TEXT, so sum client-side.
        var costs = await db.AgentRuns.AsNoTracking().Where(r => r.CostUsd != null)
            .Select(r => new { r.TicketId, Cost = r.CostUsd!.Value }).ToListAsync(ct);
        return costs.GroupBy(c => c.TicketId).ToDictionary(g => g.Key, g => g.Sum(c => c.Cost));
    }

    public async Task AddTouchAsync(TouchRecord touch, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.Touches.Add(new TouchEntity
            {
                TicketId = touch.TicketId, ProjectId = touch.ProjectId, StageId = touch.StageId, Kind = touch.Kind, At = touch.At,
            });
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<TouchRecord>> TouchesAsync(string? projectId, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var query = db.Touches.AsNoTracking();
        if (projectId is not null) query = query.Where(x => x.ProjectId == projectId);
        // Filtered and ordered client-side: At is stored as a binary long, which does not compare as a time.
        return (await query.ToListAsync(ct)).Where(x => x.At >= since).OrderBy(x => x.At).ThenBy(x => x.Id)
            .Select(x => new TouchRecord(x.TicketId, x.ProjectId, x.StageId, x.Kind, x.At)).ToList();
    }

    public async Task<IReadOnlyList<GateDecisionRecord>> GateDecisionsAsync(string? projectId, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var query = db.GateDecisions.AsNoTracking();
        if (projectId is not null) query = query.Where(x => x.ProjectId == projectId);
        // Filtered and ordered client-side: At is stored as a binary long, which does not compare as a time.
        return (await query.ToListAsync(ct)).Where(x => x.At >= since).OrderBy(x => x.At).ThenBy(x => x.Id).Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<GateReview>> GateReviewsSinceAsync(string? projectId, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var query = db.GateReviews.AsNoTracking();
        if (projectId is not null) query = query.Where(x => x.ProjectId == projectId);
        var from = since - TimeSpan.FromDays(30);
        return (await query.ToListAsync(ct)).Where(x => x.WaitingSince >= from).OrderBy(x => x.At).ThenBy(x => x.Id).Select(ToReview).ToList();
    }

    public async Task<IReadOnlyList<ProviderPause>> ProviderPausesAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.ProviderPauses.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new ProviderPause(x.Key, x.Since, x.ResetsAt, x.NextProbeAt)).ToList();
    }

    public async Task SaveProviderPauseAsync(ProviderPause pause, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            var row = await db.ProviderPauses.FirstOrDefaultAsync(x => x.Key == pause.Key, ct);
            if (row is null) db.ProviderPauses.Add(row = new ProviderPauseEntity { Key = pause.Key });
            row.Since = pause.Since;
            row.ResetsAt = pause.ResetsAt;
            row.NextProbeAt = pause.NextProbeAt;
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task RemoveProviderPauseAsync(string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            db.ProviderPauses.RemoveRange(await db.ProviderPauses.Where(x => x.Key == key).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            var running = await db.Tickets.Where(t => t.Status == StageStatus.Running).ToListAsync(ct);
            var interrupted = running.Where(t => isAgentStage(t.ProjectId, t.StageId)).ToList();
            foreach (var t in interrupted)
            {
                t.Status = StageStatus.Pending;
                t.Attempts++;
                t.UpdatedAt = now;
                db.TicketEvents.Add(new TicketEvent
                {
                    TicketId = t.Id, StageId = t.StageId, At = now, Message = "interrupted by restart; retrying",
                });
            }
            await db.SaveChangesAsync(ct);
            return interrupted.Count;
        }
        finally { _writeLock.Release(); }
    }

    private async Task<SymphonistDbContext> OpenAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        return NewContext();
    }

    private SymphonistDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SymphonistDbContext>().UseSqlite(_connectionString).Options);

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_migrated) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var db = NewContext();
            await db.Database.MigrateAsync(ct);
            _migrated = true;
        }
        finally { _initLock.Release(); }
    }
}
