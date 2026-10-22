namespace Symphonist.Core.Integrations;

public sealed record SourceComment(string Author, bool IsMine, DateTimeOffset Created, string Body);

public sealed record SourceTicket(string Key, string Title, string Description, string Status,
    IReadOnlyList<SourceComment> Comments, Uri WebUrl);

public interface ITracker
{
    /// <summary>Returns all matching tickets across all pages; throws on any HTTP failure (never partial).</summary>
    Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct);

    /// <summary>Returns false when no available transition leads to <paramref name="targetStatus"/>.</summary>
    Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct);

    Task CommentAsync(string key, string markdown, CancellationToken ct);
}
