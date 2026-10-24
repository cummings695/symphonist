using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Risk;

public class ReviewAgreementTests
{
    [Fact]
    public void Agreement_counts_agree_disagree_and_false_lows()
    {
        var rows = ReviewAgreement.Compute([
            new("p", "implement", RiskLevel.Low, GateDecision.Approved),
            new("p", "implement", RiskLevel.Low, GateDecision.SentBack),
            new("p", "implement", RiskLevel.High, GateDecision.SentBack),
            new("p", "implement", RiskLevel.High, GateDecision.Approved)], []);
        var row = Assert.Single(rows);
        Assert.Equal(4, row.Decided);
        Assert.Equal(2, row.Agreed);
        Assert.Equal(1, row.FalseLows);
        Assert.Equal(0.5, row.Rate);
    }

    [Fact]
    public void Auto_approved_rounds_are_counted_separately()
    {
        var rows = ReviewAgreement.Compute(
            [new("p", "implement", RiskLevel.Low, GateDecision.Approved)],
            [("p", "plan"), ("p", "plan")]);
        Assert.Equal(["implement", "plan"], rows.Select(r => r.StageId));
        var plan = rows[1];
        Assert.Equal(0, plan.Decided);
        Assert.Equal(2, plan.AutoApproved);
        Assert.Null(plan.Rate);
    }
}
