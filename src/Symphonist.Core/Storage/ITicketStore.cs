using Symphonist.Core.Pipeline;

namespace Symphonist.Core.Storage;

public interface ITicketStore
{
    Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct);
    Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct);
    Task SaveAsync(Ticket ticket, CancellationToken ct);
    Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct);
    Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct);
    Task<IReadOnlyList<AgentRunRecord>> AgentRunsAsync(Guid ticketId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct);
    Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct);
}

public sealed record AgentRunRecord(Guid TicketId, string StageId, DateTimeOffset StartedAt, TimeSpan Duration,
    bool Succeeded, decimal? CostUsd, string? Error, string RawLogPath, string? Summary = null);
