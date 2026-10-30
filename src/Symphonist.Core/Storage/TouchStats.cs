using Symphonist.Core.Risk;

namespace Symphonist.Core.Storage;

/// <summary>One stage's touches over the trend window.</summary>
/// <param name="Overrules">Both overrule kinds: approving a round the reviewer called high, sending back one it called low.</param>
/// <param name="ApprovedByYou">Gate rounds whose latest decision is a human approval.</param>
/// <param name="WeeklyTouches">Touches per week, oldest first, one entry per bucket.</param>
public sealed record StageTouches(string ProjectId, string StageId, int SendBacks, int Unblocks, int Overrules,
    int ApprovedByYou, int AutoApproved, IReadOnlyList<int> WeeklyTouches);

/// <summary>This week's touches against last week's, and how many of this week's gate rounds were approved automatically.</summary>
public sealed record TouchSummary(int ThisWeek, int LastWeek, int SendBacks, int Unblocks, int Overrules, int AutoApproved, int Decided);

/// <summary>
/// Counts how often the developer stepped in. A touch is counted once: a send-back, an unblock, or an approval of a round
/// the reviewer called high. Each gate round counts by its latest decision; auto approvals are not touches.
/// </summary>
public static class TouchStats
{
    /// <summary>Monday 00:00 of the week <paramref name="now"/> falls in, in <paramref name="zone"/>.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now, TimeZoneInfo zone) => ToOffset(MondayOf(now, zone), zone);

    public static IReadOnlyList<StageTouches> Compute(IEnumerable<TouchRecord> touches, IEnumerable<GateDecisionRecord> decisions,
        IEnumerable<GateReview> reviews, DateTimeOffset now, TimeZoneInfo zone, int weeks = 8)
    {
        var first = MondayOf(now, zone).AddDays(-7 * (weeks - 1));
        var rows = new Dictionary<(string, string), Acc>();
        Acc Row(string project, string stage) =>
            rows.TryGetValue((project, stage), out var a) ? a : rows[(project, stage)] = new Acc(weeks);

        foreach (var t in touches)
        {
            if (Bucket(t.At, first, zone, weeks) is not { } bucket) continue;
            var row = Row(t.ProjectId, t.StageId);
            row.Weekly[bucket]++;
            if (t.Kind == TouchKind.SendBack) row.SendBacks++; else row.Unblocks++;
        }
        foreach (var (decision, risk) in Rounds(decisions, reviews))
        {
            if (Bucket(decision.At, first, zone, weeks) is not { } bucket) continue;
            var row = Row(decision.ProjectId, decision.StageId);
            if (decision.Auto) { row.AutoApproved++; continue; }
            if (decision.Decision == GateDecision.Approved)
            {
                row.ApprovedByYou++;
                if (risk == RiskLevel.High)
                {
                    row.Overrules++;
                    row.Weekly[bucket]++; // the approval is the touch
                }
            }
            else if (risk == RiskLevel.Low)
                row.Overrules++; // the send-back is already a touch of its own
        }
        return rows.OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2, StringComparer.Ordinal)
            .Select(x => new StageTouches(x.Key.Item1, x.Key.Item2, x.Value.SendBacks, x.Value.Unblocks, x.Value.Overrules,
                x.Value.ApprovedByYou, x.Value.AutoApproved, x.Value.Weekly)).ToList();
    }

    /// <summary>
    /// This week's split. <paramref name="stages"/> (from <see cref="Compute"/> over the same inputs) supplies the weekly totals;
    /// the inputs supply what only the raw records know.
    /// </summary>
    public static TouchSummary Summarize(IReadOnlyList<StageTouches> stages, IEnumerable<TouchRecord> touches,
        IEnumerable<GateDecisionRecord> decisions, IEnumerable<GateReview> reviews, DateTimeOffset now, TimeZoneInfo zone)
    {
        var week = WeekStart(now, zone);
        int sendBacks = 0, unblocks = 0, overrules = 0, auto = 0, decided = 0;
        foreach (var t in touches.Where(t => t.At >= week))
            if (t.Kind == TouchKind.SendBack) sendBacks++; else unblocks++;
        foreach (var (decision, risk) in Rounds(decisions, reviews).Where(r => r.Decision.At >= week))
        {
            decided++;
            if (decision.Auto) auto++;
            else if ((decision.Decision == GateDecision.Approved && risk == RiskLevel.High)
                || (decision.Decision == GateDecision.SentBack && risk == RiskLevel.Low)) overrules++;
        }
        return new TouchSummary(stages.Sum(s => s.WeeklyTouches[^1]), stages.Sum(s => s.WeeklyTouches[^2]),
            sendBacks, unblocks, overrules, auto, decided);
    }

    private const string Bars = "▁▂▃▄▅▆▇█";

    /// <summary>A bar per value, scaled to the largest (all zero: the lowest bar throughout).</summary>
    public static string Trend(IReadOnlyList<int> values)
    {
        var max = values.Count == 0 ? 0 : values.Max();
        return string.Concat(values.Select(v => Bars[max == 0 ? 0 : (int)Math.Ceiling(Math.Max(v, 0) * 7.0 / max)]));
    }

    private sealed class Acc(int weeks)
    {
        public int SendBacks, Unblocks, Overrules, ApprovedByYou, AutoApproved;
        public int[] Weekly { get; } = new int[weeks];
    }

    /// <summary>Each decided round once, by its latest decision, with the risk its newest review gave (null without a review).</summary>
    private static List<(GateDecisionRecord Decision, RiskLevel? Risk)> Rounds(IEnumerable<GateDecisionRecord> decisions,
        IEnumerable<GateReview> reviews)
    {
        var reviewList = reviews.ToList();
        return decisions.GroupBy(d => (d.TicketId, d.StageId, d.WaitingSince)).Select(g =>
        {
            var latest = GateAgreement.Latest(g, g.Key.TicketId, g.Key.StageId, g.Key.WaitingSince)!;
            var review = reviewList.LastOrDefault(r => r.TicketId == latest.TicketId && r.StageId == latest.StageId
                && r.WaitingSince == latest.WaitingSince);
            return (latest, (RiskLevel?)review?.Verdict.Risk);
        }).ToList();
    }

    private static int? Bucket(DateTimeOffset at, DateTime firstMonday, TimeZoneInfo zone, int weeks)
    {
        var days = (MondayOf(at, zone) - firstMonday).Days;
        if (days < 0) return null;
        var bucket = days / 7;
        return bucket < weeks ? bucket : null;
    }

    private static DateTime MondayOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime.Date;
        return local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
