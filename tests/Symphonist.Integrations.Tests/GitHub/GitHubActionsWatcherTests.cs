using System.Net;
using System.Text;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.GitHub;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.GitHub;

public class GitHubActionsWatcherTests
{
    private const string Sha = "9f2c4e1a7b8d0c3e5f6a1b2c3d4e5f6a7b8c9d0e";
    private const string RunsPrefix = "/repos/acme/orders/actions/runs?head_sha=" + Sha + "&per_page=100&page=";

    private static GitHubRepoConfig Repo() =>
        new("github", "/work/orders", "main", "production", "gh-token", "acme", "orders", "merge");

    private static string Run(string file, string status, string? conclusion, string created = "2026-10-24T10:00:00Z",
        string trigger = "push", int id = 1, string? path = null) =>
        $"{{\"id\":{id},\"event\":\"{trigger}\",\"path\":\"{path ?? ".github/workflows/" + file}\",\"status\":\"{status}\",\"conclusion\":" +
        (conclusion is null ? "null" : $"\"{conclusion}\"") + $",\"created_at\":\"{created}\"}}";

    private static string Page(params string[] runs) =>
        $"{{\"total_count\":{runs.Length},\"workflow_runs\":[{string.Join(',', runs)}]}}";

    private static Task<BuildStatus> Status(string[] build, string[] deploy, BuildPurpose purpose, params string[] runs)
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, RunsPrefix + "1", HttpStatusCode.OK, Page(runs));
        return Watcher(stub, build, deploy).GetStatusAsync(Sha, purpose, CancellationToken.None);
    }

    private static GitHubActionsWatcher Watcher(StubHttpHandler stub, string[] build, string[] deploy) =>
        new(new HttpClient(stub), Repo(), new GitHubActionsConfig("github-actions", 45, build, deploy), "ghp_secret");

    private static Task<BuildStatus> Any(params string[] runs) => Status([], [], BuildPurpose.Build, runs);

    [Fact]
    public async Task No_runs_is_NotFound() => Assert.Equal(BuildStatus.NotFound, await Any());

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("waiting")]
    public async Task Any_incomplete_run_is_Running(string status) =>
        Assert.Equal(BuildStatus.Running, await Any(Run("ci.yml", "completed", "success"), Run("b.yml", status, null)));

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("startup_failure")]
    public async Task Any_failed_conclusion_is_Failed(string conclusion) =>
        Assert.Equal(BuildStatus.Failed, await Any(Run("ci.yml", "completed", "success"), Run("b.yml", "completed", conclusion)));

    [Fact]
    public async Task Success_skipped_neutral_is_Succeeded() =>
        Assert.Equal(BuildStatus.Succeeded, await Any(Run("a.yml", "completed", "success"),
            Run("b.yml", "completed", "skipped"), Run("c.yml", "completed", "neutral")));

    [Fact]
    public async Task Latest_run_per_workflow_wins() =>
        Assert.Equal(BuildStatus.Succeeded, await Any(
            Run("ci.yml", "completed", "failure", "2026-10-24T10:00:00Z"),
            Run("ci.yml", "completed", "success", "2026-10-24T11:00:00Z")));

    [Fact]
    public async Task Listed_workflow_without_run_keeps_Running() =>
        Assert.Equal(BuildStatus.Running, await Status([], ["deploy.yml"], BuildPurpose.Deploy,
            Run("ci.yml", "completed", "success")));

    [Fact]
    public async Task Listed_workflow_without_any_run_is_NotFound() =>
        Assert.Equal(BuildStatus.NotFound, await Status([], ["deploy.yml"], BuildPurpose.Deploy));

    [Fact]
    public async Task Purpose_selects_list()
    {
        var runs = new[] { Run("ci.yml", "completed", "success"), Run("deploy.yml", "completed", "failure") };
        Assert.Equal(BuildStatus.Succeeded, await Status(["ci.yml"], ["deploy.yml"], BuildPurpose.Build, runs));
        Assert.Equal(BuildStatus.Failed, await Status(["ci.yml"], ["deploy.yml"], BuildPurpose.Deploy, runs));
    }

    [Fact]
    public async Task Unlisted_runs_are_ignored() =>
        Assert.Equal(BuildStatus.Succeeded, await Status(["ci.yml"], [], BuildPurpose.Build,
            Run("codeql.yml", "completed", "failure"), Run("ci.yml", "completed", "success")));

    [Fact]
    public async Task Listed_pair_with_one_not_started_is_Running() =>
        Assert.Equal(BuildStatus.Running, await Status(["ci.yml", "lint.yml"], [], BuildPurpose.Build,
            Run("ci.yml", "completed", "success")));

    [Theory]
    [InlineData("issue_comment")]
    [InlineData("issues")]
    [InlineData("schedule")]
    [InlineData("workflow_dispatch")]
    [InlineData("repository_dispatch")]
    [InlineData("discussion")]
    [InlineData("discussion_comment")]
    [InlineData("label")]
    [InlineData("milestone")]
    [InlineData("project")]
    [InlineData("project_card")]
    [InlineData("project_column")]
    [InlineData("watch")]
    [InlineData("fork")]
    [InlineData("gollum")]
    public async Task Runs_not_triggered_by_code_are_ignored(string trigger)
    {
        Assert.Equal(BuildStatus.Succeeded, await Any(Run("ci.yml", "completed", "success"),
            Run("bot.yml", "completed", "failure", trigger: trigger)));
        Assert.Equal(BuildStatus.Succeeded, await Any(Run("ci.yml", "completed", "success"),
            Run("nightly.yml", "in_progress", null, trigger: trigger)));
        Assert.Equal(BuildStatus.Succeeded, await Status(["ci.yml"], [], BuildPurpose.Build,
            Run("ci.yml", "completed", "success"), Run("ci.yml", "completed", "failure", "2026-10-24T11:00:00Z", trigger)));
    }

    [Fact]
    public async Task Pull_request_and_push_runs_count() =>
        Assert.Equal(BuildStatus.Failed, await Any(Run("ci.yml", "completed", "success"),
            Run("pr.yml", "completed", "failure", trigger: "pull_request")));

    [Fact]
    public async Task Workflow_ref_suffix_is_stripped_from_the_path() =>
        Assert.Equal(BuildStatus.Succeeded, await Status(["ci.yml"], [], BuildPurpose.Build,
            Run("", "completed", "success", path: ".github/workflows/ci.yml@refs/heads/main")));

    [Fact]
    public async Task Runs_without_a_path_are_kept_apart() =>
        Assert.Equal(BuildStatus.Failed, await Any(
            Run("", "completed", "success", id: 1, path: ""), Run("", "completed", "failure", id: 2, path: "")));

    [Fact]
    public async Task Repository_names_are_escaped_in_the_request()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get,
            "/repos/a%2Fb/or%20ders/actions/runs?head_sha=" + Sha + "&per_page=100&page=1", HttpStatusCode.OK, Page());
        var repo = new GitHubRepoConfig("github", "/work/orders", "main", "production", "gh-token", "a/b", "or ders", "merge");
        var watcher = new GitHubActionsWatcher(new HttpClient(stub), repo, new GitHubActionsConfig("github-actions", 45, [], []), "t");

        Assert.Equal(BuildStatus.NotFound, await watcher.GetStatusAsync(Sha, BuildPurpose.Build, CancellationToken.None));
    }

    [Fact]
    public async Task Runs_read_all_pages()
    {
        var first = Enumerable.Range(0, 100).Select(i => Run($"w{i}.yml", "completed", "success")).ToArray();
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, RunsPrefix + "1", HttpStatusCode.OK, Page(first))
            .On(HttpMethod.Get, RunsPrefix + "2", HttpStatusCode.OK, Page(Run("late.yml", "completed", "failure")));

        var status = await Watcher(stub, [], []).GetStatusAsync(Sha, BuildPurpose.Build, CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, status);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public void Provider_parses_workflow_lists_and_rejects_blank_items()
    {
        var provider = new GitHubActionsProvider();
        var ok = (GitHubActionsConfig)provider.Parse(new BuildSection(new Dictionary<string, object?>
        {
            ["type"] = "github-actions",
            ["buildWorkflows"] = new List<object> { "ci.yml" },
        }, "/cfg"));
        Assert.Equal(["ci.yml"], ok.BuildWorkflows);
        Assert.Empty(ok.DeployWorkflows);

        var ex = Assert.Throws<ProjectConfigException>(() => provider.Parse(new BuildSection(new Dictionary<string, object?>
        {
            ["type"] = "github-actions",
            ["deployWorkflows"] = new List<object?> { "" },
        }, "/cfg")));
        Assert.Equal("build.deployWorkflows contains an empty item", ex.Message);
    }
}
