using System.Diagnostics;
using Symphonist.Integrations.Processes;

namespace Symphonist.Integrations.Tests.Processes;

public class ProcessRunnerTests
{
    internal static readonly string SleeperDll = Path.Combine(AppContext.BaseDirectory, "Sleeper", "Sleeper.dll");

    [Fact]
    public async Task Passes_arguments_with_spaces_and_stdin_verbatim()
    {
        var r = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "echo", "a b \"c\""], StandardInput: "in put"), default);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("a b \"c\"|in put", r.StandardOutput.Trim());
    }

    [Fact]
    public async Task Refuses_specs_with_bind_mounts()
    {
        // Mounts only mean something to a container launcher; running such a spec directly would silently drop them.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessRunner().RunAsync(
            new("dotnet", [SleeperDll, "echo", "x"], Mounts: [new BindMount("/tmp/run", "/symphonist-run")]), default));
    }

    [Fact]
    public async Task Times_out_and_kills()
    {
        var sw = Stopwatch.StartNew();
        var r = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "sleep"], Timeout: TimeSpan.FromSeconds(1)), default);
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_kills_and_throws()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "sleep"]), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Large_output_on_both_streams_does_not_deadlock()
    {
        var r = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "flood"], Timeout: TimeSpan.FromSeconds(60)), default);
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.StandardOutput.Length > 1_000_000);
        Assert.True(r.StandardError.Length > 1_000_000);
    }

    [Fact]
    public async Task Environment_sets_and_removes_variables()
    {
        Environment.SetEnvironmentVariable("SYMPH_REMOVE_ME", "present");
        try
        {
            var env = new Dictionary<string, string?> { ["SYMPH_SET_ME"] = "v1", ["SYMPH_REMOVE_ME"] = null };
            var set = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "env", "SYMPH_SET_ME"], Environment: env), default);
            var removed = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "env", "SYMPH_REMOVE_ME"], Environment: env), default);
            Assert.Equal("v1", set.StandardOutput);
            Assert.Equal("<unset>", removed.StandardOutput);
        }
        finally { Environment.SetEnvironmentVariable("SYMPH_REMOVE_ME", null); }
    }
}
