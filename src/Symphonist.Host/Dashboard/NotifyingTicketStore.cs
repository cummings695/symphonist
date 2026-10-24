using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Dashboard;

/// <summary>Forwards to <paramref name="inner"/> and tells <paramref name="notifier"/> after each successful write. Reads pass through.</summary>
public sealed class NotifyingTicketStore(ITicketStore inner, BoardNotifier notifier) : ITicketStore
{
    public Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct) => inner.FindAsync(projectId, key, ct);

    public Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct) => inner.ListAsync(projectId, ct);

    public Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct) => inner.EventsAsync(ticketId, ct);

    public Task<IReadOnlyList<AgentRunRecord>> AgentRunsAsync(Guid ticketId, CancellationToken ct) => inner.AgentRunsAsync(ticketId, ct);

    public Task<IReadOnlyList<GateReview>> GateReviewsAsync(Guid ticketId, CancellationToken ct) => inner.GateReviewsAsync(ticketId, ct);

    public Task<IReadOnlyList<StageAgreement>> ReviewAgreementAsync(string? projectId, CancellationToken ct) => inner.ReviewAgreementAsync(projectId, ct);

    public Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct) => inner.AgentCostsAsync(ct);

    public async Task SaveAsync(Ticket ticket, CancellationToken ct)
    {
        await inner.SaveAsync(ticket, ct);
        notifier.Notify();
    }

    public async Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct)
    {
        await inner.AddEventAsync(ticketId, stageId, message, at, ct);
        notifier.Notify();
    }

    public async Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct)
    {
        await inner.AddAgentRunAsync(run, ct);
        notifier.Notify();
    }

    public async Task AddGateReviewAsync(GateReview review, CancellationToken ct)
    {
        await inner.AddGateReviewAsync(review, ct);
        notifier.Notify();
    }

    public async Task AddGateDecisionAsync(GateDecisionRecord decision, CancellationToken ct)
    {
        await inner.AddGateDecisionAsync(decision, ct);
        notifier.Notify();
    }

    public async Task<bool> DeleteAsync(string projectId, string key, CancellationToken ct)
    {
        var deleted = await inner.DeleteAsync(projectId, key, ct);
        if (deleted) notifier.Notify();
        return deleted;
    }

    public async Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct)
    {
        var reset = await inner.ResetInterruptedRunsAsync(isAgentStage, now, ct);
        if (reset > 0) notifier.Notify();
        return reset;
    }
}
