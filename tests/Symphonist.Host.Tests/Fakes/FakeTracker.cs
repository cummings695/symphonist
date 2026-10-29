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

    /// <summary>Thrown from TransitionAsync when set.</summary>
    public Exception? TransitionError { get; set; }

    /// <summary>How many times the tracker was queried.</summary>
    public int QueryCount => Volatile.Read(ref queryCount);
    private int queryCount;

    public Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref queryCount);
        return Task.FromResult(OnQuery?.Invoke() ?? Tickets.ToList());
    }

    /// <summary>Like the real tracker, a successful transition changes the ticket's status in later query results.</summary>
    public Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct)
    {
        if (TransitionError is not null) throw TransitionError;
        Transitions.Add((key, targetStatus));
        var ok = OnTransition(key, targetStatus);
        if (ok) Move(key, targetStatus);
        return Task.FromResult(ok);
    }

    /// <summary>Simulates the developer (or a transition) moving the card to <paramref name="status"/>.</summary>
    public void Move(string key, string status)
    {
        var index = Tickets.FindIndex(t => t.Key == key);
        if (index >= 0) Tickets[index] = Tickets[index] with { Status = status };
    }

    /// <summary>Attachment bytes by attachment id; an unknown id fails like a rejected download.</summary>
    public Dictionary<string, byte[]> AttachmentContent { get; } = [];

    /// <summary>Overrides the download (e.g. to throw or stall); default writes <see cref="AttachmentContent"/>.</summary>
    public Func<SourceAttachment, Stream, CancellationToken, Task>? OnDownload { get; set; }

    public async Task DownloadAttachmentAsync(string key, SourceAttachment attachment, Stream destination, CancellationToken ct)
    {
        if (OnDownload is not null)
        {
            await OnDownload(attachment, destination, ct);
            return;
        }

        if (!AttachmentContent.TryGetValue(attachment.Id, out var bytes))
            throw new HttpRequestException($"attachment {attachment.Id} not found");
        await destination.WriteAsync(bytes, ct);
    }

    /// <summary>Thrown from CommentAsync when set (e.g. an HttpRequestException).</summary>
    public Exception? CommentError { get; set; }

    public Task CommentAsync(string key, string markdown, CancellationToken ct)
    {
        if (CommentError is not null) throw CommentError;
        Comments.Add((key, markdown));
        return Task.CompletedTask;
    }
}
