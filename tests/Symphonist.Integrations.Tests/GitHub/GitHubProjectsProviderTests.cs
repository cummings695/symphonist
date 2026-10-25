using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.GitHub;

namespace Symphonist.Tests.GitHub;

public class GitHubProjectsProviderTests
{
    /// <summary>The stages of <c>tests/Symphonist.Core.Tests/Config/orders.yaml</c>.</summary>
    internal static IReadOnlyList<StageDefinition> OrdersPipeline(string inProgress = "In Progress") =>
    [
        new("triage", StageKind.Triage, GatePolicy.Human, ["Backlog"], null, null, "Planned"),
        new("plan", StageKind.Plan, GatePolicy.Human, ["Planned"], "Planned", null, inProgress),
        new("implement", StageKind.Implement, GatePolicy.Human, [], inProgress, "In Review", null),
        new("build", StageKind.Build, GatePolicy.None, [], null, null, null),
        new("release", StageKind.Release, GatePolicy.Human, [], null, null, null),
        new("deploy", StageKind.Deploy, GatePolicy.None, [], null, null, null),
        new("done", StageKind.Done, GatePolicy.None, [], "Done", null, null),
    ];

    internal static GitHubProjectsTrackerConfig TrackerConfig(IReadOnlyList<string>? statuses = null,
        IReadOnlyList<string>? repositories = null) =>
        new("github-projects", "github-acme", "acme", 7, "Claude", "Status", statuses, repositories);

    internal static GitHubRepoConfig GitHubRepo() =>
        new("github", "/work/orders", "main", "production", "github-acme", "acme", "orders", "merge");

    internal static ProjectConfig Project(TrackerConfig? tracker = null, RepoConfig? repo = null,
        IReadOnlyList<StageDefinition>? pipeline = null) =>
        new("orders", "Orders", tracker ?? TrackerConfig(), repo ?? GitHubRepo(), null!, null!, 3,
            pipeline ?? OrdersPipeline(), new Dictionary<StageKind, string>(), "/cfg");

    private static TrackerSection Section(params (string Key, object? Value)[] extra)
    {
        var values = new Dictionary<string, object?>
        {
            ["type"] = "github-projects", ["tokenSecret"] = "github-acme",
            ["owner"] = "acme", ["project"] = "7", ["label"] = "Claude",
        };
        foreach (var (key, value) in extra) values[key] = value;
        return new TrackerSection(values, Path.GetFullPath("/cfg"));
    }

    [Fact]
    public void Pickup_defaults_to_pipeline_statuses_without_done() =>
        Assert.Equal(["Backlog", "Planned", "In Progress", "In Review"],
            GitHubProjectsProvider.PickupStatuses(Project()));

    [Fact]
    public void Pickup_uses_configured_statuses() =>
        Assert.Equal(["Ready", "Doing"],
            GitHubProjectsProvider.PickupStatuses(Project(TrackerConfig(statuses: ["Ready", "Doing"]))));

    [Fact]
    public void Required_includes_done() =>
        Assert.Equal(["Backlog", "Planned", "In Progress", "In Review", "Done"],
            GitHubProjectsProvider.RequiredStatuses(Project()));

    [Fact]
    public void Repositories_default_to_github_repo() =>
        Assert.Equal(["acme/orders"], GitHubProjectsProvider.Repositories(Project()));

    [Fact]
    public void Repositories_use_configured_list() =>
        Assert.Equal(["acme/orders", "acme/web"],
            GitHubProjectsProvider.Repositories(Project(TrackerConfig(repositories: ["acme/orders", "acme/web"]))));

    [Fact]
    public void Repositories_null_for_bitbucket_repo() =>
        Assert.Null(GitHubProjectsProvider.Repositories(Project(repo:
            new BitbucketRepoConfig("bitbucket", "/work/orders", "main", "production", "t", "acme", "orders",
                "dev@example.com", "squash"))));

    [Fact]
    public void Parses_required_fields_and_defaults()
    {
        var provider = new GitHubProjectsProvider();
        var config = Assert.IsType<GitHubProjectsTrackerConfig>(provider.Parse(Section()));

        Assert.Equal(new GitHubProjectsTrackerConfig("github-projects", "github-acme", "acme", 7, "Claude", "Status",
            null, null), config);
        Assert.Equal("github-projects", provider.Type);
        Assert.Equal("GitHub", provider.DisplayName);
        Assert.Equal("github", provider.HttpClientName);
    }

    [Fact]
    public void Parses_optional_fields()
    {
        var config = (GitHubProjectsTrackerConfig)new GitHubProjectsProvider().Parse(Section(
            ("assignee", "me"), ("statusField", "Stage"),
            ("statuses", new List<object> { "Ready", "Doing" }), ("repositories", new List<object> { "acme/orders" })));

        Assert.Equal("Stage", config.StatusField);
        Assert.Equal(["Ready", "Doing"], config.Statuses!);
        Assert.Equal(["acme/orders"], config.Repositories!);
    }

    [Fact]
    public void Rejects_assignee_other_than_me()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new GitHubProjectsProvider().Parse(Section(("assignee", "octocat"))));

        Assert.Equal("unsupported tracker.assignee 'octocat'", ex.Message);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("project")]
    [InlineData("label")]
    public void Requires_owner_project_and_label(string missing)
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new GitHubProjectsProvider().Parse(Section((missing, null))));

        Assert.Equal($"missing required 'tracker.{missing}'", ex.Message);
    }

    [Fact]
    public void Rejects_non_numeric_project()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new GitHubProjectsProvider().Parse(Section(("project", "seven"))));

        Assert.Equal("invalid 'tracker.project'", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_field()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            new GitHubProjectsProvider().Parse(Section(("jql", "x"))));

        Assert.Equal("unknown field 'tracker.jql'", ex.Message);
    }

    [Fact]
    public void Is_registered() =>
        Assert.IsType<GitHubProjectsProvider>(Symphonist.Integrations.BuiltInProviders.Registry.Tracker("github-projects"));
}
