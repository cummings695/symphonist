using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Tests.Fakes;

/// <summary>
/// Behaves like the SQLite store: loads and saves copies (callers never share instances), upserts by Id,
/// enforces the unique (ProjectId, Key) index, and resets interrupted agent runs.
/// </summary>
public sealed class FakeTicketStore : ITicketStore
{
    private readonly Lock sync = new();

    /// <summary>The stored tickets (read in assertions; mutate through <see cref="SaveAsync"/>).</summary>
    public List<Ticket> Tickets { get; } = [];
    public List<TicketEvent> Events { get; } = [];
    public List<AgentRunRecord> AgentRuns { get; } = [];

    /// <summary>Thrown from ResetInterruptedRunsAsync when set.</summary>
    public Exception? ResetError { get; set; }

    public Ticket Single(string key)
    {
        lock (sync) return Tickets.Single(t => t.Key == key);
    }

    public IReadOnlyList<string> EventMessages(string key)
    {
        lock (sync)
        {
            var id = Tickets.Single(t => t.Key == key).Id;
            return Events.Where(e => e.TicketId == id).Select(e => e.Message).ToList();
        }
    }

    public Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct)
    {
        lock (sync) return Task.FromResult(Tickets.FirstOrDefault(t => t.ProjectId == projectId && t.Key == key) is { } t ? Copy(t) : null);
    }

    public Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyList<Ticket>>(
                Tickets.Where(t => projectId is null || t.ProjectId == projectId).Select(Copy).ToList());
    }

    public Task SaveAsync(Ticket ticket, CancellationToken ct)
    {
        lock (sync)
        {
            if (Tickets.Any(t => t.Id != ticket.Id && t.ProjectId == ticket.ProjectId && t.Key == ticket.Key))
                throw new InvalidOperationException($"UNIQUE constraint failed: Tickets.ProjectId, Tickets.Key ({ticket.Key})");
            var index = Tickets.FindIndex(t => t.Id == ticket.Id);
            if (index >= 0) Tickets[index] = Copy(ticket);
            else Tickets.Add(Copy(ticket));
        }
        return Task.CompletedTask;
    }

    public Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct)
    {
        lock (sync) Events.Add(new TicketEvent { TicketId = ticketId, StageId = stageId, Message = message, At = at });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct)
    {
        lock (sync) return Task.FromResult<IReadOnlyList<TicketEvent>>(Events.Where(e => e.TicketId == ticketId).ToList());
    }

    public Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct)
    {
        lock (sync) AgentRuns.Add(run);
        return Task.CompletedTask;
    }

    public Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct)
    {
        if (ResetError is not null) throw ResetError;
        lock (sync)
        {
            var interrupted = Tickets.Where(t => t.Status == StageStatus.Running && isAgentStage(t.ProjectId, t.StageId)).ToList();
            foreach (var t in interrupted)
            {
                t.Status = StageStatus.Pending;
                t.Attempts++;
                t.UpdatedAt = now;
                Events.Add(new TicketEvent { TicketId = t.Id, StageId = t.StageId, At = now, Message = "interrupted by restart; retrying" });
            }
            return Task.FromResult(interrupted.Count);
        }
    }

    private static Ticket Copy(Ticket t) => t.Clone();
}
