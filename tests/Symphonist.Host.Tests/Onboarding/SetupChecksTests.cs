using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;

namespace Symphonist.Host.Tests.Onboarding;

[Collection(SymphonistHomeCollection.Name)]
public sealed class SetupChecksTests : IDisposable
{
    private static readonly ProviderRegistry Providers = BuiltInProviders.Registry;
    private static readonly string[] StepNames =
        ["Secrets", "Tracker sign-in", "Tracker statuses", "Tracker query", "Repository", "Production branch", "Build", "Local checkout"];

    private readonly TestHome home = new();
    private readonly FakeProjectDiscovery discovery = new();
    private readonly FakeSecretStore secrets = new();
    private readonly FakeGitWorkspace git = new() { OriginUrl = "git@example.org:acme/orders.git" };
    private readonly SetupChecks checks;
    private readonly List<string> adopted = [];

    public SetupChecksTests()
    {
        checks = new SetupChecks(discovery, Providers, secrets, git);
        discovery.Repository = new RepositoryDiscovery("acme/orders", "main", true);
    }

    public void Dispose() => home.Dispose();

    /// <summary>A draft as the setup session builds it: repository acme/orders, container isolation.</summary>
    private ProjectConfig Draft(string tracker = "jira", string repo = "bitbucket")
    {
        var form = ProjectFileStoreTests.Form("orders", tracker, repo);
        var keys = Providers.Repo(repo).FullNameKeys;
        form.Repo.Values[keys[0]] = "acme";
        form.Repo.Values[keys[1]] = "orders";
        return ProjectConfigLoader.Parse(ProjectYamlWriter.NewFile(form, Providers), home.Projects, "setup draft", Providers);
    }

    private void SetAllSecrets(ProjectConfig draft)
    {
        secrets.Set(draft.Tracker.TokenSecret, "t");
        secrets.Set(draft.Repo.TokenSecret, "r");
        secrets.Set("claude-code-token", "c");
    }

    private Task<ConnectionReport> Check(ProjectConfig draft) => checks.CheckAsync(draft, adopted.Add, CancellationToken.None);

    [Fact]
    public async Task Missing_secrets_skip_remote_checks()
    {
        var report = await Check(Draft());

        Assert.Equal(StepNames, report.Steps.Select(s => s.Name));
        Assert.Equal(new CheckStep("Secrets", CheckStatus.Failed,
            "not set: jira-orders, bitbucket-orders, claude-code-token; enter them above"), report.Steps[0]);
        Assert.All(report.Steps.Skip(1).Take(6), s =>
        {
            Assert.Equal(CheckStatus.Skipped, s.Status);
            Assert.Equal("needs secrets", s.Detail);
        });
        Assert.Equal(CheckStatus.Passed, report.Steps[7].Status);
        Assert.Equal(0, discovery.Calls);
        Assert.Null(report.Tracker);
        Assert.Null(report.Repository);
        Assert.Null(report.Build);
    }

    [Fact]
    public async Task Shared_github_secret_is_checked_once()
    {
        secrets.Set("claude-code-token", "c");

        var report = await Check(Draft("github-projects", "github"));

        Assert.Equal(new CheckStep("Secrets", CheckStatus.Failed, "not set: github-orders; enter them above"), report.Steps[0]);
        Assert.Equal(0, discovery.Calls);
    }

    [Theory]
    [InlineData("jira", "bitbucket")]
    [InlineData("github-projects", "github")]
    public async Task All_passing_steps_report_discovery(string tracker, string repo)
    {
        var draft = Draft(tracker, repo);
        SetAllSecrets(draft);
        var t = Providers.Tracker(tracker).DisplayName;
        var r = Providers.Repo(repo).DisplayName;
        var b = Providers.Build(draft.Build.Type).DisplayName;

        var report = await Check(draft);

        Assert.Equal(StepNames, report.Steps.Select(s => s.Name));
        Assert.All(report.Steps, s => Assert.Equal(CheckStatus.Passed, s.Status));
        Assert.Equal($"{t}: signed in as Dev", report.Steps[1].Detail);
        Assert.Equal("5 statuses from Jira project ACME", report.Steps[2].Detail);
        Assert.Equal("3 tickets match", report.Steps[3].Detail);
        Assert.Equal($"{r}: acme/orders, main branch main", report.Steps[4].Detail);
        Assert.Equal($"branch 'production' found on {r}", report.Steps[5].Detail);
        Assert.Equal($"{b}: latest pipeline #1 (COMPLETED)", report.Steps[6].Detail);
        Assert.Equal("origin is git@example.org:acme/orders.git", report.Steps[7].Detail);
        Assert.Same(discovery.Tracker, report.Tracker);
        Assert.Same(discovery.Repository, report.Repository);
        Assert.Same(discovery.Build, report.Build);
        Assert.Empty(adopted);
    }

    [Fact]
    public async Task No_status_source_skips_statuses()
    {
        var draft = Draft();
        SetAllSecrets(draft);
        discovery.Tracker = new TrackerDiscovery("Dev", null, [], null, ["set tracker.projectKey to read the project's statuses"]);

        var report = await Check(draft);

        Assert.Equal(new CheckStep("Tracker statuses", CheckStatus.Skipped,
            "set tracker.projectKey to read the project's statuses"), report.Steps[2]);
        Assert.Equal(new CheckStep("Tracker query", CheckStatus.Passed, "count unavailable"), report.Steps[3]);
    }

