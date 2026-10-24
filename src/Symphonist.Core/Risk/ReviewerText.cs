using System.Globalization;
using System.Text;

namespace Symphonist.Core.Risk;

/// <summary>
/// Makes reviewer (agent) text safe to show outside Symphonist (tracker comments, terminals): one line, no control or
/// invisible format characters (escape sequences, bidi overrides), and bounded in length and number.
/// </summary>
public static class ReviewerText
{
    public const int MaxSummaryChars = 500;
    public const int MaxItemChars = 200;
    public const int MaxItems = 10;

    private const string Cut = "…";

    /// <summary>
    /// <paramref name="text"/> as one line: line breaks and tabs become spaces, other control and format characters are
    /// dropped, and it is cut to <paramref name="max"/> characters (ending in "…" when cut).
    /// </summary>
    public static string Line(string? text, int max)
    {
        var sb = new StringBuilder(text?.Length ?? 0);
        foreach (var c in text ?? "")
        {
            if (c is '\n' or '\r' or '\t') sb.Append(' ');
            else if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format) sb.Append(c);
        }
        var line = sb.ToString().Trim();
        if (line.Length <= max) return line;
        var keep = max - Cut.Length;
        if (keep > 0 && char.IsHighSurrogate(line[keep - 1])) keep--;
        return line[..Math.Max(keep, 0)].TrimEnd() + Cut;
    }

    /// <summary>
    /// At most <see cref="MaxItems"/> non-empty items, each a <see cref="Line"/> of at most <see cref="MaxItemChars"/>,
    /// and a "(+N more)" item for the rest.
    /// </summary>
    public static IReadOnlyList<string> Items(IEnumerable<string> items)
    {
        var lines = items.Select(i => Line(i, MaxItemChars)).Where(l => l.Length > 0).ToList();
        return lines.Count <= MaxItems ? lines : [.. lines.Take(MaxItems), $"(+{lines.Count - MaxItems} more)"];
    }
}
