using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
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
    public List<GateReview> GateReviews { get; } = [];
    public List<GateDecisionRecord> GateDecisions { get; } = [];

    /// <summary>Thrown from ResetInterruptedRunsAsync when set.</summary>
    public Exception? ResetError { get; set; }

    /// <summary>When set, the next ListAsync throws (and clears the flag).</summary>
    public bool FailNextList { get; set; }

    /// <summary>When set, the next AddEventAsync throws (and clears the flag).</summary>
    public bool FailNextAddEvent { get; set; }

    /// <summary>When set, the next FindAsync throws (and clears the flag).</summary>
    public bool FailNextFind { get; set; }

    /// <summary>When set, SaveAsync waits for it before saving, ignoring cancellation (a write already under way).</summary>
    public TaskCompletionSource? HoldSaves { get; set; }

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
        lock (sync)
        {
            if (FailNextFind)
            {
                FailNextFind = false;
                throw new InvalidOperationException("database is locked");
            }
        }
        lock (sync) return Task.FromResult(Tickets.FirstOrDefault(t => t.ProjectId == projectId && t.Key == key) is { } t ? Copy(t) : null);
    }

    public Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct)
    {
        lock (sync)
        {
            if (FailNextList)
            {
                FailNextList = false;
                throw new InvalidOperationException("database is locked");
            }
            return Task.FromResult<IReadOnlyList<Ticket>>(
                Tickets.Where(t => projectId is null || t.ProjectId == projectId).Select(Copy).ToList());
        }
    }

    public async Task SaveAsync(Ticket ticket, CancellationToken ct)
    {
        if (HoldSaves is { } hold) await hold.Task;
        lock (sync)
        {
            if (Tickets.Any(t => t.Id != ticket.Id && t.ProjectId == ticket.ProjectId && t.Key == ticket.Key))
                throw new InvalidOperationException($"UNIQUE constraint failed: Tickets.ProjectId, Tickets.Key ({ticket.Key})");
            var index = Tickets.FindIndex(t => t.Id == ticket.Id);
            if (index >= 0) Tickets[index] = Copy(ticket);
            else Tickets.Add(Copy(ticket));
        }
    }

    public Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct)
    {
        lock (sync)
        {
            if (FailNextAddEvent)
            {
                FailNextAddEvent = false;
                throw new InvalidOperationException("database is locked");
            }
            Events.Add(new TicketEvent { TicketId = ticketId, StageId = stageId, Message = message, At = at });
        }
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

    public Task<IReadOnlyList<AgentRunRecord>> AgentRunsAsync(Guid ticketId, CancellationToken ct)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyList<AgentRunRecord>>(
                AgentRuns.Where(r => r.TicketId == ticketId).OrderBy(r => r.StartedAt).ToList());
    }

    public Task AddGateReviewAsync(GateReview review, CancellationToken ct)
    {
        lock (sync) GateReviews.Add(review);
        return Task.CompletedTask;
    }

    public Task AddGateDecisionAsync(GateDecisionRecord decision, CancellationToken ct)
    {
        lock (sync) GateDecisions.Add(decision);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GateReview>> GateReviewsAsync(Guid ticketId, CancellationToken ct)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyList<GateReview>>(GateReviews.Where(r => r.TicketId == ticketId)
                .Reverse().OrderByDescending(r => r.At)
                .Select(r => Join(r, GateDecisions)).ToList());
    }

    public Task<IReadOnlyList<StageAgreement>> ReviewAgreementAsync(string? projectId, CancellationToken ct)
    {
        lock (sync)
            return Task.FromResult(GateAgreement.Compute(
                GateReviews.Where(r => projectId is null || r.ProjectId == projectId),
                GateDecisions.Where(d => projectId is null || d.ProjectId == projectId)));
    }

    private static GateReview Join(GateReview review, IEnumerable<GateDecisionRecord> decisions) =>
        GateAgreement.Latest(decisions, review.TicketId, review.StageId, review.WaitingSince) is { } d
            ? review with { Decision = d.Decision, AutoDecided = d.Auto }
            : review;

    public Task<bool> DeleteAsync(string projectId, string key, CancellationToken ct)
    {
        lock (sync)
        {
            var ticket = Tickets.FirstOrDefault(t => t.ProjectId == projectId && t.Key == key);
            if (ticket is null) return Task.FromResult(false);
            Tickets.Remove(ticket);
            Events.RemoveAll(e => e.TicketId == ticket.Id);
            AgentRuns.RemoveAll(r => r.TicketId == ticket.Id);
            GateReviews.RemoveAll(r => r.TicketId == ticket.Id);
            GateDecisions.RemoveAll(d => d.TicketId == ticket.Id);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct)
    {
        lock (sync)
            return Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(AgentRuns.Where(r => r.CostUsd is not null)
                .GroupBy(r => r.TicketId).ToDictionary(g => g.Key, g => g.Sum(r => r.CostUsd!.Value)));
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
