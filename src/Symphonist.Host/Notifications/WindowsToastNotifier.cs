using System.Text.Json;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Notifications;

/// <summary>Shows a Windows toast by running <c>toast.ps1</c>; title, body and link go in on standard input, never as arguments.</summary>
public sealed class WindowsToastNotifier(IProcessRunner runner, string scriptPath, ILogger<WindowsToastNotifier> log) : INotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task ShowAsync(DesktopNotification notification, CancellationToken ct)
    {
        try
        {
            var link = notification.Link;
            if (link.Scheme != Uri.UriSchemeHttp || link.Host != "127.0.0.1")
            {
                log.LogWarning("Refusing a notification whose link is not on 127.0.0.1: {Link}", link.GetLeftPart(UriPartial.Authority));
                return;
            }
            var json = JsonSerializer.Serialize(new { title = notification.Title, body = notification.Body, link = link.AbsoluteUri });
            var spec = new ProcessSpec("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                StandardInput: json, Timeout: Timeout);
            var result = await runner.RunAsync(spec, ct);
            if (result.TimedOut) log.LogWarning("The toast script timed out");
            else if (result.ExitCode != 0)
                log.LogWarning("The toast script failed with exit code {Code}: {Error}", result.ExitCode, result.StandardError.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            log.LogWarning(e, "Showing a desktop notification failed");
        }
    }
}