    [Fact]
    public async Task Tracker_failure_skips_statuses_and_query()
    {
        var draft = Draft("github-projects", "github");
        SetAllSecrets(draft);
        discovery.TrackerError = new InvalidOperationException("board acme #7 has no single-select field 'Status'");

        var report = await Check(draft);

        Assert.Equal(new CheckStep("Tracker sign-in", CheckStatus.Failed,
            "GitHub: board acme #7 has no single-select field 'Status'"), report.Steps[1]);
        Assert.Equal(new CheckStep("Tracker statuses", CheckStatus.Skipped, "needs tracker sign-in"), report.Steps[2]);
        Assert.Equal(new CheckStep("Tracker query", CheckStatus.Skipped, "needs tracker sign-in"), report.Steps[3]);
        Assert.Null(report.Tracker);
        Assert.Equal(CheckStatus.Passed, report.Steps[4].Status);
    }

    [Fact]
    public async Task Repository_and_build_failures_are_bounded_lines()
    {
        var draft = Draft();
        SetAllSecrets(draft);
        discovery.RepositoryError = new HttpRequestException("404 Not Found\nsecond line");
        discovery.BuildError = new HttpRequestException(new string('x', 2000));

        var report = await Check(draft);

        Assert.Equal(new CheckStep("Repository", CheckStatus.Failed, "Bitbucket: 404 Not Found second line"), report.Steps[4]);
        Assert.Equal(new CheckStep("Production branch", CheckStatus.Skipped, "needs the repository"), report.Steps[5]);
        Assert.Equal(CheckStatus.Failed, report.Steps[6].Status);
        Assert.StartsWith("Bitbucket Pipelines: xxx", report.Steps[6].Detail);
        Assert.True(report.Steps[6].Detail.Length <= 500);
    }

    [Fact]
    public async Task Case_only_repo_name_difference_adopts_host_spelling()
    {
        var draft = Draft();
        SetAllSecrets(draft);
        discovery.Repository = new RepositoryDiscovery("Acme/Orders", "main", true);

        var report = await Check(draft);

        Assert.Equal(new CheckStep("Repository", CheckStatus.Passed,
            "Bitbucket: Acme/Orders, main branch main; the form now uses Bitbucket's spelling"), report.Steps[4]);
        Assert.Equal(["Acme/Orders"], adopted);
    }

    [Fact]
    public async Task Other_repository_fails_and_main_branch_difference_is_noted()
    {
        var draft = Draft();
        SetAllSecrets(draft);
        discovery.Repository = new RepositoryDiscovery("acme/orders", "develop", false);

        var branch = await Check(draft);

        Assert.Equal(new CheckStep("Repository", CheckStatus.Passed,
            "Bitbucket: acme/orders, main branch main; Bitbucket's main branch is develop"), branch.Steps[4]);
        Assert.Equal(new CheckStep("Production branch", CheckStatus.Failed, "branch 'production' not found on Bitbucket"),
            branch.Steps[5]);

        discovery.Repository = new RepositoryDiscovery("acme/billing", "main", true);
        var other = await Check(draft);

        Assert.Equal(new CheckStep("Repository", CheckStatus.Failed, "Bitbucket names it acme/billing, expected acme/orders"),
            other.Steps[4]);
        Assert.Empty(adopted);
    }

    [Fact]
    public async Task Build_notes_are_shown_without_failing()
    {
        var draft = Draft("github-projects", "github");
        SetAllSecrets(draft);
        discovery.Build = new BuildDiscovery("3 workflows", ["ci.yml"], ["workflow 'nope.yml' not found in .github/workflows"]);

        var report = await Check(draft);

        Assert.Equal(new CheckStep("Build", CheckStatus.Passed,
            "GitHub Actions: 3 workflows; workflow 'nope.yml' not found in .github/workflows"), report.Steps[6]);
    }

    [Fact]
    public async Task Wrong_origin_fails_local_checkout()
    {
        var draft = Draft();
        SetAllSecrets(draft);
        git.OriginUrl = "https://bitbucket.org/acme/billing.git";

        var wrong = await Check(draft);

        Assert.Equal(new CheckStep("Local checkout", CheckStatus.Failed,
            "origin is https://bitbucket.org/acme/billing.git, expected acme/orders"), wrong.Steps[7]);

        git.OriginUrl = "https://bitbucket.org/acme/orders-old";
        Assert.Equal(CheckStatus.Failed, (await Check(draft)).Steps[7].Status);

        git.OriginUrl = "https://bitbucket.org/ACME/Orders.git/";
        Assert.Equal(CheckStatus.Passed, (await Check(draft)).Steps[7].Status);

        git.OriginUrl = null;
        Assert.Equal(new CheckStep("Local checkout", CheckStatus.Failed,
            $"not a git checkout with an origin: {draft.Repo.LocalPath}"), (await Check(draft)).Steps[7]);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var draft = Draft();
        SetAllSecrets(draft);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            checks.CheckAsync(draft, null, new CancellationToken(canceled: true)));
    }
}
