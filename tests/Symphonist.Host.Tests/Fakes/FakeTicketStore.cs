using Symphonist.Core.Pipeline;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeTicketStore : ITicketStore
{
    public List<Ticket> Tickets { get; } = [];
    public List<TicketEvent> Events { get; } = [];
    public List<AgentRunRecord> AgentRuns { get; } = [];

    public Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct) =>
        Task.FromResult(Tickets.FirstOrDefault(t => t.ProjectId == projectId && t.Key == key));

    public Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Ticket>>(Tickets.Where(t => projectId is null || t.ProjectId == projectId).ToList());

    public Task SaveAsync(Ticket ticket, CancellationToken ct)
    {
        if (!Tickets.Contains(ticket)) Tickets.Add(ticket);
        return Task.CompletedTask;
    }

    public Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct)
    {
        Events.Add(new TicketEvent { TicketId = ticketId, StageId = stageId, Message = message, At = at });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TicketEvent>>(Events.Where(e => e.TicketId == ticketId).ToList());

    public Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct)
    {
        AgentRuns.Add(run);
        return Task.CompletedTask;
    }

    public Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(0);
}
