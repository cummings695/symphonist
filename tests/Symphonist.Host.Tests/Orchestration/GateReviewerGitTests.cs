using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations.Git;
using Symphonist.Integrations.Processes;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Host.Tests.Orchestration;

/// <summary>GateReviewer against real git repos: what the patch can hide, and which commits a verdict covers.</summary>
[Collection(SymphonistHomeCollection.Name)]
public sealed class GateReviewerGitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "symph-review-" + Guid.NewGuid().ToString("N"));
    private readonly string home;
    private readonly string dev;
    private readonly FakeAgentRunner agent = new();
    private readonly GitWorkspace git;
    private readonly GateReviewer reviewer;
    private readonly ProjectConfig project;

    public GateReviewerGitTests()
    {
        foreach (var (k, v) in new[] { ("NAME", "Test"), ("EMAIL", "test@example.com") })
        {
            Environment.SetEnvironmentVariable("GIT_AUTHOR_" + k, v);
            Environment.SetEnvironmentVariable("GIT_COMMITTER_" + k, v);
        }
        home = Path.Combine(root, "home");
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", home);
        var bare = Path.Combine(root, "remote.git");
        dev = Path.Combine(root, "dev");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", "-b", "main", bare);
        Run(root, "clone", bare, dev);
        Run(dev, "checkout", "-B", "main");
        Write(dev, "README.md", "hello");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "init");
        Run(dev, "push", "-u", "origin", "main");

        git = new GitWorkspace(new ProcessRunner(), Path.Combine(root, "work"));
        reviewer = new GateReviewer(git, new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = agent }),
            new FakeTicketStore(), new FakeTimeProvider(DateTimeOffset.Parse("2026-10-24T10:00:00Z")));
        StageDefinition S(string id, StageKind kind) => new(id, kind, GatePolicy.Human, [], null, null, null);
        project = new ProjectConfig("orders", "Orders",
            new JiraTrackerConfig("jira", "jira-token", new Uri("https://jira.example"), "me@example.com", "jql"),
            new BitbucketRepoConfig("bitbucket", dev, "main", "main", "bb-token", "w", "orders", "me@example.com", "squash"),
            new BitbucketPipelinesConfig("bitbucket-pipelines", 45),
            new AgentSettings("claude-code", Isolation.Container, [], [], 30, null, null),
            3, [S("implement", StageKind.Implement)], new Dictionary<StageKind, string>(), root)
        {
            RiskAreas = [new RiskArea("pricing", "Prices", ["src/Pricing/**"])],
            Review = new ReviewConfig(ReviewMode.Shadow,
                new AgentSettings("claude-code", Isolation.Host, [], ReviewConfig.ReadOnlyTools, 20, null, null), null),
        };
        agent.OnRun = _ => FakeAgentRunner.Review("low");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best effort */ }
    }

    private static string Run(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {e}");
        return o.Trim();
    }

    private static void Write(string ws, string relative, string text)
    {
        var path = Path.Combine([ws, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void WriteBytes(string ws, string relative, byte[] bytes)
    {
        var path = Path.Combine([ws, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static Ticket Ticket() => new()
    {
        Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", Title = "Add export", StageId = "implement",
        Status = StageStatus.AwaitingApproval, Branch = "symphonist/ORD-1", WaitingSince = DateTimeOffset.Parse("2026-10-24T09:00:00Z"),
    };

    /// <summary>Prepares the ticket's workspace, lets <paramref name="change"/> edit it, then commits and pushes.</summary>
    private async Task<string> CommitAndPushAsync(Action<string> change)
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        change(ws);
        Run(ws, "add", "-A");
        Run(ws, "commit", "-m", "change");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");
        return ws;
    }

    private Task<GateReviewOutcome> ReviewAsync() => reviewer.ReviewAsync(project, Ticket(), null, default);

    [Fact]
    public async Task Plain_change_is_low_and_pinned_to_the_pushed_head()
    {
        var ws = await CommitAndPushAsync(ws => Write(ws, "src/Export/Csv.cs", "class Csv {}"));

        var outcome = await ReviewAsync();

        Assert.Equal(RiskLevel.Low, outcome.Verdict.Risk);
        Assert.Equal(Run(ws, "rev-parse", "origin/symphonist/ORD-1"), outcome.ReviewedHead);
        Assert.Contains("+class Csv {}", Assert.Single(agent.Requests).Prompt);
    }

    [Fact]
    public async Task Local_commits_that_were_not_pushed_are_not_what_is_reviewed()
    {
        var ws = await CommitAndPushAsync(ws => Write(ws, "src/Export/Csv.cs", "class Csv {}"));
        var pushed = Run(ws, "rev-parse", "HEAD");
        Write(ws, "src/Export/Local.cs", "class LocalOnly {}");
        Run(ws, "add", "-A");
        Run(ws, "commit", "-m", "local only");

        var outcome = await ReviewAsync();

        Assert.Equal(pushed, outcome.ReviewedHead);
        Assert.DoesNotContain("LocalOnly", Assert.Single(agent.Requests).Prompt);
    }

    [Fact]
    public async Task Unpushed_branch_is_high()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Write(ws, "src/Export/Csv.cs", "class Csv {}");
        Run(ws, "add", "-A");
        Run(ws, "commit", "-m", "not pushed");

        var outcome = await ReviewAsync();

        Assert.Equal(RiskLevel.High, outcome.Verdict.Risk);
        Assert.Equal("branch not pushed: origin/symphonist/ORD-1", outcome.Verdict.Error);
        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task Committed_gitattributes_hiding_source_is_high()
    {
        await CommitAndPushAsync(ws =>
        {
            Write(ws, ".gitattributes", "*.cs -diff\n");
            Write(ws, "src/Export/Csv.cs", "class Csv { /* hidden */ }");
        });

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("changes git attributes or submodules: .gitattributes", verdict.Reasons);
        Assert.Contains("patch hides the content of src/Export/Csv.cs (shown as binary)", verdict.Reasons);
    }

    [Fact]
    public async Task Workspace_only_attributes_hiding_source_are_high()
    {
        await CommitAndPushAsync(ws =>
        {
            Directory.CreateDirectory(Path.Combine(ws, ".git", "info"));
            File.WriteAllText(Path.Combine(ws, ".git", "info", "attributes"), "*.cs -diff\n");
            Write(ws, "src/Export/Csv.cs", "class Csv { /* hidden */ }");
        });

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("patch hides the content of src/Export/Csv.cs (shown as binary)", verdict.Reasons);
    }

    [Fact]
    public async Task Submodule_pointer_change_is_high()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Run(ws, "update-index", "--add", "--cacheinfo", "160000,1234567890abcdef1234567890abcdef12345678,vendor/lib");
        Run(ws, "commit", "-m", "add submodule pointer");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("changes a submodule pointer", verdict.Reasons);
    }

    [Fact]
    public async Task Submodule_pointer_change_is_high_whatever_diff_submodule_says()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Run(ws, "config", "diff.submodule", "log");
        Run(ws, "update-index", "--add", "--cacheinfo", "160000,1234567890abcdef1234567890abcdef12345678,vendor/lib");
        Run(ws, "commit", "-m", "add submodule pointer");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("changes a submodule pointer", verdict.Reasons);
        Assert.Contains("Subproject commit 1234567890abcdef1234567890abcdef12345678", Assert.Single(agent.Requests).Prompt);
    }

    /// <summary>Stages a symlink without OS symlink rights: a 120000 index entry whose blob is the target path.</summary>
    private static void StageSymlink(string ws, string path, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", ["hash-object", "-w", "--stdin"])
        {
            WorkingDirectory = ws, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(target);
        p.StandardInput.Close();
        var blob = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Run(ws, "update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
    }

    [Fact]
    public async Task Claude_directory_committed_as_a_symlink_is_high()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Write(ws, "tools/cfg/settings.json", "{\"hooks\":{}}");
        Run(ws, "add", "tools/cfg/settings.json");
        StageSymlink(ws, ".claude", "tools/cfg");
        Run(ws, "commit", "-m", "settings via symlink");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("changes agent configuration: .claude", verdict.Reasons);
        Assert.Contains("symlink added or changed: .claude", verdict.Reasons);
    }

    [Fact]
    public async Task Symlink_anywhere_is_high()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        StageSymlink(ws, "docs/link", "README.md");
        Run(ws, "commit", "-m", "add link");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("symlink added or changed: docs/link", verdict.Reasons);
    }

    [Fact]
    public async Task Symlink_whose_target_changes_is_high()
    {
        StageSymlink(dev, "docs/link", "README.md");
        Run(dev, "commit", "-m", "link on main");
        Run(dev, "push", "origin", "main");
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        StageSymlink(ws, "docs/link", "src/Unguarded.md");
        Run(ws, "commit", "-m", "retarget link");
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("symlink added or changed: docs/link", verdict.Reasons);
    }

    [Fact]
    public async Task A_tag_named_like_the_remote_branch_does_not_move_the_base()
    {
        var older = Run(dev, "rev-parse", "HEAD");
        Write(dev, "src/Main/FromMain.cs", "class FromMainB {}");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "main moves on");
        Run(dev, "push", "origin", "main");
        var mainHead = Run(dev, "rev-parse", "HEAD");
        Run(dev, "tag", "origin/main", older);
        Run(dev, "push", "origin", "refs/tags/origin/main");
        var ws = await CommitAndPushAsync(ws => Write(ws, "src/Export/Csv.cs", "class Csv {}"));
        Assert.Equal(older, Run(ws, "rev-parse", "refs/tags/origin/main")); // the shadowing tag reached the workspace

        var outcome = await ReviewAsync();

        var prompt = Assert.Single(agent.Requests).Prompt;
        Assert.Contains($"{mainHead}...{outcome.ReviewedHead}", prompt);
        Assert.DoesNotContain("FromMainB", prompt);
        Assert.Equal(RiskLevel.Low, outcome.Verdict.Risk);
    }

    [Fact]
    public async Task A_deleted_remote_branch_no_longer_resolves()
    {
        await CommitAndPushAsync(ws => Write(ws, "src/Export/Csv.cs", "class Csv {}"));
        Assert.Equal(RiskLevel.Low, (await ReviewAsync()).Verdict.Risk);
        Run(dev, "push", "origin", "--delete", "symphonist/ORD-1");

        var outcome = await ReviewAsync();

        Assert.Equal(RiskLevel.High, outcome.Verdict.Risk);
        Assert.Equal("branch not pushed: origin/symphonist/ORD-1", outcome.Verdict.Error);
    }

    [Fact]
    public async Task Png_change_outside_risk_areas_stays_low()
    {
        await CommitAndPushAsync(ws => WriteBytes(ws, "docs/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3]));

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.Low, verdict.Risk);
        Assert.Contains("Binary files /dev/null and b/docs/logo.png differ", Assert.Single(agent.Requests).Prompt);
    }

    [Fact]
    public async Task Implement_with_nothing_pushed_beyond_main_is_high()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Run(ws, "push", "-u", "origin", "symphonist/ORD-1");

        var verdict = (await ReviewAsync()).Verdict;

        Assert.Equal(RiskLevel.High, verdict.Risk);
        Assert.Contains("no changes to review", verdict.Reasons);
    }
}
