using Symphonist.Core.Config;

namespace Symphonist.Core.Tests.Config;

public class ProjectConfigLoaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Config", name);

    [Fact]
    public void Loads_fixture_with_defaults()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"));
        Assert.Equal("orders", p.Id);
        Assert.Equal(Isolation.Container, p.Agent.Isolation);
        Assert.Equal(60, p.Agent.TimeoutMinutes);
        Assert.Equal(3, p.MaxAttemptsPerStage);
        Assert.Equal("merge_commit", p.Repo.MergeStrategy);
        Assert.Equal(7, p.Pipeline.Count);
        Assert.Equal(GatePolicy.None, p.Stage("build").Gate);
        Assert.Equal("In Progress", p.Stage("plan").ApprovedWhenStatus);
        Assert.True(Path.IsPathFullyQualified(p.Repo.LocalPath));
        Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "plan.md"), p.PromptOverrides[StageKind.Plan]);
        Assert.Equal("deploy", p.NextStage("release")!.Id);
        Assert.Null(p.NextStage("done"));
    }

    [Fact]
    public void LoadAll_reads_yaml_files()
    {
        var all = ProjectConfigLoader.LoadAll(Path.GetDirectoryName(Fixture("orders.yaml"))!);
        Assert.Contains(all, c => c.Id == "orders");
    }

    [Theory]
    [InlineData("pipeline: []", "pipeline must contain at least one stage")]
    [InlineData("duplicate-stage-id", "duplicate stage id 'plan'")]
    [InlineData("last-stage-not-done", "last stage must be kind 'done'")]
    [InlineData("host-without-tools", "host isolation requires agent.allowedTools")]
    [InlineData("unknown-kind", "unknown stage kind 'qa'")]
    [InlineData("pipeline: [~]", "pipeline[0] is empty")]
    [InlineData("null-entry-status", "pipeline stage 'triage' entryStatuses contains an empty item")]
    [InlineData("null-allowed-domain", "agent.allowedDomains contains an empty item")]
    [InlineData("null-prompt", "missing required 'prompts.plan'")]
    public void Rejects_invalid_config(string variant, string expectedMessage)
    {
        var yaml = File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n");
        yaml = variant switch
        {
            "pipeline: []" => yaml[..yaml.IndexOf("pipeline:", StringComparison.Ordinal)] + "pipeline: []\n",
            "duplicate-stage-id" => yaml.Replace("id: build,", "id: plan,"),
            "last-stage-not-done" => string.Join('\n', yaml.Split('\n').Where(l => !l.Contains("id: done"))),
            "host-without-tools" => yaml.Replace("  type: claude-code\n", "  type: claude-code\n  isolation: host\n"),
            "unknown-kind" => yaml.Replace("kind: release", "kind: qa"),
            "pipeline: [~]" => yaml[..yaml.IndexOf("pipeline:", StringComparison.Ordinal)] + "pipeline: [~]\n",
            "null-entry-status" => yaml.Replace("entryStatuses: [Backlog]", "entryStatuses: [Backlog, ~]"),
            "null-allowed-domain" => yaml.Replace("allowedDomains: [api.nuget.org,", "allowedDomains: [~, api.nuget.org,"),
            "null-prompt" => yaml.Replace("plan: prompts/plan.md", "plan: ~"),
            _ => throw new ArgumentException(variant),
        };
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.yaml");
            File.WriteAllText(path, yaml);
            var ex = Assert.Throws<ProjectConfigException>(() => ProjectConfigLoader.Load(path));
            Assert.Contains(expectedMessage, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("type: jira", "type: github", "unsupported tracker.type 'github'")]
    [InlineData("type: bitbucket\n  workspace", "type: gitlab\n  workspace", "unsupported repo.type 'gitlab'")]
    [InlineData("type: claude-code", "type: codex", "unsupported agent.type 'codex'")]
    public void Rejects_unsupported_types(string from, string to, string expectedMessage)
    {
        var yaml = File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n").Replace(from, to);
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.yaml");
            File.WriteAllText(path, yaml);
            var ex = Assert.Throws<ProjectConfigException>(() => ProjectConfigLoader.Load(path));
            Assert.Contains(expectedMessage, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static ProjectConfig LoadYaml(string yaml)
    {
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "p.yaml");
            File.WriteAllText(path, yaml);
            return ProjectConfigLoader.Load(path);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string FixtureYaml() => File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n");

    [Fact]
    public void Risk_areas_and_review_are_parsed()
    {
        var yaml = FixtureYaml()
            .Replace("  allowedDomains: [api.nuget.org, registry.npmjs.org]\n",
                "  allowedDomains: [api.nuget.org, registry.npmjs.org]\n  image: acme/agent\n")
            + "riskAreas:\n"
            + "  - name: pricing\n"
            + "    description: Anything that changes order prices.\n"
            + "    paths: [\"src/Pricing/**\"]\n"
            + "review:\n"
            + "  mode: active\n"
            + "  prompt: prompts/review.md\n"
            + "  agent: { type: claude-code, model: opus }\n";
        var p = LoadYaml(yaml);

        var area = Assert.Single(p.RiskAreas);
        Assert.Equal("pricing", area.Name);
        Assert.Equal("Anything that changes order prices.", area.Description);
        Assert.Equal(["src/Pricing/**"], area.Paths);
        Assert.Equal(ReviewMode.Active, p.Review.Mode);
        Assert.Equal("opus", p.Review.Agent.Model);
        Assert.Equal(20, p.Review.Agent.TimeoutMinutes);
        Assert.Equal(p.Agent.Isolation, p.Review.Agent.Isolation);
        Assert.Equal("acme/agent", p.Review.Agent.Image);
        Assert.Equal(p.Agent.AllowedDomains, p.Review.Agent.AllowedDomains);
        Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "review.md"), p.Review.PromptPath);
    }

    [Fact]
    public void Review_defaults_to_off_with_read_only_tools()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"));
        Assert.Empty(p.RiskAreas);
        Assert.Equal(ReviewMode.Off, p.Review.Mode);
        Assert.Equal(ReviewConfig.ReadOnlyTools, p.Review.Agent.AllowedTools);
        Assert.Equal(["Read", "Grep", "Glob"], p.Review.Agent.AllowedTools);
        Assert.Null(p.Review.PromptPath);
        Assert.Equal(20, p.Review.Agent.TimeoutMinutes);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("autoWhenLowRisk")]
    public void Auto_gate_is_parsed(string gate)
    {
        var p = LoadYaml(FixtureYaml().Replace("kind: implement, gate: human", $"kind: implement, gate: {gate}"));
        Assert.Equal(GatePolicy.AutoWhenLowRisk, p.Stage("implement").Gate);
    }

    [Fact]
    public void Auto_gate_on_build_is_rejected()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml().Replace("{ id: build,     kind: build }", "{ id: build,     kind: build, gate: auto }")));
        Assert.Contains("stage 'build': gate 'auto' is only supported for triage, plan, implement and release", ex.Message);
    }

    [Fact]
    public void Unsupported_review_agent_is_rejected()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "review:\n  mode: shadow\n  agent: { type: codex }\n"));
        Assert.Contains("unsupported review.agent.type 'codex'", ex.Message);
    }

    [Fact]
    public void Duplicate_or_incomplete_risk_area_is_rejected()
    {
        var dup = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing, description: a }\n  - { name: pricing, description: b }\n"));
        Assert.Contains("duplicate risk area 'pricing'", dup.Message);

        var noDescription = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing }\n"));
        Assert.Contains("missing required 'riskAreas[].description'", noDescription.Message);

        var blankPath = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing, description: a, paths: [\"\"] }\n"));
        Assert.Contains("riskAreas 'pricing' paths contains an empty item", blankPath.Message);
    }

    [Fact]
    public void Reviews_only_gated_reviewable_stages()
    {
        var shadow = LoadYaml(FixtureYaml() + "review:\n  mode: shadow\n");
        Assert.True(shadow.Reviews(shadow.Stage("triage")));
        Assert.False(shadow.Reviews(shadow.Stage("build")));
        Assert.True(shadow.Reviews(shadow.Stage("release")));
        Assert.False(shadow.Reviews(shadow.Stage("deploy")));

        var off = ProjectConfigLoader.Load(Fixture("orders.yaml"));
        Assert.All(off.Pipeline, s => Assert.False(off.Reviews(s)));
    }
}
