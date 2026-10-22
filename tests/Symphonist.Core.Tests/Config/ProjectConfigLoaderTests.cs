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
}
