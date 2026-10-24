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
}
