using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;

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
    /// <summary>Deletes the ticket with its events, agent runs, gate reviews and gate decisions; false when there is no such ticket.</summary>
    Task<bool> DeleteAsync(string projectId, string key, CancellationToken ct);
    Task AddGateReviewAsync(GateReview review, CancellationToken ct);
    Task AddGateDecisionAsync(GateDecisionRecord decision, CancellationToken ct);
    /// <summary>The ticket's gate reviews, newest first, each with the latest decision of its round joined.</summary>
    Task<IReadOnlyList<GateReview>> GateReviewsAsync(Guid ticketId, CancellationToken ct);
    /// <summary>How often the developer's decisions matched the reviewer's verdict, per project and stage (see <see cref="ReviewAgreement"/>).</summary>
    Task<IReadOnlyList<StageAgreement>> ReviewAgreementAsync(string? projectId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct);
    Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct);
}

public sealed record AgentRunRecord(Guid TicketId, string StageId, DateTimeOffset StartedAt, TimeSpan Duration,
    bool Succeeded, decimal? CostUsd, string? Error, string RawLogPath, string? Summary = null, bool IsReview = false);

/// <param name="Decision">The latest decision of the same round (joined on read); null while undecided.</param>
public sealed record GateReview(Guid TicketId, string ProjectId, string StageId, DateTimeOffset WaitingSince, DateTimeOffset At,
    ReviewMode Mode, GatePolicy Gate, GateVerdict Verdict, GateDecision? Decision = null, bool AutoDecided = false);

public sealed record GateDecisionRecord(Guid TicketId, string ProjectId, string StageId, DateTimeOffset WaitingSince,
    GateDecision Decision, bool Auto, DateTimeOffset At);
