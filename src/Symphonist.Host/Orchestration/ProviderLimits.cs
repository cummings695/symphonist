using Symphonist.Core.Agents;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

/// <summary>
/// Which agent providers are held back by a usage limit. State is cached in memory and written through
/// to the store, so a restart keeps the pauses (see <see cref="LoadAsync"/>).
/// </summary>
public sealed class ProviderLimits(ITicketStore store, TimeProvider time)
{
    /// <summary>How often one job may probe a paused provider whose reset time is unknown.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(30);

    // gate serializes whole mutate-and-persist operations so store order matches memory order; sync guards the dictionary for reads.
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock sync = new();
    private readonly Dictionary<string, ProviderPause> pauses = new(StringComparer.Ordinal);

    public event Action<ProviderPause>? Paused;
    public event Action<string>? Resumed;
    public event Action? Changed;

    public IReadOnlyList<ProviderPause> Pauses
    {
        get { lock (sync) return pauses.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(); }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var stored = await store.ProviderPausesAsync(ct);
            lock (sync)
            {
                pauses.Clear();
                foreach (var p in stored) pauses[p.Key] = p;
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    /// <summary>True when a job for <paramref name="key"/> may start now; lifts a pause whose reset has passed.</summary>
    public async Task<bool> TryStartAsync(string key, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lifted = false;
        var probed = false;
        bool allowed;
        await gate.WaitAsync(ct);
        try
        {
            ProviderPause? pause;
            lock (sync) pauses.TryGetValue(key, out pause);
            if (pause is null) return true;

            if (pause.ResetsAt is { } resets)
            {
                allowed = now >= resets;
                if (allowed)
                {
                    await store.RemoveProviderPauseAsync(key, ct);
                    lock (sync) pauses.Remove(key);
                    lifted = true;
                }
            }
            else if (pause.NextProbeAt is not { } next || now >= next)
            {
                allowed = true;
                var probe = pause with { NextProbeAt = now + ProbeInterval };
                await store.SaveProviderPauseAsync(probe, ct);
                lock (sync) pauses[key] = probe;
                probed = true;
            }
            else allowed = false;
        }
        finally { gate.Release(); }

        if (lifted) Resumed?.Invoke(key);
        if (lifted || probed) Changed?.Invoke();
        return allowed;
    }

    /// <summary>Pauses <paramref name="key"/>, or replaces its pause (keeping when it began).</summary>
    public async Task PauseAsync(string key, UsageLimit limit, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        ProviderPause pause;
        bool isNew;
        await gate.WaitAsync(ct);
        try
        {
            ProviderPause? existing;
            lock (sync) isNew = !pauses.TryGetValue(key, out existing);
            pause = new ProviderPause(key, existing?.Since ?? now, limit.ResetsAt,
                limit.ResetsAt is null ? now + ProbeInterval : null);
            await store.SaveProviderPauseAsync(pause, ct);
            lock (sync) pauses[key] = pause;
        }
        finally { gate.Release(); }

        if (isNew) Paused?.Invoke(pause);
        Changed?.Invoke();
    }

    /// <summary>A job for <paramref name="key"/> ran, so the provider is usable again.</summary>
    public Task SucceededAsync(string key, CancellationToken ct) => ResumeAsync(key, ct);

    public async Task ResumeAsync(string key, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            bool present;
            lock (sync) present = pauses.ContainsKey(key);
            if (!present) return;
            await store.RemoveProviderPauseAsync(key, ct);
            lock (sync) pauses.Remove(key);
        }
        finally { gate.Release(); }

        Resumed?.Invoke(key);
        Changed?.Invoke();
    }
}
