namespace Symphonist.Host.Notifications;

/// <summary>For platforms without a notifier (and <c>SYMPHONIST_NOTIFICATIONS=off</c>): logs once, shows nothing.</summary>
public sealed class NullNotifier(ILogger<NullNotifier> log) : INotifier
{
    private int logged;

    public Task ShowAsync(DesktopNotification notification, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref logged, 1) == 0)
            log.LogInformation("Desktop notifications are not available on this platform yet");
        return Task.CompletedTask;
    }
}
