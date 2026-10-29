namespace Symphonist.Host.Notifications;

/// <param name="Link">Opened when the notification is clicked; only <c>http://127.0.0.1:{port}/…</c> is ever shown.</param>
public sealed record DesktopNotification(string Title, string Body, Uri Link);

public interface INotifier
{
    /// <summary>Shows the notification. Implementations never throw; they log instead.</summary>
    Task ShowAsync(DesktopNotification notification, CancellationToken ct);
}
