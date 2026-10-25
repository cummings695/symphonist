using System.Text.RegularExpressions;
using Symphonist.Integrations.GitHub;
using Symphonist.Integrations;
using Symphonist.Core.Config;

namespace Symphonist.Core.Tests.Config;

public class SampleConfigTests
{
    [Fact]
    public void Example_project_in_samples_loads()
    {
        var p = ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.example.yaml"), BuiltInProviders.Registry);
        Assert.Equal("orders", p.Id);
        Assert.Equal(Isolation.Container, p.Agent.Isolation);
        Assert.Equal(7, p.Pipeline.Count);
        Assert.Equal(StageKind.Done, p.Pipeline[^1].Kind);
        Assert.EndsWith("acme-orders", p.Repo.LocalPath);
    }

    [Fact]
    public void Sample_config_loads_with_review_and_risk_areas()
    {
        var p = ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.example.yaml"), BuiltInProviders.Registry);
        Assert.True(p.RiskAreas.Count >= 1);
        Assert.Equal(ReviewMode.Off, p.Review.Mode);
        Assert.All(p.Pipeline, s => Assert.True(s.Gate is GatePolicy.None or GatePolicy.Human));
        Assert.Equal("In Review", p.Stage("implement").OnGateStatus);
    }

    [Fact]
    public void GitHub_example_project_loads()
    {
        var p = ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders-github.example.yaml"), BuiltInProviders.Registry);
        var tracker = Assert.IsType<GitHubProjectsTrackerConfig>(p.Tracker);
        var repo = Assert.IsType<GitHubRepoConfig>(p.Repo);
        Assert.IsType<GitHubActionsConfig>(p.Build);
        Assert.Equal(tracker.TokenSecret, repo.TokenSecret);
        Assert.Equal(StageKind.Done, p.Pipeline[^1].Kind);
    }

    [Fact]
    public void Jira_tracker_with_github_repo_loads()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Config", "orders.example.yaml"));
        const string github = "repo:\n  type: github\n  owner: acme\n  repo: orders\n  localPath: C:/src/acme-orders\n"
            + "  tokenSecret: github-acme\n\nbuild:\n  type: github-actions\n\nagent:";
        var swapped = Regex.Replace(text, @"(?ms)^repo:.*?^agent:", github);
        Assert.NotEqual(text, swapped);
        var path = Path.Combine(Path.GetTempPath(), $"symphonist-mixed-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, swapped);
        try
        {
            var p = ProjectConfigLoader.Load(path, BuiltInProviders.Registry);
            Assert.Equal("jira", p.Tracker.Type);
            Assert.IsType<GitHubRepoConfig>(p.Repo);
            Assert.IsType<GitHubActionsConfig>(p.Build);
        }
        finally { File.Delete(path); }
    }
}
