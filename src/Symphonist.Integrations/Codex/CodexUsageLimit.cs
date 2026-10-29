using System.Globalization;
using System.Text.RegularExpressions;
using Symphonist.Core.Agents;

namespace Symphonist.Integrations.Codex;

/// <summary>
/// Recognises the usage and rate limits Codex reports. Phrasings, as found in the Codex binary shipped in the agent
/// image: "You've hit your usage limit. Upgrade to Plus to continue using Codex (...)", with "try again at 3:04 PM" or
/// "Try again in 2 hours 5 minutes" appended, "Usage limit reached.", and "exceeded retry limit, last status: 429 Too
/// Many Requests".
/// </summary>
public static class CodexUsageLimit
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Usage = new(@"usage limit|hit your limit", Options);
    private static readonly Regex TryAgainIn = new(@"try again in\s+(?<d>(?:\d+\s*[a-z]+[\s,]*(?:and\s+)?)+)", Options);
    private static readonly Regex DurationPart = new(@"(?<n>\d+)\s*(?<u>[a-z]+)", Options);
    private static readonly Regex TryAgainAt = new(
        @"try again at\s+(?:(?<mon>[a-z]{3,9})\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<year>\d{4}),?\s+)?(?<h>\d{1,2}):(?<m>\d{2})\s*(?<ap>am|pm)",
        Options);
    private static readonly Regex Rate = new(@"rate[_ -]?limit|\b429\b|too many requests", Options);

    public static UsageLimit? Detect(string text, DateTimeOffset now, TimeZoneInfo local)
    {
        if (Usage.IsMatch(text))
            return new UsageLimit(Duration(text) is { } wait ? now + wait : ResetTime(text, now, local));
        return Rate.IsMatch(text) ? new UsageLimit(now + UsageLimit.RateLimitPause) : null;
    }

    private static TimeSpan? Duration(string text)
    {
        var m = TryAgainIn.Match(text);
        if (!m.Success) return null;
        var total = TimeSpan.Zero;
        foreach (Match part in DurationPart.Matches(m.Groups["d"].Value))
        {
            var n = double.Parse(part.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = part.Groups["u"].Value.ToLowerInvariant();
            if (unit.StartsWith('d')) total += TimeSpan.FromDays(n);
            else if (unit.StartsWith('h')) total += TimeSpan.FromHours(n);
            else if (unit.StartsWith('m')) total += TimeSpan.FromMinutes(n);
            else if (unit.StartsWith('s')) total += TimeSpan.FromSeconds(n);
            else return null;
        }
        return total > TimeSpan.Zero ? total : null;
    }

    private static DateTimeOffset? ResetTime(string text, DateTimeOffset now, TimeZoneInfo local)
    {
        var m = TryAgainAt.Match(text);
        if (!m.Success) return null;
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hour is < 1 or > 12 || minute > 59) return null;
        hour = hour % 12 + (m.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);

        if (!m.Groups["mon"].Success) return UsageLimit.NextOccurrence(now, local, hour, minute);
        // "Nov 3rd, 2026 3:04 PM": a dated reset is on that local day.
        if (!DateTime.TryParseExact($"{m.Groups["mon"].Value} {m.Groups["day"].Value} {m.Groups["year"].Value}",
                ["MMM d yyyy", "MMMM d yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return UsageLimit.NextOccurrence(now, local, hour, minute);
        var wall = DateTime.SpecifyKind(day.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, local.GetUtcOffset(wall));
    }
}
