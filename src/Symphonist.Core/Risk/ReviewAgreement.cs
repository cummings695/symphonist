namespace Symphonist.Core.Risk;

public sealed record DecidedReview(string ProjectId, string StageId, RiskLevel Risk, GateDecision Decision);

public sealed record StageAgreement(string ProjectId, string StageId, int Decided, int Agreed, int FalseLows, int AutoApproved)
{
    public double? Rate => Decided == 0 ? null : (double)Agreed / Decided;
}

public static class ReviewAgreement
{
    public static IReadOnlyList<StageAgreement> Compute(IEnumerable<DecidedReview> human,
        IEnumerable<(string ProjectId, string StageId)> autoApproved)
    {
        var rows = new Dictionary<(string Project, string Stage), int[]>();
        int[] Row(string project, string stage)
        {
            if (!rows.TryGetValue((project, stage), out var row)) rows[(project, stage)] = row = new int[4];
            return row; // Decided, Agreed, FalseLows, AutoApproved
        }

        foreach (var d in human)
        {
            var row = Row(d.ProjectId, d.StageId);
            row[0]++;
            if ((d.Risk == RiskLevel.Low) == (d.Decision == GateDecision.Approved)) row[1]++;
            if (d.Risk == RiskLevel.Low && d.Decision == GateDecision.SentBack) row[2]++;
        }
        foreach (var (project, stage) in autoApproved) Row(project, stage)[3]++;

        return rows
            .OrderBy(r => r.Key.Project, StringComparer.Ordinal).ThenBy(r => r.Key.Stage, StringComparer.Ordinal)
            .Select(r => new StageAgreement(r.Key.Project, r.Key.Stage, r.Value[0], r.Value[1], r.Value[2], r.Value[3]))
            .ToList();
    }
}
