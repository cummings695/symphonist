using Symphonist.Core.Risk;
using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Notifications;

public enum NotificationKind { WaitingOnYou, Blocked, ReleaseReady, LimitPaused, LimitResumed }

/// <param name="Provider">Display name of the provider; only used by the limit kinds.</param>
public sealed record NotificationEvent(NotificationKind Kind, string? ProjectId, string? TicketKey, string Line, string? Provider = null);

/// <summary>
/// Collects events for <see cref="FoldWindow"/> after the first one and sends them as a single notification, so ten
/// tickets blocked in one tick are one toast.
/// </summary>
public sealed class NotificationCenter(INotifier notifier, DashboardAddress address, Func<string, string> projectName,
    TimeProvider time, ILogger<NotificationCenter> log) : IDisposable
{
    public static readonly TimeSpan FoldWindow = TimeSpan.FromSeconds(30);
    private const int MaxLine = 200;

    private readonly Lock sync = new();
    private readonly List<NotificationEvent> pending = [];
    private readonly HashSet<(NotificationKind, string?, string?, string?)> seen = [];
    private ITimer? timer;
    private bool disposed;

    public void Post(NotificationEvent e)
    {
        try
        {
            lock (sync)
            {
                if (disposed || !seen.Add((e.Kind, e.ProjectId, e.TicketKey, e.Provider))) return;
                pending.Add(e);
                timer ??= time.CreateTimer(_ => _ = FlushAsync(), null, FoldWindow, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Queueing a notification failed");
        }
    }

    public async Task FlushAsync()
    {
        List<NotificationEvent> batch;
        lock (sync)
        {
            timer?.Dispose();
            timer = null;
            batch = [.. pending];
            pending.Clear();
            seen.Clear();
        }
        if (batch.Count == 0) return;
        try
        {
            await notifier.ShowAsync(Compose(batch, address, projectName), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sending a notification failed");
        }
    }

    public static DesktopNotification Compose(IReadOnlyList<NotificationEvent> events, DashboardAddress address,
        Func<string, string> projectName)
    {
        if (events.Count == 1)
        {
            var e = events[0];
            var provider = e.Provider ?? "Provider";
            var title = e.Kind switch
            {
                NotificationKind.WaitingOnYou => $"{e.TicketKey} is waiting on you",
                NotificationKind.Blocked => $"{e.TicketKey} is blocked",
                NotificationKind.ReleaseReady => $"{(e.ProjectId is null ? "A project" : projectName(e.ProjectId))} has work ready to release",
                NotificationKind.LimitPaused => $"{provider} usage limit reached",
                _ => $"{provider} jobs resumed",
            };
            var line = OneLine(e.Line);
            var body = e.ProjectId is null ? line : $"{projectName(e.ProjectId)} · {line}";
            Uri link;
            if (e.Kind is NotificationKind.WaitingOnYou or NotificationKind.Blocked && e.ProjectId is not null && e.TicketKey is not null)
                link = new Uri(address.Url, $"/tickets/{Uri.EscapeDataString(e.ProjectId)}/{Uri.EscapeDataString(e.TicketKey)}");
            else if (e.Kind == NotificationKind.ReleaseReady && e.ProjectId is not null)
                link = new Uri(address.Url, $"/?project={Uri.EscapeDataString(e.ProjectId)}");
            else
                link = new Uri(address.Url, "/");
            return new DesktopNotification(title, body, link);
        }

        var allTickets = events.All(e => e.TicketKey is not null);
        var foldedTitle = allTickets ? $"{events.Count} tickets need you" : $"{events.Count} updates from Symphonist";
        var parts = new List<string>();
        void Count(NotificationKind kind, string label)
        {
            var n = events.Count(e => e.Kind == kind);
            if (n > 0) parts.Add($"{n} {label}");
        }
        Count(NotificationKind.WaitingOnYou, "waiting on you");
        Count(NotificationKind.Blocked, "blocked");
        Count(NotificationKind.ReleaseReady, "ready to release");
        Count(NotificationKind.LimitPaused, "usage limit");
        Count(NotificationKind.LimitResumed, "resumed");
        return new DesktopNotification(foldedTitle, string.Join(" · ", parts), new Uri(address.Url, "/"));
    }

    /// <summary>A notification never carries more than one line: whitespace runs (line breaks too) become one space.</summary>
    private static string OneLine(string text) =>
        ReviewerText.Line(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxLine);

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }
}
