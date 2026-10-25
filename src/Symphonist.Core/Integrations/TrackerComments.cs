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

    [GeneratedRegex(@"(?<!\\)@claude", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mention();

    /// <summary>
    /// Escapes every "@claude" (any case) as "\@claude", so text Symphonist posts under the developer's account can
    /// neither read as a request nor trigger the Claude GitHub App. An already escaped mention is left as it is.
    /// </summary>
    public static string EscapeMentions(string text) => Mention().Replace(text, @"\$0");

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
