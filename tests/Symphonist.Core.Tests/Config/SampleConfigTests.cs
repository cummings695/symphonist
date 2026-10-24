using Symphonist.Core.Config;

namespace Symphonist.Core.Tests.Config;

public class SampleConfigTests
{
    [Fact]
    public void Example_project_in_samples_loads()
    {
        var p = ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.example.yaml"));
        Assert.Equal("orders", p.Id);
        Assert.Equal(Isolation.Container, p.Agent.Isolation);
        Assert.Equal(7, p.Pipeline.Count);
        Assert.Equal(StageKind.Done, p.Pipeline[^1].Kind);
        Assert.EndsWith("acme-orders", p.Repo.LocalPath);
    }

    [Fact]
    public void Sample_config_loads_with_review_and_risk_areas()
    {
        var p = ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.example.yaml"));
        Assert.True(p.RiskAreas.Count >= 1);
        Assert.Equal(ReviewMode.Off, p.Review.Mode);
        Assert.All(p.Pipeline, s => Assert.True(s.Gate is GatePolicy.None or GatePolicy.Human));
        Assert.Equal("In Review", p.Stage("implement").OnGateStatus);
    }
}
