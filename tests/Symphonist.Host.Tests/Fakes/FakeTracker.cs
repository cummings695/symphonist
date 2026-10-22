using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeTracker : ITracker
{
    public List<SourceTicket> Tickets { get; } = [];
    public List<(string Key, string Markdown)> Comments { get; } = [];
    public List<(string Key, string Status)> Transitions { get; } = [];

    /// <summary>Overrides the query; default returns <see cref="Tickets"/>. Throw from it to simulate a failed query.</summary>
    public Func<IReadOnlyList<SourceTicket>>? OnQuery { get; set; }

    /// <summary>Decides whether a transition exists; default true.</summary>
    public Func<string, string, bool> OnTransition { get; set; } = (_, _) => true;

    public Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct) =>
        Task.FromResult(OnQuery?.Invoke() ?? Tickets.ToList());

    public Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct)
    {
        Transitions.Add((key, targetStatus));
        return Task.FromResult(OnTransition(key, targetStatus));
    }

    public Task CommentAsync(string key, string markdown, CancellationToken ct)
    {
        Comments.Add((key, markdown));
        return Task.CompletedTask;
    }
}
