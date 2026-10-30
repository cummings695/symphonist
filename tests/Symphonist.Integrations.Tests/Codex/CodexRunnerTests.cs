using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Codex;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.Codex;

public sealed class CodexRunnerTests : IDisposable
{
    /// <summary>
    /// Records the spec and lets a test act on the mounted per-run directory (as Codex in the container would) before
    /// returning a result, throwing, or reporting a timeout.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessSpec? Last { get; private set; }
        public string? RunDir { get; private set; }
        public ProcessResult Result { get; set; } = new(0, "out", "", false);
        public Action<string>? OnRun { get; set; }
        public Exception? Throws { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Last = spec;
            RunDir = spec.Mounts?.SingleOrDefault(m => m.ContainerPath == CodexRunner.RunFilesMount)?.HostPath;
            if (RunDir is not null) OnRun?.Invoke(RunDir);
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result);
        }
    }

    private sealed class PassthroughLauncher(bool providesIsolation) : IAgentLauncher
    {
        public List<ProcessSpec> CleanedUp { get; } = [];
        public bool ProvidesIsolation(AgentRunRequest request) => providesIsolation;
        public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request) => agentCommand;
        public Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct)
        {
            CleanedUp.Add(wrapped);
            return Task.CompletedTask;
        }
    }

    private const string Completed = """{"status":"completed","summary":"did it"}""";

    private readonly string temp = Path.Combine(Path.GetTempPath(), "symphonist-codex-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string codexHome;
    private readonly string runFiles;
    private readonly FakeProcessRunner fake = new();
    private readonly PassthroughLauncher launcher = new(true);

    public CodexRunnerTests()
    {
        codexHome = Path.Combine(temp, "codex-home");
        runFiles = Path.Combine(temp, "runs");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(runFiles);
        fake.OnRun = dir => WriteLastMessage(dir, Completed);
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }

    private CodexRunner Runner(IAgentLauncher? l = null) => new(fake, l ?? launcher, codexHome, runFiles);

    private static AgentRunRequest Request(string? model = null, bool readOnly = false,
        CodexAuth auth = CodexAuth.ApiKey, string schema = StageOutputSchema.Json) =>
        new("/work", "do the thing",
            new AgentSettings(AgentTypes.Codex, Isolation.Container, [], [], 7, model, null)
            {
                Codex = new CodexSettings(auth, CodexSettings.DefaultApiKeySecret),
            }, schema, readOnly);

    private static AgentRunRequest ChatGpt() => Request(auth: CodexAuth.ChatGpt);

    private static void WriteLastMessage(string runDir, string json) =>
        File.WriteAllText(Path.Combine(runDir, "last-message.json"), json);

    private string DeveloperAuth => Path.Combine(codexHome, "auth.json");

    private void SignIn(string json = """{"tokens":{"refresh_token":"old"}}""") => File.WriteAllText(DeveloperAuth, json);

    private void RunDirsAreGone() => Assert.Empty(Directory.EnumerateFileSystemEntries(runFiles));

    [Fact]
    public async Task Refuses_without_isolation()
    {
        var result = await Runner(new PassthroughLauncher(false)).RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal("codex runs only in containers", result.Error);
        Assert.Null(fake.Last);
        RunDirsAreGone();
    }

    [Fact]
    public async Task Write_run_arguments()
    {
        await Runner().RunAsync(Request(), default);
        var spec = fake.Last!;
        Assert.Equal("codex", spec.FileName);
        Assert.Equal(
        [
            "exec", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", "--ephemeral",
            "--color", "never", "--output-schema", "/symphonist-run/schema.json",
            "-o", "/symphonist-run/last-message.json", "-",
        ], spec.Arguments);
        Assert.Equal("do the thing", spec.StandardInput);
        Assert.Equal("/work", spec.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(7), spec.Timeout);
        Assert.Equal("/symphonist-run/home", spec.Environment!["CODEX_HOME"]);
        var mount = Assert.Single(spec.Mounts!);
        Assert.Equal("/symphonist-run", mount.ContainerPath);
        Assert.False(mount.ReadOnly);
        Assert.Equal(Path.GetFullPath(runFiles), Path.GetDirectoryName(mount.HostPath));
    }

    [Fact]
    public async Task Read_only_run_adds_hardening()
    {
        await Runner().RunAsync(Request(readOnly: true), default);
        var args = fake.Last!.Arguments.ToList();
        var i = args.IndexOf("project_doc_max_bytes=0");
        Assert.True(i > 0);
        Assert.Equal("-c", args[i - 1]);
        Assert.Equal("-c", args[i + 1]);
        Assert.Equal("web_search=\"disabled\"", args[i + 2]);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public async Task Model_is_passed_when_set()
    {
        await Runner().RunAsync(Request(model: "gpt-5-codex", readOnly: true), default);
        var args = fake.Last!.Arguments.ToList();
        Assert.Equal(["-m", "gpt-5-codex", "-"], args[^3..]);

        await Runner().RunAsync(Request(model: " "), default);
        Assert.DoesNotContain("-m", fake.Last!.Arguments);
    }

    [Fact]
    public async Task Schema_file_is_strict()
    {
        string? schema = null;
        fake.OnRun = dir =>
        {
            schema = File.ReadAllText(Path.Combine(dir, "schema.json"));
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(Request(schema: PrFixOutputSchema.Json), default);
        Assert.Equal(CodexSchema.Strict(PrFixOutputSchema.Json), schema);
    }

    [Fact]
    public async Task Completed_stage_output_succeeds_with_null_cost()
    {
        fake.Result = new ProcessResult(0, "progress", "warn", false);
        var result = await Runner().RunAsync(Request(), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.CostUsd);
        Assert.Equal("completed", result.Output!.Status);
        Assert.Equal("did it", result.Output.Summary);
        Assert.Equal("progress\n--- stderr ---\nwarn", result.RawLog);
    }

    [Fact]
    public async Task Null_optional_fields_are_dropped()
    {
        fake.OnRun = dir => WriteLastMessage(dir,
            """{"status":"completed","summary":"s","comment_markdown":null,"tests_passed":null,"pr_title":"t"}""");
        var result = await Runner().RunAsync(Request(), default);
        Assert.True(result.Succeeded, result.Error);
        var structured = result.Structured!.Value;
        Assert.False(structured.TryGetProperty("comment_markdown", out _));
        Assert.False(structured.TryGetProperty("tests_passed", out _));
        Assert.Equal("t", structured.GetProperty("pr_title").GetString());
        Assert.Null(result.Output!.TestsPassed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1]")]
    public async Task Missing_last_message_fails(string? content)
    {
        fake.OnRun = dir => { if (content is not null) WriteLastMessage(dir, content); };
        var result = await Runner().RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal("missing structured output", result.Error);
    }

    [Fact]
    public async Task Nonzero_exit_fails_with_stderr()
    {
        fake.Result = new ProcessResult(2, "", "boom: bad model", false);
        var result = await Runner().RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal("boom: bad model", result.Error);

        fake.Result = new ProcessResult(3, "", "  ", false);
        result = await Runner().RunAsync(Request(), default);
        Assert.Equal("agent exited with code 3", result.Error);
    }

    [Fact]
    public async Task Timeout_fails_and_cleans_up()
    {
        fake.Result = new ProcessResult(-1, "", "", true);
        var result = await Runner().RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal("timed out after 7 minutes", result.Error);
        Assert.Single(launcher.CleanedUp);
    }

    [Fact]
    public async Task Cancel_cleans_up_and_rethrows()
    {
        fake.Throws = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runner().RunAsync(Request(), default));
        Assert.Single(launcher.CleanedUp);
    }

    [Fact]
    public async Task Chatgpt_without_auth_file_fails_with_login_hint()
    {
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.False(result.Succeeded);
        Assert.Equal("Codex is not signed in: run `codex login` on this machine", result.Error);
        Assert.Null(fake.Last);
        RunDirsAreGone();
    }

    [Fact]
    public async Task Chatgpt_auth_is_copied_into_the_run_home()
    {
        SignIn();
        string? copied = null;
        fake.OnRun = dir =>
        {
            copied = File.ReadAllText(Path.Combine(dir, "home", "auth.json"));
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", copied);
    }

    [Fact]
    public async Task Chatgpt_refreshed_auth_is_written_back()
    {
        SignIn();
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"new"}}""");
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"new"}}""", File.ReadAllText(DeveloperAuth));
        Assert.Equal(["auth.json"], Directory.GetFiles(codexHome).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Chatgpt_unchanged_auth_is_not_rewritten()
    {
        SignIn();
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(DeveloperAuth, stamp);
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(DeveloperAuth));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task Chatgpt_corrupt_auth_is_not_written_back(string corrupt)
    {
        SignIn();
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), corrupt);
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
    }

    [Fact]
    public async Task Chatgpt_deleted_auth_is_not_written_back()
    {
        SignIn();
        fake.OnRun = dir => File.Delete(Path.Combine(dir, "home", "auth.json"));
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
    }

    [Fact]
    public async Task Chatgpt_refreshed_auth_is_written_back_after_timeout_and_cancel()
    {
        SignIn();
        fake.OnRun = dir => File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"v":1}""");
        fake.Result = new ProcessResult(-1, "", "", true);
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"v":1}""", File.ReadAllText(DeveloperAuth));

        fake.OnRun = dir => File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"v":2}""");
        fake.Throws = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runner().RunAsync(ChatGpt(), default));
        Assert.Equal("""{"v":2}""", File.ReadAllText(DeveloperAuth));
    }

    [Fact]
    public async Task Run_directory_is_deleted_on_success_failure_timeout_and_cancel()
    {
        SignIn();
        var seen = new List<string>();
        fake.OnRun = dir => { seen.Add(dir); WriteLastMessage(dir, Completed); };

        await Runner().RunAsync(ChatGpt(), default);
        RunDirsAreGone();

        fake.Result = new ProcessResult(1, "", "failed", false);
        await Runner().RunAsync(ChatGpt(), default);
        RunDirsAreGone();

        fake.Result = new ProcessResult(-1, "", "", true);
        await Runner().RunAsync(ChatGpt(), default);
        RunDirsAreGone();

        fake.Throws = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runner().RunAsync(ChatGpt(), default));
        RunDirsAreGone();

        fake.Throws = new InvalidOperationException("docker exploded");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(ChatGpt(), default));
        RunDirsAreGone();

        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task Run_directory_is_deleted_when_the_launcher_throws()
    {
        SignIn();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(new ThrowingLauncher()).RunAsync(ChatGpt(), default));
        RunDirsAreGone();
    }

    private sealed class ThrowingLauncher : IAgentLauncher
    {
        public bool ProvidesIsolation(AgentRunRequest request) => true;
        public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request) =>
            throw new InvalidOperationException("secret missing");
        public Task CleanupAsync(ProcessSpec wrapped, AgentRunRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Plants a symlink as the agent could; false when this machine does not permit creating one (e.g. Windows without
    /// developer mode), in which case the calling test has nothing to check.
    /// </summary>
    private static bool TryLink(string link, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Symlinked_auth_is_not_written_back()
    {
        SignIn();
        var hostSecret = Path.Combine(temp, "other-credentials.json");
        File.WriteAllText(hostSecret, """{"claudeAiOauth":{"accessToken":"secret"}}""");
        var linked = true;
        fake.OnRun = dir =>
        {
            var auth = Path.Combine(dir, "home", "auth.json");
            File.Delete(auth);
            linked = TryLink(auth, hostSecret);
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
        if (linked) Assert.Contains("auth.json not written back: it is a link", result.RawLog);
    }

    [Fact]
    public async Task Symlinked_home_directory_is_not_written_back()
    {
        SignIn();
        var hostDir = Path.Combine(temp, "host-dir");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "auth.json"), """{"tokens":{"refresh_token":"stolen"}}""");
        fake.OnRun = dir =>
        {
            var home = Path.Combine(dir, "home");
            Directory.Delete(home, recursive: true);
            TryLink(home, hostDir, directory: true);
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
        Assert.True(File.Exists(Path.Combine(hostDir, "auth.json"))); // deleting the run dir does not follow the link
    }

    [Fact]
    public async Task Symlinked_last_message_is_missing_structured_output()
    {
        var hostFile = Path.Combine(temp, "host.json");
        File.WriteAllText(hostFile, Completed);
        var linked = true;
        fake.OnRun = dir => linked = TryLink(Path.Combine(dir, "last-message.json"), hostFile);
        var result = await Runner().RunAsync(Request(), default);
        if (!linked) return;
        Assert.False(result.Succeeded);
        Assert.Equal("missing structured output", result.Error);
    }

    [Fact]
    public async Task Oversize_last_message_is_missing_structured_output()
    {
        fake.OnRun = dir => WriteLastMessage(dir,
            """{"status":"completed","summary":"s","comment_markdown":""" + "\"" + new string('x', 1024 * 1024) + "\"}");
        var result = await Runner().RunAsync(Request(), default);
        Assert.Equal("missing structured output", result.Error);
    }

    [Fact]
    public async Task Oversize_auth_is_not_written_back()
    {
        SignIn();
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"),
                """{"pad":""" + "\"" + new string('x', 64 * 1024) + "\"}");
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
        Assert.Contains("auth.json not written back: it is too large", result.RawLog);
    }

    [Fact]
    public async Task Auth_for_another_account_is_not_written_back()
    {
        SignIn("""{"tokens":{"account_id":"acct-1","refresh_token":"old"}}""");
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"),
                """{"tokens":{"account_id":"acct-2","refresh_token":"theirs"}}""");
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("""{"tokens":{"account_id":"acct-1","refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
        Assert.Contains("auth.json not written back: it names a different account", result.RawLog);

        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"none"}}""");
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"account_id":"acct-1","refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
    }

    [Fact]
    public async Task Auth_for_the_same_account_is_written_back()
    {
        SignIn("""{"tokens":{"account_id":"acct-1","refresh_token":"old"}}""");
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"),
                """{"tokens":{"account_id":"acct-1","refresh_token":"new"}}""");
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"account_id":"acct-1","refresh_token":"new"}}""", File.ReadAllText(DeveloperAuth));
        Assert.DoesNotContain("not written back", result.RawLog);
    }

    [Fact]
    public async Task Failed_write_back_is_noted_in_the_raw_log()
    {
        SignIn();
        // Windows: an open handle without delete sharing blocks replacing the file. Elsewhere: a read-only Codex home
        // blocks creating the temp file (not for root, which the check below then skips).
        FileStream? held = null;
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"new"}}""");
            if (OperatingSystem.IsWindows())
                held = new FileStream(DeveloperAuth, FileMode.Open, FileAccess.Read, FileShare.Read);
            else
                File.SetUnixFileMode(codexHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            WriteLastMessage(dir, Completed);
        };
        try
        {
            var result = await Runner().RunAsync(ChatGpt(), default);
            Assert.True(result.Succeeded, result.Error);
            if (File.ReadAllText(DeveloperAuth).Contains("new")) return; // running as root
            Assert.Contains("Codex auth.json not written back:", result.RawLog);
            Assert.Equal(["auth.json"], Directory.GetFiles(codexHome).Select(Path.GetFileName));
        }
        finally
        {
            held?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(codexHome, OwnerOnlyDir);
        }
    }

    [Fact]
    public async Task Sign_in_changed_elsewhere_during_the_run_is_not_overwritten()
    {
        SignIn();
        fake.OnRun = dir =>
        {
            // The developer ran `codex login` again (or another run refreshed first) while this run also refreshed.
            File.WriteAllText(DeveloperAuth, """{"tokens":{"refresh_token":"newer-login"}}""");
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"stale-run"}}""");
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("""{"tokens":{"refresh_token":"newer-login"}}""", File.ReadAllText(DeveloperAuth));
        Assert.Contains("Codex sign-in changed elsewhere; not written back", result.RawLog);
    }

    [Fact]
    public async Task Sign_in_removed_during_the_run_is_not_recreated()
    {
        SignIn();
        fake.OnRun = dir =>
        {
            File.Delete(DeveloperAuth); // `codex logout`
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"new"}}""");
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.False(File.Exists(DeveloperAuth));
        Assert.Contains("Codex sign-in changed elsewhere; not written back", result.RawLog);
    }

    [Fact]
    public async Task Unreadable_run_auth_is_noted_and_not_written_back()
    {
        SignIn();
        FileStream? held = null;
        fake.OnRun = dir =>
        {
            var auth = Path.Combine(dir, "home", "auth.json");
            File.WriteAllText(auth, """{"tokens":{"refresh_token":"new"}}""");
            if (OperatingSystem.IsWindows())
                held = new FileStream(auth, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else
                File.SetUnixFileMode(auth, UnixFileMode.None);
            WriteLastMessage(dir, Completed);
        };
        try
        {
            var result = await Runner().RunAsync(ChatGpt(), default);
            if (File.ReadAllText(DeveloperAuth).Contains("new")) return; // running as root
            Assert.Equal("""{"tokens":{"refresh_token":"old"}}""", File.ReadAllText(DeveloperAuth));
            Assert.Contains("Codex auth.json not written back: it could not be read", result.RawLog);
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task Run_directory_that_cannot_be_deleted_is_noted()
    {
        string? runDir = null;
        FileStream? held = null;
        fake.OnRun = dir =>
        {
            runDir = dir;
            WriteLastMessage(dir, Completed);
            if (OperatingSystem.IsWindows())
                held = new FileStream(Path.Combine(dir, "schema.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            else
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        };
        try
        {
            var result = await Runner().RunAsync(Request(), default);
            Assert.True(result.Succeeded, result.Error);
            if (!Directory.Exists(runDir)) return; // running as root
            Assert.Contains("Codex run directory not deleted:", result.RawLog);
        }
        finally
        {
            held?.Dispose();
            if (!OperatingSystem.IsWindows() && Directory.Exists(runDir)) File.SetUnixFileMode(runDir!, OwnerOnlyDir);
        }
    }

    [Fact]
    public void Cleanup_removes_leftover_run_directories_only()
    {
        var stale = Path.Combine(runFiles, "stale");
        Directory.CreateDirectory(Path.Combine(stale, "home"));
        File.WriteAllText(Path.Combine(stale, "home", "auth.json"), """{"tokens":{"refresh_token":"live"}}""");
        Directory.CreateDirectory(Path.Combine(runFiles, "other"));
        var hostDir = Path.Combine(temp, "host-dir");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "keep.txt"), "x");
        var linked = TryLink(Path.Combine(runFiles, "linked"), hostDir, directory: true);

        var removed = CodexRunner.CleanupRunFiles(runFiles);

        Assert.Equal(linked ? 3 : 2, removed);
        Assert.True(Directory.Exists(runFiles));
        RunDirsAreGone();
        Assert.True(File.Exists(Path.Combine(hostDir, "keep.txt"))); // a link is removed, not followed
    }

    [Fact]
    public void Cleanup_without_a_run_files_root_does_nothing()
    {
        Assert.Equal(0, CodexRunner.CleanupRunFiles(Path.Combine(temp, "missing")));
    }

    [Fact]
    public async Task Failed_run_after_another_run_rotated_the_sign_in_says_so()
    {
        SignIn();
        fake.OnRun = _ => File.WriteAllText(DeveloperAuth, """{"tokens":{"refresh_token":"rotated"}}""");
        fake.Result = new ProcessResult(1, "", "401 refresh token already used", false);
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("401 refresh token already used; " + CodexRunner.RotatedElsewhereHint, result.Error);
    }

    [Fact]
    public async Task Failed_run_without_rotation_keeps_its_error()
    {
        SignIn();
        fake.OnRun = dir => File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"mine"}}""");
        fake.Result = new ProcessResult(1, "", "bad model", false);
        var result = await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("bad model", result.Error);
        Assert.Equal("""{"tokens":{"refresh_token":"mine"}}""", File.ReadAllText(DeveloperAuth));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Failed_run_with_limit_text_sets_Limit()
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        fake.Result = new ProcessResult(1, "", "You've hit your usage limit. Try again in 2 hours 5 minutes.", false);
        var result = await new CodexRunner(fake, launcher, codexHome, runFiles, new FixedTime(now))
            .RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal(now.AddHours(2).AddMinutes(5), result.Limit!.ResetsAt);
        Assert.Equal("You've hit your usage limit. Try again in 2 hours 5 minutes.", result.Error);
    }

    [Fact]
    public async Task Failed_run_with_strict_limit_phrasing_on_stdout_sets_Limit()
    {
        fake.Result = new ProcessResult(1, "exceeded retry limit, last status: 429 Too Many Requests", "", false);
        var result = await Runner().RunAsync(Request(), default);
        Assert.NotNull(result.Limit);
    }

    [Fact]
    public async Task Failed_run_with_rate_limit_prose_on_stdout_has_no_Limit()
    {
        fake.Result = new ProcessResult(1, "I will add a rate limit: return 429 Too Many Requests after 10 calls", "boom", false);
        var result = await Runner().RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Null(result.Limit);
    }

    [Fact]
    public async Task Successful_run_mentioning_limit_has_no_Limit()
    {
        fake.Result = new ProcessResult(0, "you've hit your usage limit", "usage limit", false);
        var result = await Runner().RunAsync(Request(), default);
        Assert.True(result.Succeeded);
        Assert.Null(result.Limit);
    }

    private const UnixFileMode OwnerOnlyFile =UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDir = OwnerOnlyFile | UnixFileMode.UserExecute;

    [Containers.NonWindowsFact, System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Run_files_are_owner_only()
    {
        SignIn();
        UnixFileMode? runDir = null, home = null, auth = null;
        fake.OnRun = dir =>
        {
            runDir = File.GetUnixFileMode(dir);
            home = File.GetUnixFileMode(Path.Combine(dir, "home"));
            auth = File.GetUnixFileMode(Path.Combine(dir, "home", "auth.json"));
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal(OwnerOnlyDir, runDir);
        Assert.Equal(OwnerOnlyDir, home);
        Assert.Equal(OwnerOnlyFile, auth);
    }

    [Containers.NonWindowsFact, System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Written_back_auth_is_owner_only()
    {
        SignIn();
        File.SetUnixFileMode(DeveloperAuth, OwnerOnlyFile);
        fake.OnRun = dir =>
        {
            File.WriteAllText(Path.Combine(dir, "home", "auth.json"), """{"tokens":{"refresh_token":"new"}}""");
            WriteLastMessage(dir, Completed);
        };
        await Runner().RunAsync(ChatGpt(), default);
        Assert.Equal("""{"tokens":{"refresh_token":"new"}}""", File.ReadAllText(DeveloperAuth));
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(DeveloperAuth));
    }

    [Fact]
    public async Task Api_key_run_copies_no_auth_file()
    {
        SignIn();
        var homeFiles = new List<string>();
        fake.OnRun = dir =>
        {
            homeFiles.AddRange(Directory.EnumerateFileSystemEntries(Path.Combine(dir, "home")));
            WriteLastMessage(dir, Completed);
        };
        var result = await Runner().RunAsync(Request(auth: CodexAuth.ApiKey), default);
        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(homeFiles);
    }

    [Fact]
    public void Default_codex_home_honours_CODEX_HOME()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", temp);
            Assert.Equal(temp, CodexRunner.DefaultCodexHome());
            Environment.SetEnvironmentVariable("CODEX_HOME", " ");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
                CodexRunner.DefaultCodexHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }
}
