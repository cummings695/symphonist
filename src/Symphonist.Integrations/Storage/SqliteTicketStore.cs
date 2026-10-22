using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Symphonist.Core.Pipeline;
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
            });
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
