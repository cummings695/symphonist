namespace Symphonist.Host.Dashboard;

/// <summary>Raises <see cref="Changed"/> at most once per <see cref="Window"/>, on the trailing edge of a burst of <see cref="Notify"/> calls.</summary>
public sealed class BoardNotifier(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private readonly Lock sync = new();
    private ITimer? timer;
    private bool armed;

    /// <summary>Each subscriber is invoked separately; one that throws is ignored and the rest still run.</summary>
    public event Action? Changed;

    public void Notify()
    {
        lock (sync)
        {
            if (armed) return;
            armed = true;
            if (timer is null) timer = time.CreateTimer(_ => Raise(), null, Window, Timeout.InfiniteTimeSpan);
            else timer.Change(Window, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raise()
    {
        lock (sync) armed = false;
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception)
            {
                // A faulty subscriber must not stop the others (or the timer thread).
            }
        }
    }
}
