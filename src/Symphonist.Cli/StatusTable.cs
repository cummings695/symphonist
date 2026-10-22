using System.Text;

namespace Symphonist.Cli;

/// <summary>Plain-text, fixed-width rendering of API results.</summary>
public static class StatusTable
{
    public static string Render(IEnumerable<TicketDto> tickets, DateTimeOffset now)
    {
        var rows = tickets
            .OrderBy(t => t.Status == "AwaitingApproval" ? 0 : t.Status == "Blocked" ? 1 : 2)
            .ThenBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.Project, StringComparer.Ordinal)
            .Select(t => new[] { t.Key, t.Project, t.Stage, t.Status, Age(t.WaitingSince, now), t.BlockedReason ?? "" });
        return Table(["KEY", "PROJECT", "STAGE", "STATUS", "WAITING", "NOTE"], rows);
    }

    public static string RenderProjects(IEnumerable<ProjectDto> projects) =>
        Table(["ID", "NAME", "STATE", "ISOLATION", "PROBLEM"],
            projects.Select(p => new[] { p.Id, p.Name, p.Paused ? "paused" : "active", p.Isolation, p.Problem ?? "" }));

    public static string RenderDetail(TicketDetailDto t, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{t.Key}  {t.Title}");
        sb.AppendLine($"Project:  {t.Project}");
        sb.AppendLine($"Stage:    {t.Stage}");
        sb.AppendLine($"Status:   {t.Status}");
        sb.AppendLine($"Attempts: {t.Attempts}");
        if (t.WaitingSince is not null) sb.AppendLine($"Waiting:  {Age(t.WaitingSince, now)}");
        if (t.BlockedReason is not null) sb.AppendLine($"Blocked:  {t.BlockedReason}");
        if (t.PrUrl is not null) sb.AppendLine($"PR:       {t.PrUrl}");
        sb.AppendLine().AppendLine("Events:");
        foreach (var e in t.Events) sb.AppendLine($"  {e.At.ToLocalTime():yyyy-MM-dd HH:mm}  [{e.Stage}] {e.Message}");
        return sb.ToString();
    }

    /// <summary>Humanized age: "12m", "3h", "2d"; empty when not waiting.</summary>
    public static string Age(DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is not { } s) return "";
        var age = now - s;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    private static string Table(string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.Prepend(header).ToList();
        var widths = header.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
        var sb = new StringBuilder();
        foreach (var row in all)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        return sb.ToString();
    }
}
