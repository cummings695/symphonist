using System.Globalization;
using System.Text.RegularExpressions;
using Symphonist.Core.Agents;

namespace Symphonist.Integrations.ClaudeCode;

/// <summary>
/// Recognises the usage and rate limits Claude Code reports. Phrasings, as found in the CLI shipped in the agent image:
/// "Claude AI usage limit reached|&lt;epoch&gt;", "You've hit your limit · resets 3pm (Europe/London)",
/// "you have reached your weekly usage limit", "spend limit reached (daily; resets ...)", and API errors
/// "API Error: 429 {"type":"rate_limit_error"...}".
/// </summary>
public static class ClaudeUsageLimit
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Usage = new(@"usage limit|hit your limit|limit reached", Options);
    private static readonly Regex Epoch = new(@"limit reached\s*\|\s*(\d{9,11})", Options);
    private static readonly Regex ResetsAt = new(
        @"resets?\s+(?:at\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)(?:\s*\((?<zone>[A-Za-z_/+\-0-9]+)\))?", Options);
    private static readonly Regex Overloaded = new(@"overloaded|\b529\b", Options);
    // Only the API's own wording: a bare "429" or "rate limit" also turns up in logs and prose about other things.
    private static readonly Regex Rate = new(
        @"rate_limit_error|api error:?\s*429|\b429\s+too many requests|\brate limited\b|rate limit (?:exceeded|reached)", Options);

    public static UsageLimit? Detect(string text, DateTimeOffset now, TimeZoneInfo local)
    {
        if (Usage.IsMatch(text))
        {
            if (Epoch.Match(text) is { Success: true } e && long.TryParse(e.Groups[1].Value, out var seconds))
                return new UsageLimit(DateTimeOffset.FromUnixTimeSeconds(seconds));
            return new UsageLimit(ResetTime(text, now, local));
        }
        if (Overloaded.IsMatch(text)) return null;
        return Rate.IsMatch(text) ? new UsageLimit(now + UsageLimit.RateLimitPause) : null;
    }

    private static DateTimeOffset? ResetTime(string text, DateTimeOffset now, TimeZoneInfo local)
    {
        var m = ResetsAt.Match(text);
        if (!m.Success) return null;
        var zone = local;
        if (m.Groups["zone"].Success)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(m.Groups["zone"].Value); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59) return null;
        hour = hour % 12 + (m.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        return UsageLimit.NextOccurrence(now, zone, hour, minute);
    }
}
