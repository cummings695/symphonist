using Microsoft.Extensions.Logging;
using Symphonist.Core.Config;
using Symphonist.Core.Risk;
using Symphonist.Integrations.Git;
using Symphonist.Integrations.Processes;
using Symphonist.Integrations.Bitbucket;

namespace Symphonist.Integrations.Tests.Git;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "symph-git-" + Guid.NewGuid().ToString("N"));
    private readonly string bare;
    private readonly string dev;
    private readonly string workRoot;
    private readonly GitWorkspace git;
    private readonly ProjectConfig project;

    public GitWorkspaceTests()
    {
        foreach (var (k, v) in new[] { ("NAME", "Test"), ("EMAIL", "test@example.com") })
        {
            Environment.SetEnvironmentVariable("GIT_AUTHOR_" + k, v);
            Environment.SetEnvironmentVariable("GIT_COMMITTER_" + k, v);
        }
        bare = Path.Combine(root, "remote.git");
        dev = Path.Combine(root, "dev checkout");
        workRoot = Path.Combine(root, "work");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", "-b", "main", bare);
        Run(root, "clone", bare, dev);
        Run(dev, "checkout", "-B", "main");
        File.WriteAllText(Path.Combine(dev, "README.md"), "hello");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "init");
        Run(dev, "push", "-u", "origin", "main");
        git = new GitWorkspace(new ProcessRunner(), workRoot);
        project = new ProjectConfig("proj", "Proj", null!,
            new BitbucketRepoConfig("bitbucket", dev, "main", "main", "t", "w", "s", "e", "squash"),
            null!, null!, 3, [], new Dictionary<StageKind, string>(), root);
    }

    public void Dispose()
    {
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

    private static string Git(string ws, string args) => Run(ws, args.Split(' '));

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('/', '\\').ToLowerInvariant();

    [Fact]
    public async Task Prepare_clones_and_branches_from_main_with_real_origin()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Assert.Equal(Path.Combine(workRoot, "proj", "ORD-1"), ws);
        Assert.Equal(Norm(bare), Norm(Git(ws, "remote get-url origin")));
        Assert.Equal("symphonist/ORD-1", Git(ws, "branch --show-current"));
        Assert.True(File.Exists(Path.Combine(ws, "README.md")));
    }

    [Fact]
    public async Task Origin_url_of_non_repo_is_null()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Null(await git.OriginUrlAsync(plain, default));
        Assert.Null(await git.OriginUrlAsync(Path.Combine(root, "missing"), default));

        var noOrigin = Path.Combine(root, "no origin");
        Run(root, "init", "-b", "main", noOrigin);
        Assert.Null(await git.OriginUrlAsync(noOrigin, default));

        Assert.Equal(Norm(bare), Norm((await git.OriginUrlAsync(dev, default))!));
        Run(dev, "remote", "set-url", "origin", CredentialUrl);
        Assert.Equal("https://bitbucket.org/acme/orders.git", await git.OriginUrlAsync(dev, default));
    }

    [Fact]
    public async Task Remove_deletes_workspace_and_ignores_missing()
    {
        var ws = await git.PrepareAsync(project, "_onboarding", "main", default);
        Assert.True(Directory.Exists(ws));

        await git.RemoveAsync(project, "_onboarding", default);

        Assert.False(Directory.Exists(ws));
        await git.RemoveAsync(project, "_onboarding", default);
        await git.RemoveAsync(project, "_container-check", default);
        await Assert.ThrowsAsync<ArgumentException>(() => git.RemoveAsync(project, "..", default));
        Assert.True(Directory.Exists(dev));
    }

    [Fact]
    public async Task Prepare_twice_reuses_workspace_and_keeps_changes()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        File.WriteAllText(Path.Combine(ws, "x.txt"), "keep");
        var ws2 = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Assert.Equal(ws, ws2);
        Assert.True(File.Exists(Path.Combine(ws, "x.txt")));
    }

    [Fact]
    public async Task Commit_returns_false_when_clean()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Assert.False(await git.CommitAllAsync(ws, "nothing", default));
    }

    [Fact]
    public async Task Has_commits_ahead_of_main_only_after_a_commit_even_once_pushed()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Assert.False(await git.HasCommitsAheadAsync(ws, "main", default));

        File.WriteAllText(Path.Combine(ws, "x.txt"), "new");
        await git.CommitAllAsync(ws, "add x", default);
        Assert.True(await git.HasCommitsAheadAsync(ws, "main", default));

        await git.PushAsync(ws, "symphonist/ORD-1", default);
        Assert.True(await git.HasCommitsAheadAsync(ws, "main", default));
    }

    [Fact]
    public async Task Commit_and_push_puts_branch_on_remote()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        File.WriteAllText(Path.Combine(ws, "x.txt"), "new");
        Assert.True(await git.CommitAllAsync(ws, "add x", default));
        await git.PushAsync(ws, "symphonist/ORD-1", default);
        Assert.NotEmpty(Git(bare, "branch --list symphonist/ORD-1"));
    }

    [Fact]
    public async Task Prepare_after_workspace_deleted_continues_from_pushed_branch()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        File.WriteAllText(Path.Combine(ws, "x.txt"), "pushed");
        await git.CommitAllAsync(ws, "add x", default);
        await git.PushAsync(ws, "symphonist/ORD-1", default);
        foreach (var f in Directory.EnumerateFiles(ws, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(ws, true);

        var ws2 = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Assert.Equal("pushed", File.ReadAllText(Path.Combine(ws2, "x.txt")));
        Assert.Equal("symphonist/ORD-1", Git(ws2, "branch --show-current"));
    }

    private static void WriteFile(string ws, string relative, string text)
    {
        var path = Path.Combine([ws, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Changed_files_lists_branch_changes()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        WriteFile(ws, "a/b.cs", "class B {}");
        await git.CommitAllAsync(ws, "add b", default);

        Assert.Equal(["a/b.cs"], await git.ChangedFilesAsync(ws, "origin/main...HEAD", default));
    }

    [Fact]
    public async Task Changed_files_keep_non_ascii_and_spaced_paths_exact_and_force_risk_area_match()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        WriteFile(ws, "src/Pricing/Prix é.cs", "class Prix {}");
        WriteFile(ws, "src/Pricing/税.cs", "class Tax {}");
        await git.CommitAllAsync(ws, "add pricing", default);

        var files = await git.ChangedFilesAsync(ws, "origin/main...HEAD", default);

        Assert.Equal(["src/Pricing/Prix é.cs", "src/Pricing/税.cs"], files.Order(StringComparer.Ordinal));
        var pricing = new RiskArea("pricing", "prices", ["src/Pricing/**"]);
        Assert.Equal(2, RiskAreaMatcher.Touched([pricing], files).Count);
    }

    [Fact]
    public async Task A_rename_out_of_a_risk_area_lists_the_old_path_too()
    {
        WriteFile(dev, "src/Pricing/Calc.cs", "class Calc { /* long enough to be detected as a rename */ }");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "pricing");
        Run(dev, "push", "origin", "main");
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        Run(ws, "mv", "src/Pricing/Calc.cs", "src/Calc.cs");
        await git.CommitAllAsync(ws, "move calc out", default);

        var files = await git.ChangedFilesAsync(ws, "origin/main...HEAD", default);

        Assert.Equal(["src/Calc.cs", "src/Pricing/Calc.cs"], files.Order(StringComparer.Ordinal));
        Assert.Contains(("pricing", "src/Pricing/Calc.cs"),
            RiskAreaMatcher.Touched([new RiskArea("pricing", "prices", ["src/Pricing"])], files));
    }

    [Fact]
    public async Task Diff_returns_the_patch_of_the_range()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        WriteFile(ws, "a/b.cs", "class B { int secretSauce; }");
        await git.CommitAllAsync(ws, "add b", default);

        var patch = await git.DiffAsync(ws, "origin/main...HEAD", default);

        Assert.Contains("+++ b/a/b.cs", patch);
        Assert.Contains("+class B { int secretSauce; }", patch);
    }

    [Fact]
    public async Task Resolve_commit_returns_the_sha_or_null_when_missing()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);

        Assert.Equal(Git(ws, "rev-parse origin/main"), await git.ResolveCommitAsync(ws, "origin/main", default));
        Assert.Null(await git.ResolveCommitAsync(ws, "origin/symphonist/ORD-1", default));
        await Assert.ThrowsAsync<GitCommandException>(() => git.ResolveCommitAsync(root, "origin/main", default));
    }

    [Fact]
    public async Task A_tag_named_like_a_remote_branch_is_not_that_branch()
    {
        // A tag "refs/remotes/origin/feat" is refs/tags/refs/remotes/origin/feat, which rev-parse would fall back to.
        Run(dev, "checkout", "-b", "side");
        File.WriteAllText(Path.Combine(dev, "evil.txt"), "planted");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "planted");
        var planted = Run(dev, "rev-parse", "HEAD");
        Run(dev, "tag", "refs/remotes/origin/feat", planted);
        Run(dev, "checkout", "main");

        var ws = await git.PrepareAsync(project, "ORD-9", "feat", default);

        Assert.Equal(planted, Git(ws, "rev-parse refs/tags/refs/remotes/origin/feat")); // the tag reached the workspace
        Assert.False(File.Exists(Path.Combine(ws, "evil.txt"))); // the branch starts from main, not from the tag
        Assert.Equal(Git(ws, "rev-parse refs/remotes/origin/main"), Git(ws, "rev-parse HEAD"));
        Assert.Null(await git.ResolveCommitAsync(ws, "refs/remotes/origin/feat", default));
        Assert.Equal(Git(ws, "rev-parse refs/remotes/origin/main"),
            await git.ResolveCommitAsync(ws, "refs/remotes/origin/main", default));
    }

    [Fact]
    public async Task Changed_files_and_diff_fail_loudly_on_a_bad_range()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        await Assert.ThrowsAsync<GitCommandException>(() => git.ChangedFilesAsync(ws, "origin/nope...HEAD", default));
        await Assert.ThrowsAsync<GitCommandException>(() => git.DiffAsync(ws, "origin/nope...HEAD", default));
    }

    [Fact]
    public async Task Diff_never_runs_an_external_diff_or_textconv_configured_in_the_workspace()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        var marker = Path.Combine(root, "diff-driver-ran");
        var script = Path.Combine(ws, ".git", "evil-diff");
        WriteHook(script, $"touch '{ShPath(marker)}'\nexit 0");
        Run(ws, "config", "diff.external", ShPath(script));
        Run(ws, "config", "diff.evil.textconv", ShPath(script));
        Directory.CreateDirectory(Path.Combine(ws, ".git", "info"));
        File.WriteAllText(Path.Combine(ws, ".git", "info", "attributes"), "* diff=evil\n");
        WriteFile(ws, "a/b.cs", "class B {}");
        await git.CommitAllAsync(ws, "add b", default);

        await git.ChangedFilesAsync(ws, "origin/main...HEAD", default);
        await git.DiffAsync(ws, "origin/main...HEAD", default);

        Assert.False(File.Exists(marker), "a diff driver from the workspace's .git ran on the host");
    }

    [Fact]
    public async Task Failing_git_command_throws_with_stderr()
    {
        var ex = await Assert.ThrowsAsync<GitCommandException>(() => git.PushAsync(dev, "no-such-branch", default));
        Assert.Contains("does not match any", ex.Message);
    }

    private static void WriteHook(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ShPath(string p) => p.Replace('\\', '/');

    [Fact]
    public async Task Hooks_planted_in_the_workspace_never_run_on_the_host()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        var marker = Path.Combine(root, "hook-ran");
        foreach (var hook in new[] { "pre-commit", "commit-msg", "post-commit", "pre-push" })
            WriteHook(Path.Combine(ws, ".git", "hooks", hook), $"touch '{ShPath(marker)}'\nexit 1");
        File.WriteAllText(Path.Combine(ws, "x.txt"), "new");

        Assert.True(await git.CommitAllAsync(ws, "add x", default));
        await git.PushAsync(ws, "symphonist/ORD-1", default);
        Assert.False(File.Exists(marker), "a hook from the workspace's .git ran on the host");
    }

    [Fact]
    public async Task Fsmonitor_configured_in_the_workspace_never_runs_on_the_host()
    {
        var ws = await git.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        var marker = Path.Combine(root, "fsmonitor-ran");
        var script = Path.Combine(ws, ".git", "evil-fsmonitor");
        WriteHook(script, $"touch '{ShPath(marker)}'\nexit 1");
        Run(ws, "config", "core.fsmonitor", ShPath(script));
        File.WriteAllText(Path.Combine(ws, "x.txt"), "new");

        Assert.True(await git.CommitAllAsync(ws, "add x", default));
        Assert.False(File.Exists(marker), "core.fsmonitor from the workspace's .git ran on the host");
    }

    private sealed class FailFetchOnce(IProcessRunner inner) : IProcessRunner
    {
        public bool Failed;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            if (!Failed && spec.Arguments.Contains("fetch"))
            {
                Failed = true;
                return Task.FromResult(new ProcessResult(128, "", "simulated network failure", false));
            }
            return inner.RunAsync(spec, ct);
        }
    }

    [Fact]
    public async Task Prepare_failing_midway_leaves_no_workspace_and_retry_succeeds()
    {
        var flaky = new FailFetchOnce(new ProcessRunner());
        var g = new GitWorkspace(flaky, workRoot);
        var ex = await Assert.ThrowsAsync<GitCommandException>(() => g.PrepareAsync(project, "ORD-2", "symphonist/ORD-2", default));
        Assert.Contains("simulated network failure", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(workRoot, "proj", "ORD-2")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(workRoot, "proj")));

        var ws = await g.PrepareAsync(project, "ORD-2", "symphonist/ORD-2", default);
        Assert.Equal("symphonist/ORD-2", Git(ws, "branch --show-current"));
        Assert.Equal(Norm(bare), Norm(Git(ws, "remote get-url origin")));
    }
    private const string CredentialUrl = "https://dev:s3cret@bitbucket.org/acme/orders.git";

    /// <summary>Pretends the developer's origin URL embeds credentials; records what the workspace origin is set to.</summary>
    private sealed class CredentialOrigin(IProcessRunner inner, string realRemote, string? failFetchWith = null) : IProcessRunner
    {
        public List<string> SetUrls { get; } = [];
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            var args = spec.Arguments.ToList();
            if (args.Contains("get-url")) return Task.FromResult(new ProcessResult(0, CredentialUrl + "\n", "", false));
            if (args.Contains("set-url"))
            {
                SetUrls.Add(args[^1]);
                args[^1] = realRemote; // keep the test offline: point the workspace at the local bare repo
                return inner.RunAsync(spec with { Arguments = args }, ct);
            }
            if (failFetchWith is not null && args.Contains("fetch"))
                return Task.FromResult(new ProcessResult(128, "", failFetchWith, false));
            return inner.RunAsync(spec, ct);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task First_parent_commits_list_merges_and_direct_commits_newest_first()
    {
        Run(dev, "push", "origin", "main:production");
        Run(dev, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(dev, "feature.txt"), "f");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "feature work");
        var featureWork = Git(dev, "rev-parse HEAD");
        Run(dev, "checkout", "main");
        Run(dev, "merge", "--no-ff", "-m", "merge feature", "feature");
        var merge = Git(dev, "rev-parse HEAD");
        File.WriteAllText(Path.Combine(dev, "direct.txt"), "d");
        Run(dev, "add", "-A");
        Run(dev, "commit", "-m", "direct");
        var direct = Git(dev, "rev-parse HEAD");
        Run(dev, "push", "origin", "main");
        var ws = await git.PrepareAsync(project, "_release", "main", default);

        var commits = await git.FirstParentCommitsAsync(ws,
            "refs/remotes/origin/production..refs/remotes/origin/main", default);

        Assert.Equal([direct, merge], commits);
        Assert.DoesNotContain(featureWork, commits);
        Assert.Empty(await git.FirstParentCommitsAsync(ws, "refs/remotes/origin/main..refs/remotes/origin/production", default));
    }

    [Fact]
    public async Task First_parent_commits_refuse_a_range_that_reads_as_an_option()
    {
        var ws = await git.PrepareAsync(project, "_release", "main", default);

        await Assert.ThrowsAsync<ArgumentException>(() => git.FirstParentCommitsAsync(ws, "--all", default));
    }

    [Fact]
    public async Task Credentials_in_the_developer_origin_never_reach_the_workspace_and_are_warned_about_once()
    {
        var runner = new CredentialOrigin(new ProcessRunner(), bare);
        var log = new ListLogger();
        var g = new GitWorkspace(runner, workRoot, log);

        await g.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);
        await g.PrepareAsync(project, "ORD-2", "symphonist/ORD-2", default);

        Assert.Equal(["https://bitbucket.org/acme/orders.git", "https://bitbucket.org/acme/orders.git"], runner.SetUrls);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain("s3cret", warning.Message);
        Assert.All(log.Entries, e => Assert.DoesNotContain("s3cret", e.Message));
    }

    [Fact]
    public async Task Git_failure_messages_redact_credentials()
    {
        var runner = new CredentialOrigin(new ProcessRunner(), bare,
            failFetchWith: $"fatal: unable to access '{CredentialUrl}/': The requested URL returned error: 403");
        var g = new GitWorkspace(runner, workRoot);

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => g.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default));

        Assert.DoesNotContain("s3cret", ex.Message);
        Assert.Contains("https://***@bitbucket.org/acme/orders.git", ex.Message);
    }

    [Theory]
    [InlineData("https://dev:s3cret@bitbucket.org/acme/orders.git", "https://bitbucket.org/acme/orders.git")]
    [InlineData("http://token@host.example/x.git", "http://host.example/x.git")]
    [InlineData("https://bitbucket.org/acme/orders.git", "https://bitbucket.org/acme/orders.git")]
    [InlineData("git@bitbucket.org:acme/orders.git", "git@bitbucket.org:acme/orders.git")]
    [InlineData("C:/src/orders", "C:/src/orders")]
    public void Strip_user_info_only_touches_http_urls(string url, string expected) =>
        Assert.Equal(expected, GitUrls.StripUserInfo(url));

    [Fact]
    public void Redact_masks_user_info_anywhere_in_text() =>
        Assert.Equal("git push https://***@h.example/x.git failed; see 'http://***@other/y'",
            GitUrls.Redact("git push https://u:p@h.example/x.git failed; see 'http://tok@other/y'"));

    [Fact]
    public async Task Git_never_prompts_for_credentials()
    {
        var runner = new CredentialOrigin(new ProcessRunner(), bare);
        var g = new GitWorkspace(runner, workRoot);

        await g.PrepareAsync(project, "ORD-1", "symphonist/ORD-1", default);

        Assert.All(runner.Specs, spec =>
        {
            Assert.Equal("0", spec.Environment!["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("never", spec.Environment!["GCM_INTERACTIVE"]);
        });
    }
}
