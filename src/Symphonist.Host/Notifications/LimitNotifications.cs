using Symphonist.Core.Agents;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Notifications;

/// <summary>Posts a notification when a provider is paused by a usage limit and when its jobs run again.</summary>
public static class LimitNotifications
{
    public static void Subscribe(ProviderLimits limits, NotificationCenter center, TimeProvider time, TimeZoneInfo zone)
    {
        limits.Paused += pause => center.Post(new NotificationEvent(NotificationKind.LimitPaused, null, null,
            UsageLimitText.Paused(pause.Key, pause.ResetsAt, zone, time.GetUtcNow()), UsageLimits.ProviderName(pause.Key)));
        limits.Resumed += key => center.Post(new NotificationEvent(NotificationKind.LimitResumed, null, null,
            "Jobs are running again", UsageLimits.ProviderName(key)));
    }
}
