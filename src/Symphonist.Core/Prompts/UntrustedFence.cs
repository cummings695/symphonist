using System.Text.RegularExpressions;

namespace Symphonist.Core.Prompts;

/// <summary>
/// Marks untrusted text in a prompt. The fence id is random per run, so text inside cannot guess it, and forged
/// markers inside are neutralised. The prompt must say that only markers carrying the fence id delimit data.
/// </summary>
public static partial class UntrustedFence
{
    public static string NewNonce() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// "&lt;untrusted-{nonce}&gt;\n{text}\n&lt;/untrusted-{nonce}&gt;", with any <c>&lt;untrusted-</c> or
    /// <c>&lt;/untrusted-</c> in <paramref name="text"/> turned into <c>&amp;lt;…</c>.
    /// </summary>
    public static string Wrap(string nonce, string text) =>
        $"<untrusted-{nonce}>\n{ForgedMarker().Replace(text, "&lt;$1")}\n</untrusted-{nonce}>";

    [GeneratedRegex("<(/?untrusted-)", RegexOptions.IgnoreCase)]
    private static partial Regex ForgedMarker();
}
