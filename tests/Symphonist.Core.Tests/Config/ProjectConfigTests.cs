using Symphonist.Core.Config;
using Symphonist.Integrations;

namespace Symphonist.Core.Tests.Config;

public class ProjectConfigTests
{
    private static readonly ProjectConfig P =
        ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.yaml"), BuiltInProviders.Registry);

    private static ProjectConfig With(string stageId, GatePolicy gate, ReviewMode mode = ReviewMode.Off) =>
        P with
        {
            Pipeline = P.Pipeline.Select(s => s.Id == stageId ? s with { Gate = gate } : s).ToList(),
            Review = new ReviewConfig(mode, P.Agent, null),
        };

    [Fact] public void Release_gate_none_is_human()
    {
        var p = With("release", GatePolicy.None);
        Assert.Equal(GatePolicy.Human, p.GateOf(p.Stage("release")));
        var q = With("implement", GatePolicy.None);
        Assert.Equal(GatePolicy.None, q.GateOf(q.Stage("implement")));
        Assert.Equal(GatePolicy.None, P.GateOf(P.Stage("build")));
        Assert.Equal(GatePolicy.Human, P.GateOf(P.Stage("release")));
        var auto = With("release", GatePolicy.AutoWhenLowRisk);
        Assert.Equal(GatePolicy.AutoWhenLowRisk, auto.GateOf(auto.Stage("release")));
    }

    [Fact] public void Release_with_gate_none_is_still_reviewed()
    {
        var p = With("release", GatePolicy.None, ReviewMode.Active);
        Assert.True(p.Reviews(p.Stage("release")));
    }

    [Fact] public void Release_stage_is_the_first_release_kind_stage()
    {
        Assert.Equal("release", P.ReleaseStage?.Id);
        var none = P with { Pipeline = P.Pipeline.Where(s => s.Kind != StageKind.Release).ToList() };
        Assert.Null(none.ReleaseStage);
    }
}
