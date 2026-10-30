using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Risk;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.ClaudeCode;

public class ClaudeCodeRunnerTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessSpec Last { get; private set; } = null!;
        private ProcessResult _result = new(0, "", "", false);
        public void Returns(int exit, string stdout, string stderr = "", bool timedOut = false) =>
            _result = new ProcessResult(exit, stdout, stderr, timedOut);
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Last = spec;
            return Task.FromResult(_result);
        }
    }

    /// <summary>Passes the command through unchanged, reporting the given isolation capability.</summary>
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

    private sealed class CancelingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            throw new OperationCanceledException(ct);
    }

    private readonly FakeProcessRunner fake = new();
    private readonly PassthroughLauncher launcher = new(true);
    private readonly ClaudeCodeRunner runner;

    public ClaudeCodeRunnerTests() => runner = new ClaudeCodeRunner(fake, launcher);

    private static AgentRunRequest Request(Isolation isolation = Isolation.Container, string[]? tools = null,
        string? model = null, string? attachments = null) =>
        new("/work", "do the thing",
            new AgentSettings("claude-code", isolation, [], tools ?? [], 7, model, null),
            AttachmentsDirectory: attachments);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string ArgAfter(ProcessSpec spec, string flag) =>
        spec.Arguments[spec.Arguments.ToList().IndexOf(flag) + 1];

    [Fact]
    public async Task Container_mode_bypasses_permissions_and_sends_prompt_on_stdin()
    {
        await runner.RunAsync(Request(Isolation.Container), default);
        Assert.Equal("claude", fake.Last.FileName);
        Assert.Contains("--dangerously-skip-permissions", fake.Last.Arguments);
        Assert.Equal("do the thing", fake.Last.StandardInput);
        Assert.DoesNotContain("do the thing", fake.Last.Arguments);
        Assert.Equal("/work", fake.Last.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(7), fake.Last.Timeout);
        Assert.Equal(StageOutputSchema.Json, ArgAfter(fake.Last, "--json-schema"));
        Assert.Equal("json", ArgAfter(fake.Last, "--output-format"));
    }

    [Fact]
    public async Task Host_mode_uses_allowlist_and_never_bypasses()
    {
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read", "Bash(dotnet test:*)"]), default);
        Assert.DoesNotContain("--dangerously-skip-permissions", fake.Last.Arguments);
        Assert.Equal("Read,Bash(dotnet test:*)", ArgAfter(fake.Last, "--allowedTools"));

        var onHost = new ClaudeCodeRunner(fake, new HostLauncher());
        await onHost.RunAsync(Request(Isolation.Host, tools: ["Read"]), default);
        Assert.DoesNotContain("--dangerously-skip-permissions", fake.Last.Arguments);
        Assert.Equal("Read", ArgAfter(fake.Last, "--allowedTools"));
    }

    [Fact]
    public async Task Host_mode_adds_attachments_dir()
    {
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read"], attachments: "/att/T-1"), default);
        Assert.Equal("/att/T-1", ArgAfter(fake.Last, "--add-dir"));
        Assert.Equal("Read", ArgAfter(fake.Last, "--allowedTools"));
    }

    [Fact]
    public async Task Host_mode_denies_edits_to_attachments_dir()
    {
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read", "Edit", "Write"], attachments: "/att/T-1"), default);
        Assert.Equal("Edit(//att/T-1/**),Write(//att/T-1/**)", ArgAfter(fake.Last, "--disallowedTools"));
    }

    [Fact]
    public async Task Host_mode_denies_edits_to_windows_attachments_dir_in_posix_form()
    {
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read"], attachments: @"C:\Users\me\.symphonist\att\T-1\"),
            default);
        Assert.Equal("Edit(//c/Users/me/.symphonist/att/T-1/**),Write(//c/Users/me/.symphonist/att/T-1/**)",
            ArgAfter(fake.Last, "--disallowedTools"));
    }

    [Fact]
    public async Task Read_only_host_run_with_attachments_passes_one_merged_deny_list()
    {
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read"], attachments: "/att/T-1")
            with { OutputSchema = GateReviewSchema.Json, ReadOnlyWorkspace = true }, default);
        Assert.Single(fake.Last.Arguments, a => a == "--disallowedTools");
        Assert.Equal(string.Join(",", [.. ClaudeCodeRunner.ReadOnlyDeniedTools, "Edit(//att/T-1/**)", "Write(//att/T-1/**)"]),
            ArgAfter(fake.Last, "--disallowedTools"));
    }

    [Fact]
    public async Task Container_mode_does_not_add_dir()
    {
        await runner.RunAsync(Request(Isolation.Container, attachments: "/att/T-1"), default);
        Assert.DoesNotContain("--add-dir", fake.Last.Arguments);
    }

    [Fact]
    public async Task Container_settings_with_host_launcher_fail_closed()
    {
        var onHost = new ClaudeCodeRunner(fake, new HostLauncher());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => onHost.RunAsync(Request(Isolation.Container), default));
        Assert.Equal("container isolation requested but HostLauncher cannot provide it", ex.Message);
        Assert.Null(fake.Last);
    }

    [Fact]
    public async Task Container_settings_without_an_isolating_launcher_never_bypass()
    {
        var unisolated = new ClaudeCodeRunner(fake, new PassthroughLauncher(false));
        await unisolated.RunAsync(Request(Isolation.Container, tools: ["Read"]), default);
        Assert.DoesNotContain("--dangerously-skip-permissions", fake.Last.Arguments);
        Assert.Equal("Read", ArgAfter(fake.Last, "--allowedTools"));
    }

    [Fact]
    public async Task Container_settings_with_docker_launcher_bypass_inside_the_container()
    {
        var inDocker = new ClaudeCodeRunner(fake, new DockerLauncher(fake, "/ctx", _ => null));
        await inDocker.RunAsync(Request(Isolation.Container), default);
        Assert.Equal("docker", fake.Last.FileName);
        var args = fake.Last.Arguments.ToList();
        Assert.True(args.IndexOf("--dangerously-skip-permissions") > args.IndexOf(DockerLauncher.DefaultImage));
        Assert.Contains(DockerLauncher.DefaultImage, args);
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("--dangerously-skip-permissions=true")]
    [InlineData("--permission-mode", "bypassPermissions")]
    [InlineData("--permission-mode=bypassPermissions")]
    [InlineData("--permission-mode", "BYPASSPERMISSIONS")]
    public void Host_launcher_rejects_permission_bypass(params string[] bypass)
    {
        Assert.Throws<InvalidOperationException>(() => new HostLauncher().Wrap(
            new ProcessSpec("claude", ["-p", .. bypass]), Request(Isolation.Host)));
    }

    [Fact]
    public void Host_launcher_allows_other_permission_modes()
    {
        var spec = new HostLauncher().Wrap(new ProcessSpec("claude", ["-p", "--permission-mode", "plan"]),
            Request(Isolation.Host));
        Assert.Equal("/work", spec.WorkingDirectory);
    }

    [Fact]
    public async Task Timed_out_run_is_cleaned_up_by_the_launcher()
    {
        fake.Returns(-1, "", "", timedOut: true);
        await runner.RunAsync(Request(), default);
        Assert.Same(fake.Last, Assert.Single(launcher.CleanedUp));
    }

    [Fact]
    public async Task Canceled_run_is_cleaned_up_by_the_launcher()
    {
        var canceling = new ClaudeCodeRunner(new CancelingProcessRunner(), launcher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceling.RunAsync(Request(), default));
        Assert.Single(launcher.CleanedUp);
    }

    [Fact]
    public async Task Completed_run_needs_no_cleanup()
    {
        fake.Returns(0, Fixture("claude-success.json"));
        await runner.RunAsync(Request(), default);
        Assert.Empty(launcher.CleanedUp);
    }

    [Fact]
    public void Launcher_isolation_capabilities()
    {
        Assert.False(new HostLauncher().ProvidesIsolation(Request(Isolation.Host)));
        Assert.True(new DockerLauncher(fake, "/ctx", _ => null).ProvidesIsolation(Request(Isolation.Container)));
    }

    [Fact]
    public async Task Model_is_passed_only_when_set()
    {
        await runner.RunAsync(Request(), default);
        Assert.DoesNotContain("--model", fake.Last.Arguments);
        await runner.RunAsync(Request(model: "opus"), default);
        Assert.Equal("opus", ArgAfter(fake.Last, "--model"));
    }

    [Fact]
    public async Task Parses_success_fixture()
    {
        fake.Returns(0, Fixture("claude-success.json"));
        var r = await runner.RunAsync(Request(), default);
        Assert.True(r.Succeeded);
        Assert.Equal("ok", r.Output!.Summary);
        Assert.NotNull(r.CostUsd);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Custom_schema_is_passed_and_structured_output_returned()
    {
        fake.Returns(0, """{"is_error":false,"total_cost_usd":0.5,"structured_output":{"risk":"low","summary":"fine","reasons":[],"checked":["diff"]}}""");
        var r = await runner.RunAsync(Request() with { OutputSchema = GateReviewSchema.Json }, default);
        Assert.Equal(GateReviewSchema.Json, ArgAfter(fake.Last, "--json-schema"));
        Assert.True(r.Succeeded);
        Assert.Null(r.Output);
        Assert.Null(r.Error);
        Assert.Equal("low", r.Structured!.Value.GetProperty("risk").GetString());
        Assert.Equal(0.5m, r.CostUsd);
    }

    [Fact]
    public void Read_rejects_missing_status()
    {
        Assert.Null(StageOutputSchema.Read(System.Text.Json.JsonDocument.Parse("""{"summary":"x"}""").RootElement));
        Assert.Null(StageOutputSchema.Read(System.Text.Json.JsonDocument.Parse("""{"status":"completed"}""").RootElement));
        Assert.Null(StageOutputSchema.Read(System.Text.Json.JsonDocument.Parse("""{"status":3,"summary":"x"}""").RootElement));
        Assert.Null(StageOutputSchema.Read(null));
        Assert.Equal("x", StageOutputSchema.Read(System.Text.Json.JsonDocument.Parse("""{"status":"completed","summary":"x","pr_title":"t"}""").RootElement)!.Summary);
    }

    [Fact]
    public async Task Pr_fix_schema_returns_structured_replies_without_stage_output()
    {
        fake.Returns(0, """{"is_error":false,"structured_output":{"status":"completed","summary":"s","replies":[{"comment_id":"issue:1","reply":"ok"},{"comment_id":3},{"reply":"x"}]}}""");
        var r = await runner.RunAsync(Request() with { OutputSchema = PrFixOutputSchema.Json }, default);
        Assert.True(r.Succeeded);
        Assert.Null(r.Output);
        Assert.Equal(new PrReply("issue:1", "ok"), Assert.Single(PrFixOutputSchema.ReadReplies(r.Structured)));
        Assert.Equal("s", StageOutputSchema.Read(r.Structured)!.Summary);
    }

    [Fact]
    public void Replies_are_bounded()
    {
        var long_ = new string('a', 5000);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            replies = Enumerable.Range(0, 80).Select(i => new { comment_id = $"issue:{i}", reply = long_ }).ToArray(),
        });
        var replies = PrFixOutputSchema.ReadReplies(json);
        Assert.Equal(50, replies.Count);
        Assert.All(replies, r => Assert.True(r.Reply.Length <= ReviewerText.MaxSummaryChars * 4));
    }

    [Fact]
    public async Task Custom_schema_without_structured_output_fails()
    {
        fake.Returns(0, """{"is_error":false,"result":"{\"risk\":\"low\"}"}""");
        var r = await runner.RunAsync(Request() with { OutputSchema = GateReviewSchema.Json }, default);
        Assert.False(r.Succeeded);
        Assert.Null(r.Structured);
        Assert.Equal("missing structured output", r.Error);
    }

    [Theory]
    [InlineData("exit-1")]
    [InlineData("is-error")]
    [InlineData("timeout")]
    [InlineData("not-json")]
    public async Task Custom_schema_bad_runs_fail(string variant)
    {
        const string structured = """{"is_error":false,"structured_output":{"risk":"low","summary":"s","reasons":[],"checked":["x"]}}""";
        switch (variant)
        {
            case "exit-1": fake.Returns(1, structured, "boom"); break;
            case "is-error": fake.Returns(0, """{"is_error":true,"result":"API Error","structured_output":{"risk":"low"}}"""); break;
            case "timeout": fake.Returns(-1, "", "", timedOut: true); break;
            case "not-json": fake.Returns(0, "garbage"); break;
        }
        var r = await runner.RunAsync(Request() with { OutputSchema = GateReviewSchema.Json }, default);
        Assert.False(r.Succeeded);
        Assert.Null(r.Output);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Failed_run_with_limit_text_sets_Limit()
    {
        fake.Returns(1, "", "Claude AI usage limit reached|1793210400");
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793210400), r.Limit!.ResetsAt);
        Assert.Equal("Claude AI usage limit reached|1793210400", r.Error);
    }

    [Fact]
    public async Task Error_result_text_with_rate_limit_sets_Limit()
    {
        fake.Returns(1, """{"is_error":true,"result":"API Error: 429 rate_limit_error"}""");
        var timed = new ClaudeCodeRunner(fake, launcher, new FixedTime(Now));
        var r = await timed.RunAsync(Request(), default);
        Assert.Equal(Now.AddMinutes(5), r.Limit!.ResetsAt);
    }

    [Fact]
    public async Task Failed_run_with_overloaded_error_has_no_Limit()
    {
        fake.Returns(1, Fixture("claude-error.json"));
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.Null(r.Limit);
    }

    [Fact]
    public async Task Successful_run_mentioning_limit_has_no_Limit()
    {
        fake.Returns(0, """{"is_error":false,"result":"usage limit reached","structured_output":{"status":"completed","summary":"documented the usage limit reached handling"}}""");
        var r = await runner.RunAsync(Request(), default);
        Assert.True(r.Succeeded);
        Assert.Null(r.Limit);
    }

    [Fact]
    public async Task Stage_schema_behaviour_unchanged()
    {
        fake.Returns(0, Fixture("claude-success.json"));
        var r = await runner.RunAsync(Request(), default);
        Assert.Equal(StageOutputSchema.Json, ArgAfter(fake.Last, "--json-schema"));
        Assert.True(r.Succeeded);
        Assert.Equal("ok", r.Output!.Summary);
        Assert.Equal("completed", r.Structured!.Value.GetProperty("status").GetString());
    }

    private static AgentRunRequest ReadOnly(Isolation isolation, string[]? tools = null) =>
        Request(isolation, tools) with { OutputSchema = GateReviewSchema.Json, ReadOnlyWorkspace = true };

    [Fact]
    public async Task Host_read_only_run_with_default_tools_allows_only_reads_and_denies_writes()
    {
        await runner.RunAsync(ReadOnly(Isolation.Host, [.. ReviewConfig.ReadOnlyTools]), default);
        Assert.Equal("Read,Grep,Glob", ArgAfter(fake.Last, "--allowedTools"));
        Assert.Equal("Write,Edit,NotebookEdit,Bash,WebFetch,WebSearch", ArgAfter(fake.Last, "--disallowedTools"));
        Assert.DoesNotContain("--dangerously-skip-permissions", fake.Last.Arguments);
        AssertIgnoresWorkspaceConfig(fake.Last);
    }

    /// <summary>The workspace's own Claude Code settings, CLAUDE.md and MCP servers must not steer a read-only run.</summary>
    private static void AssertIgnoresWorkspaceConfig(ProcessSpec spec)
    {
        Assert.Equal("user", ArgAfter(spec, "--setting-sources"));
        Assert.Contains("--strict-mcp-config", spec.Arguments);
        Assert.DoesNotContain("--mcp-config", spec.Arguments);
    }

    [Theory]
    [InlineData("Bash(git diff:*)")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("read")]
    public async Task Host_read_only_run_refuses_write_capable_tools_without_starting(string tool)
    {
        var r = await runner.RunAsync(ReadOnly(Isolation.Host, ["Read", tool]), default);
        Assert.False(r.Succeeded);
        Assert.Equal($"read-only run refuses write-capable tool '{tool}'", r.Error);
        Assert.Null(r.Output);
        Assert.Null(r.Structured);
        Assert.Null(fake.Last);
    }

    [Fact]
    public async Task Container_read_only_run_denies_writes()
    {
        await runner.RunAsync(ReadOnly(Isolation.Container, ["Write"]), default);
        Assert.Contains("--dangerously-skip-permissions", fake.Last.Arguments);
        Assert.Equal("Write,Edit,NotebookEdit,Bash,WebFetch,WebSearch", ArgAfter(fake.Last, "--disallowedTools"));
        AssertIgnoresWorkspaceConfig(fake.Last);
    }

    [Fact]
    public async Task Non_read_only_runs_have_no_deny_list()
    {
        await runner.RunAsync(Request(Isolation.Container), default);
        Assert.Equal(["-p", "--output-format", "json", "--json-schema", StageOutputSchema.Json,
            "--dangerously-skip-permissions"], fake.Last.Arguments);
        await runner.RunAsync(Request(Isolation.Host, tools: ["Read", "Write", "Bash(dotnet test:*)"]), default);
        Assert.Equal(["-p", "--output-format", "json", "--json-schema", StageOutputSchema.Json,
            "--allowedTools", "Read,Write,Bash(dotnet test:*)"], fake.Last.Arguments);
    }

    [Fact]
    public async Task Does_not_trust_a_json_result_string_when_structured_output_absent()
    {
        fake.Returns(0, """{"is_error":false,"result":"{\"status\":\"completed\",\"summary\":\"viaresult\",\"tests_passed\":true}"}""");
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.Null(r.Output);
        Assert.Equal("missing structured output", r.Error);
    }

    [Fact]
    public async Task Raw_log_contains_stdout_and_stderr()
    {
        fake.Returns(0, Fixture("claude-success.json"), "warn");
        var r = await runner.RunAsync(Request(), default);
        Assert.EndsWith("\n--- stderr ---\nwarn", r.RawLog);
        Assert.StartsWith(Fixture("claude-success.json"), r.RawLog);
    }

    [Fact]
    public async Task Failed_status_keeps_output_and_uses_summary_as_error()
    {
        fake.Returns(0, """{"is_error":false,"structured_output":{"status":"failed","summary":"tests broke"}}""");
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.Equal("tests broke", r.Error);
        Assert.Equal("failed", r.Output!.Status);
    }

    [Fact]
    public async Task Error_fixture_reports_result_text()
    {
        fake.Returns(0, Fixture("claude-error.json"));
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.Contains("API Error: overloaded", r.Error);
    }

    [Theory]
    [InlineData("exit-1")]
    [InlineData("is-error")]
    [InlineData("no-structured-output")]
    [InlineData("status-failed")]
    [InlineData("timeout")]
    [InlineData("not-json")]
    public async Task Bad_runs_fail_with_error(string variant)
    {
        switch (variant)
        {
            case "exit-1": fake.Returns(1, Fixture("claude-success.json"), "boom"); break;
            case "is-error": fake.Returns(0, Fixture("claude-error.json")); break;
            case "no-structured-output": fake.Returns(0, """{"is_error":false,"result":"just text"}"""); break;
            case "status-failed": fake.Returns(0, """{"is_error":false,"structured_output":{"status":"failed","summary":"nope"}}"""); break;
            case "timeout": fake.Returns(-1, "", "", timedOut: true); break;
            case "not-json": fake.Returns(0, "garbage"); break;
        }
        var r = await runner.RunAsync(Request(), default);
        Assert.False(r.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        if (variant == "timeout") Assert.Equal("timed out after 7 minutes", r.Error);
        if (variant == "exit-1") Assert.Contains("boom", r.Error);
    }
}
