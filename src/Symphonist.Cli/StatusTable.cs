using System.Globalization;
using System.Text;
using Symphonist.Core.Risk;

namespace Symphonist.Cli;

/// <summary>
/// Plain-text, fixed-width rendering of API results. Text from the API (ticket text, agent output, events) is printed
/// as single lines without control characters, so it cannot move the cursor or recolour the terminal.
/// </summary>
public static class StatusTable
{
    public static string Render(IEnumerable<TicketDto> tickets, DateTimeOffset now)
    {
        var rows = tickets
            .OrderBy(t => t.Status == "AwaitingApproval" ? 0 : t.Status == "Blocked" ? 1 : 2)
            .ThenBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.Project, StringComparer.Ordinal)
            .Select(t => new[] { t.Key, t.Project, t.Stage, t.Status, RiskWord(t.Risk), Age(t.WaitingSince, now), t.BlockedReason ?? "" });
        return Table(["KEY", "PROJECT", "STAGE", "STATUS", "RISK", "WAITING", "NOTE"], rows);
    }

    public static string RenderProjects(IEnumerable<ProjectDto> projects)
    {
        var list = projects.ToList();
        var table = Table(["ID", "NAME", "STATE", "REVIEW", "ISOLATION", "PROBLEM"],
            list.Select(p => new[] { p.Id, p.Name, p.Paused ? "paused" : "active", p.Review.ToLowerInvariant(), p.Isolation, p.Problem ?? "" }));

        // Agreement lines go indented under their project's row.
        var lines = table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder().AppendLine(lines[0]);
        for (var i = 0; i < list.Count; i++)
        {
            sb.AppendLine(lines[i + 1]);
            foreach (var a in Agreement(list[i]))
                sb.AppendLine($"  {a.StageId}: agrees {a.Agreed}/{a.Decided}"
                    + (a.FalseLows > 0 ? $", {a.FalseLows} low-risk sent back" : "")
                    + (a.AutoApproved > 0 ? $", {a.AutoApproved} auto-approved" : ""));
        }

        return sb.ToString();
    }

    /// <summary>Providers paused by a usage limit, with when each resets or (no reset known) is next tried; local times.</summary>
    public static string RenderLimits(IEnumerable<ProviderPauseDto> pauses) =>
        Table(["PROVIDER", "KEY", "SINCE", "RESETS / NEXT TRY"], pauses.Select(p => new[]
        {
            p.Provider, p.Key, Local(p.Since),
            p.ResetsAt is { } resets ? $"resets {Local(resets)}"
            : p.NextProbeAt is { } next ? $"next try {Local(next)}"
            : "",
        }));

    /// <summary>What Release now would ship: the ready tickets, the tickets shipping along, and commits from outside Symphonist.</summary>
    public static string RenderReleasePreview(ReleasePreviewDto preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine(preview.Ready.Count == 0 ? "Ready to release: none" : $"Ready to release: {string.Join(", ", preview.Ready.Select(Clean))}");
        if (preview.AlsoShipped.Count > 0)
        {
            sb.AppendLine("Also ships:");
            foreach (var a in preview.AlsoShipped)
                sb.AppendLine($"  {Clean(a.Key)}  at {Clean(a.StageId)} ({Clean(a.Status).ToLowerInvariant()}), "
                    + (a.Finishes ? "finishes with this release" : "stays where it is"));
        }
        if (preview.OutsideCommits > 0)
            sb.AppendLine($"Also ships: {preview.OutsideCommits} commit(s) from outside Symphonist");
        return sb.ToString();
    }

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static IEnumerable<StageAgreementDto> Agreement(ProjectDto p) =>
        (p.Agreement ?? []).Where(a => a.Decided > 0 || a.AutoApproved > 0);

    private static string RiskWord(string? risk) => risk?.ToLowerInvariant() ?? "";

    public static string RenderDetail(TicketDetailDto t, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Clean(t.Key)}  {Clean(t.Title)}");
        sb.AppendLine($"Project:  {Clean(t.Project)}");
        sb.AppendLine($"Stage:    {Clean(t.Stage)}");
        sb.AppendLine($"Status:   {Clean(t.Status)}");
        sb.AppendLine($"Attempts: {t.Attempts}");
        if (t.WaitingSince is not null) sb.AppendLine($"Waiting:  {Age(t.WaitingSince, now)}");
        if (t.BlockedReason is not null) sb.AppendLine($"Blocked:  {Clean(t.BlockedReason)}");
        if (t.PrUrl is not null) sb.AppendLine($"PR:       {Clean(t.PrUrl.ToString())}");
        if (t.Reviews?.OrderByDescending(r => r.At).FirstOrDefault() is { } r)
        {
            sb.AppendLine($"Review:   {Clean(r.Stage)} ({Clean(r.Mode).ToLowerInvariant()}) {Clean(r.Risk).ToLowerInvariant()} risk: "
                          + ReviewerText.Line(r.Summary, ReviewerText.MaxSummaryChars));
            foreach (var reason in ReviewerText.Items(r.Reasons)) sb.AppendLine($"  - {reason}");
        }
        sb.AppendLine().AppendLine("Events:");
        foreach (var e in t.Events) sb.AppendLine($"  {e.At.ToLocalTime():yyyy-MM-dd HH:mm}  [{Clean(e.Stage)}] {Clean(e.Message)}");
        return sb.ToString();
    }

    /// <summary>One line without control characters; not shortened (events and errors are shown in full).</summary>
    private static string Clean(string? text) => ReviewerText.Line(text, int.MaxValue);

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
        var all = rows.Select(r => r.Select(Clean).ToArray()).Prepend(header).ToList();
        var widths = header.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
        var sb = new StringBuilder();
        foreach (var row in all)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        return sb.ToString();
    }
}
