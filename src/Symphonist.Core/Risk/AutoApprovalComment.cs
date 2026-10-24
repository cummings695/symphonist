namespace Symphonist.Core.Risk;

public static class AutoApprovalComment
{
    /// <summary>
    /// Unsigned comment text; the caller signs it. The reviewer's text is cleaned and capped (<see cref="ReviewerText"/>):
    /// it leaves Symphonist for the tracker.
    /// </summary>
    public static string Format(string stageId, GateVerdict verdict) =>
        $"Auto-approved {stageId}: low risk.\n\nChecked:\n"
        + string.Join("\n", ReviewerText.Items(verdict.Checked).Select(c => $"- {c}"))
        + $"\n\nWhy it passed: {ReviewerText.Line(verdict.Summary, ReviewerText.MaxSummaryChars)}";
}
