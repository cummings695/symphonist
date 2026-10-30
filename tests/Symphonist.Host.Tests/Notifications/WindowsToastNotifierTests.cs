using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Host.Notifications;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Notifications;

public sealed class WindowsToastNotifierTests
{
    private const string Script = "C:\\app\\Notifications\\toast.ps1";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Toast_runs_powershell_with_json_on_stdin()
    {
        var runner = new FakeProcessRunner { OnRun = _ => new ProcessResult(0, "", "", false) };
        var notifier = new WindowsToastNotifier(runner, Script, NullLogger<WindowsToastNotifier>.Instance);

        await notifier.ShowAsync(new DesktopNotification("ORD-1 is waiting on you", "Orders · plan",
            new Uri("http://127.0.0.1:5199/tickets/orders/ORD-1")), CancellationToken.None);

        var spec = Assert.Single(runner.Calls);
        Assert.Equal("powershell.exe", spec.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script], spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("waiting on you") || a.Contains("Orders"));
        using var json = JsonDocument.Parse(spec.StandardInput!);
        Assert.Equal("ORD-1 is waiting on you", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("Orders · plan", json.RootElement.GetProperty("body").GetString());
        Assert.Equal("http://127.0.0.1:5199/tickets/orders/ORD-1", json.RootElement.GetProperty("link").GetString());
    }

    [Fact]
    public async Task Toast_failure_is_logged_not_thrown()
    {
        var log = new CapturingLogger<WindowsToastNotifier>();
        var runner = new FakeProcessRunner { OnRun = _ => new ProcessResult(1, "", "toast failed: boom", false) };
        await new WindowsToastNotifier(runner, Script, log).ShowAsync(
            new DesktopNotification("t", "b", new Uri("http://127.0.0.1:5199/")), CancellationToken.None);
        Assert.Contains(log.Messages, m => m.Contains("boom"));

        runner.OnRun = _ => throw new InvalidOperationException("no powershell");
        await new WindowsToastNotifier(runner, Script, log).ShowAsync(
            new DesktopNotification("t", "b", new Uri("http://127.0.0.1:5199/")), CancellationToken.None);
        Assert.Contains(log.Messages, m => m.Contains("failed"));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/")]
    [InlineData("http://127.0.0.1.evil.test/")]
    public async Task Non_loopback_link_is_refused(string link)
    {
        var log = new CapturingLogger<WindowsToastNotifier>();
        var runner = new FakeProcessRunner();
        await new WindowsToastNotifier(runner, Script, log).ShowAsync(
            new DesktopNotification("t", "b", new Uri(link)), CancellationToken.None);
        Assert.Empty(runner.Calls);
        Assert.Single(log.Messages);
    }
}
