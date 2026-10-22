using Symphonist.Core.Config;
using Symphonist.Integrations.Git;
using Symphonist.Integrations.Processes;

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
            new RepoConfig("bitbucket", "w", "s", dev, "main", "main", "e", "t", "squash"),
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
}
