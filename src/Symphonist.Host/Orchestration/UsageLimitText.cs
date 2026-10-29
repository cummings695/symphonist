using System.Globalization;
using Symphonist.Core.Agents;

namespace Symphonist.Host.Orchestration;

/// <summary>How a provider's usage-limit pause reads in events and on the dashboard.</summary>
public static class UsageLimitText
{
    /// <summary>
    /// "Claude usage limit, resets 11:00" (wall-clock time in <paramref name="zone"/>, with the date when that is not
    /// today there), or "Claude usage limit, trying again every 30 minutes" when the provider named no reset.
    /// </summary>
    public static string Describe(string key, DateTimeOffset? resetsAt, TimeZoneInfo zone, DateTimeOffset now) =>
        $"{UsageLimits.ProviderName(key)} usage limit, {Outlook(resetsAt, zone, now)}";

    /// <summary>"Claude jobs paused, resets 11:00" (or "..., trying again every 30 minutes"), for notifications.</summary>
    public static string Paused(string key, DateTimeOffset? resetsAt, TimeZoneInfo zone, DateTimeOffset now) =>
        $"{UsageLimits.ProviderName(key)} jobs paused, {Outlook(resetsAt, zone, now)}";

    private static string Outlook(DateTimeOffset? resetsAt, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (resetsAt is not { } at) return $"trying again every {(int)ProviderLimits.ProbeInterval.TotalMinutes} minutes";
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var format = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? "HH:mm" : "yyyy-MM-dd HH:mm";
        return $"resets {local.ToString(format, CultureInfo.InvariantCulture)}";
    }
}
