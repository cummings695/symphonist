namespace Symphonist.Core.Risk;

public static class AutoApprovalComment
{
    /// <summary>Unsigned comment text; the caller signs it.</summary>
    public static string Format(string stageId, GateVerdict verdict) =>
        $"Auto-approved {stageId}: low risk.\n\nChecked:\n"
        + string.Join("\n", verdict.Checked.Select(c => $"- {c}"))
        + $"\n\nWhy it passed: {verdict.Summary}";
}
