using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Risk;

public class AutoApprovalCommentTests
{
    [Fact]
    public void Comment_lists_checks_and_reason()
    {
        var verdict = new GateVerdict(RiskLevel.Low, "Docs-only change", [], ["tests pass", "no pricing files"], []);
        Assert.Equal(
            "Auto-approved implement: low risk.\n\nChecked:\n- tests pass\n- no pricing files\n\nWhy it passed: Docs-only change",
            AutoApprovalComment.Format("implement", verdict));
    }

    [Fact]
    public void Reviewer_text_is_cleaned_and_capped()
    {
        var checkedItems = Enumerable.Range(1, 12).Select(i => $"check {i}\n\n# injected heading").ToList();
        checkedItems[0] = new string('c', 300);
        var verdict = new GateVerdict(RiskLevel.Low, "fine\u001b[2J\n\n[click](https://evil.example) " + new string('s', 600),
            [], checkedItems, []);

        var lines = AutoApprovalComment.Format("implement", verdict).Split('\n');

        var items = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        Assert.Equal(ReviewerText.MaxItems + 1, items.Count);
        Assert.Equal("- " + new string('c', ReviewerText.MaxItemChars - 1) + "…", items[0]);
        Assert.Equal("- check 2  # injected heading", items[1]);
        Assert.Equal("- (+2 more)", items[^1]);
        var why = Assert.Single(lines, l => l.StartsWith("Why it passed: ", StringComparison.Ordinal));
        Assert.Equal(ReviewerText.MaxSummaryChars, why["Why it passed: ".Length..].Length);
        Assert.StartsWith("Why it passed: fine[2J  [click]", why);
        Assert.DoesNotContain(lines, l => l.Any(char.IsControl));
    }
}
