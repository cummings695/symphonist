using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Core.Tests.Integrations;

public class ProviderSectionTests
{
    private sealed class Dto
    {
        public string? Owner { get; set; }
        public int Project { get; set; }
        public List<string>? Workflows { get; set; }
    }

    private static readonly string Dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cfg"));

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Bind_maps_camelCase_keys()
    {
        var section = new ProviderSection("tracker", Values(("type", "x"), ("owner", "acme"), ("project", "7")), Dir, ["type"]);
        var dto = section.Bind<Dto>();
        Assert.Equal("acme", dto.Owner);
        Assert.Equal(7, dto.Project);
    }

    [Fact]
    public void Bind_rejects_unknown_key()
    {
        var section = new ProviderSection("tracker", Values(("type", "x"), ("ownr", "acme")), Dir, ["type"]);
        var ex = Assert.Throws<ProjectConfigException>(() => section.Bind<Dto>());
        Assert.Equal("unknown field 'tracker.ownr'", ex.Message);
    }

    [Fact]
    public void Bind_ignores_shared_keys()
    {
        var section = new TrackerSection(Values(("type", "x"), ("tokenSecret", "t"), ("owner", "acme")), Dir);
        Assert.Equal("acme", section.Bind<Dto>().Owner);
        Assert.Equal("t", section.TokenSecret);
    }

    [Fact]
    public void Missing_type_is_reported()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new RepoSection(Values(("tokenSecret", "t"), ("localPath", "src/x")), Dir));
        Assert.Equal("missing required 'repo.type'", ex.Message);
    }

    [Fact]
    public void Repo_section_defaults_and_resolves_paths()
    {
        var section = new RepoSection(Values(("type", "x"), ("tokenSecret", "t"), ("localPath", "src/x")), Dir);
        Assert.Equal("main", section.MainBranch);
        Assert.Equal("production", section.ProductionBranch);
        Assert.Equal(Path.GetFullPath("src/x", Dir), section.LocalPath);
    }

    [Fact]
    public void Build_section_timeout_defaults_to_45()
    {
        Assert.Equal(45, new BuildSection(Values(("type", "x")), Dir).TimeoutMinutes);
        Assert.Equal(10, new BuildSection(Values(("type", "x"), ("timeoutMinutes", "10")), Dir).TimeoutMinutes);
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new BuildSection(Values(("type", "x"), ("timeoutMinutes", "soon")), Dir));
        Assert.Equal("invalid 'build.timeoutMinutes'", ex.Message);
    }

    [Fact]
    public void Bind_maps_lists()
    {
        var section = new ProviderSection("build", Values(("type", "x"),
            ("workflows", new List<object> { "ci.yml", "deploy.yml" })), Dir, ["type"]);
        Assert.Equal(["ci.yml", "deploy.yml"], section.Bind<Dto>().Workflows);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_branches_fall_back_to_defaults(string? value)
    {
        var section = new RepoSection(Values(("type", "x"), ("tokenSecret", "t"), ("localPath", "src/x"),
            ("mainBranch", value), ("productionBranch", value)), Dir);
        Assert.Equal("main", section.MainBranch);
        Assert.Equal("production", section.ProductionBranch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_timeout_defaults_to_45(string? value)
    {
        Assert.Equal(45, new BuildSection(Values(("type", "x"), ("timeoutMinutes", value)), Dir).TimeoutMinutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_token_secret_is_missing(string? value)
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new TrackerSection(Values(("type", "x"), ("tokenSecret", value)), Dir));
        Assert.Equal("missing required 'tracker.tokenSecret'", ex.Message);
    }

    [Fact]
    public void Non_scalar_shared_key_is_rejected()
    {
        var list = Assert.Throws<ProjectConfigException>(() =>
            new TrackerSection(Values(("type", new List<object> { "a" }), ("tokenSecret", "t")), Dir));
        Assert.Equal("invalid 'tracker.type'", list.Message);

        var map = Assert.Throws<ProjectConfigException>(() => new RepoSection(Values(("type", "x"), ("tokenSecret", "t"),
            ("localPath", "src/x"), ("mainBranch", new Dictionary<object, object> { ["a"] = "b" })), Dir));
        Assert.Equal("invalid 'repo.mainBranch'", map.Message);
    }
}
