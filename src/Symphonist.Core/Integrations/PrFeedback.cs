using System.Text.Json;

namespace Symphonist.Core.Integrations;

/// <summary>One PR comment a fix run addresses: Request is the body with the @claude marker removed ("" when nothing followed).</summary>
public sealed record PrFeedbackComment(string Id, string Author, bool IsMine, string Request, string? Path, int? Line,
    string? ParentId);

/// <summary>
/// The "@claude" pull request comments that rerun implement on the same PR, and their stored form
/// (<see cref="Pipeline.Ticket.PrFeedbackJson"/>).
/// </summary>
public static class PrFeedback
{
    public const int MaxComments = 20, MaxRequestChars = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Tagged, unsigned comments newer than <paramref name="since"/>, from me (or anyone when
    /// <paramref name="fromOthers"/>), oldest first; the newest <see cref="MaxComments"/> are kept.
    /// </summary>
    public static IReadOnlyList<PullRequestComment> Select(IEnumerable<PullRequestComment> comments, DateTimeOffset? since,
        bool fromOthers)
    {
        var selected = comments
            .Where(c => (c.IsMine || fromOthers) && (since is null || c.Created > since)
                        && !TrackerComments.IsSymphonist(c.Body) && TrackerComments.ClaudeRequest(c.Body) is not null)
            .OrderBy(c => c.Created)
            .ToList();
        return selected.Count > MaxComments ? selected[^MaxComments..] : selected;
    }

    public static string ToJson(IReadOnlyList<PrFeedbackComment> comments) => JsonSerializer.Serialize(comments, Json);

    /// <summary>The stored comments; [] for null or invalid JSON.</summary>
    public static IReadOnlyList<PrFeedbackComment> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var comments = JsonSerializer.Deserialize<List<PrFeedbackComment>>(json, Json);
            return comments is null || comments.Any(c => c is null || c.Id is null || c.Author is null || c.Request is null)
                ? []
                : comments;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The comment as a fix run sees it: the request after the marker, at most <see cref="MaxRequestChars"/>.</summary>
    public static PrFeedbackComment From(PullRequestComment c) =>
        new(c.Id, c.Author, c.IsMine, Bound(TrackerComments.ClaudeRequest(c.Body) ?? ""), c.Path, c.Line, c.ParentId);

    private static string Bound(string text)
    {
        if (text.Length <= MaxRequestChars) return text;
        var end = char.IsHighSurrogate(text[MaxRequestChars - 1]) ? MaxRequestChars - 1 : MaxRequestChars;
        return text[..end];
    }
}
