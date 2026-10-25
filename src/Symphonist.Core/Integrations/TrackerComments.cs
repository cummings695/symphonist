using System.Text.RegularExpressions;

namespace Symphonist.Core.Integrations;

/// <summary>
/// Symphonist comments under the developer's account, so its own comments are signed with a footer
/// to keep them apart from the developer's feedback.
/// </summary>
public static class TrackerComments
{
    public const string Footer = "— posted by Symphonist";

    public static string Sign(string markdown) => markdown + "\n\n" + Footer;

    public static bool IsSymphonist(string body) => body.TrimEnd().EndsWith(Footer, StringComparison.Ordinal);

    /// <summary>The comment marker of a project that does not set <c>commentMarker</c>.</summary>
    public const string DefaultMarker = "@claude";

    private const string Claude = "claude";

    /// <summary>
    /// Breaks every "@claude" and, when given, every <paramref name="marker"/> (any case, case kept) with a zero-width
    /// space after the "@", so text Symphonist posts under the developer's account never contains either substring: it
    /// can neither read as a request nor trigger the Claude GitHub App, whether that reads the rendered or the raw
    /// text. Idempotent.
    /// </summary>
    public static string EscapeMentions(string text, string? marker = null)
    {
        var names = Regex.Escape(Claude);
        if (marker is { Length: > 1 } && marker[0] == '@' && !marker[1..].Equals(Claude, StringComparison.OrdinalIgnoreCase))
            names += "|" + Regex.Escape(marker[1..]);
        return Regex.Replace(text, "@(" + names + ")", "@\u200B$1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The request text of a developer comment starting with <paramref name="marker"/> (removed, trimmed); null when
    /// the body does not start with it, or the marker is followed by more than whitespace.
    /// </summary>
    public static string? ClaudeRequest(string body, string marker = DefaultMarker)
    {
        var text = body.Trim();
        if (!text.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text[marker.Length..];
        return rest.Length == 0 || char.IsWhiteSpace(rest[0]) ? rest.Trim() : null;
    }
}
