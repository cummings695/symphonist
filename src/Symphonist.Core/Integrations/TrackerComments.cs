using System.Text.RegularExpressions;

namespace Symphonist.Core.Integrations;

/// <summary>
/// Symphonist comments under the developer's account, so its own comments are signed with a footer
/// to keep them apart from the developer's feedback.
/// </summary>
public static partial class TrackerComments
{
    public const string Footer = "— posted by Symphonist";

    public static string Sign(string markdown) => markdown + "\n\n" + Footer;

    public static bool IsSymphonist(string body) => body.TrimEnd().EndsWith(Footer, StringComparison.Ordinal);

    public const string ClaudeMarker = "@claude";

    [GeneratedRegex("@(claude)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mention();

    /// <summary>
    /// Breaks every "@claude" (any case, case kept) with a zero-width space after the "@", so text Symphonist posts
    /// under the developer's account never contains the substring "@claude": it can neither read as a request nor
    /// trigger the Claude GitHub App, whether that reads the rendered or the raw text. Idempotent.
    /// </summary>
    public static string EscapeMentions(string text) => Mention().Replace(text, "@​$1");

    /// <summary>
    /// The request text of a developer "@claude" comment (marker removed, trimmed); null when the body does not start
    /// with the marker.
    /// </summary>
    public static string? ClaudeRequest(string body)
    {
        var text = body.Trim();
        if (!text.StartsWith(ClaudeMarker, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text[ClaudeMarker.Length..];
        return rest.Length == 0 || char.IsWhiteSpace(rest[0]) ? rest.Trim() : null;
    }
}
