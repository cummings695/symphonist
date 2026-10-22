using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Integrations.ClaudeCode;
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

    private readonly FakeProcessRunner fake = new();
    private readonly ClaudeCodeRunner runner;

    public ClaudeCodeRunnerTests() => runner = new ClaudeCodeRunner(fake, new HostLauncher());

    private static AgentRunRequest Request(Isolation isolation = Isolation.Container, string[]? tools = null,
        string? model = null) =>
        new("/work", "do the thing",
            new AgentSettings("claude-code", isolation, [], tools ?? [], 7, model, null));

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
    public async Task Falls_back_to_result_string_when_structured_output_absent()
    {
        fake.Returns(0, """{"is_error":false,"result":"{\"status\":\"completed\",\"summary\":\"viaresult\",\"tests_passed\":true}"}""");
        var r = await runner.RunAsync(Request(), default);
        Assert.True(r.Succeeded);
        Assert.Equal("viaresult", r.Output!.Summary);
        Assert.True(r.Output.TestsPassed);
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
