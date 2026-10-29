using System.Text.RegularExpressions;

namespace Symphonist.Host.Orchestration;

/// <summary>
/// Wraps untrusted text (ticket fields, diffs, attachment names) in <c>&lt;untrusted-{nonce}&gt;</c> markers. Use a fresh
/// nonce per prompt: text inside cannot guess it, and forged markers inside are neutralised.
/// </summary>
internal static partial class UntrustedFence
{
    public static string NewNonce() => Guid.NewGuid().ToString("N");

    public static string Wrap(string nonce, string text) =>
        $"<untrusted-{nonce}>\n{ForgedMarker().Replace(text, "&lt;$1")}\n</untrusted-{nonce}>";

    [GeneratedRegex("<(/?untrusted-)", RegexOptions.IgnoreCase)]
    private static partial Regex ForgedMarker();
}
