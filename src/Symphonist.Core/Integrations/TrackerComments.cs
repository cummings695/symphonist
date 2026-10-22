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
}
