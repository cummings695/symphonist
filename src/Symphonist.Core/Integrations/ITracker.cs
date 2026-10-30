namespace Symphonist.Core.Integrations;

public sealed record SourceComment(string Author, bool IsMine, DateTimeOffset Created, string Body);

public sealed record SourceAttachment(string Id, string FileName, string MimeType, long Size, DateTimeOffset Created);

public sealed record SourceTicket(string Key, string Title, string Description, string Status,
    IReadOnlyList<SourceComment> Comments, Uri WebUrl, IReadOnlyList<SourceAttachment>? Attachments = null)
{
    public IReadOnlyList<SourceAttachment> AttachmentList => Attachments ?? [];
}

public interface ITracker
{
    /// <summary>Returns all matching tickets across all pages; throws on any HTTP failure (never partial).</summary>
    Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct);

    /// <summary>Returns false when no available transition leads to <paramref name="targetStatus"/>.</summary>
    Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct);

    Task CommentAsync(string key, string markdown, CancellationToken ct);

    /// <summary>Streams one attachment's bytes into <paramref name="destination"/>; throws on any failure.</summary>
    Task DownloadAttachmentAsync(string key, SourceAttachment attachment, Stream destination, CancellationToken ct);
}
