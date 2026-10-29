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
        var stored = await store.ProviderPausesAsync(ct);
        lock (sync)
        {
            pauses.Clear();
            foreach (var p in stored) pauses[p.Key] = p;
        }
        Changed?.Invoke();
    }

    /// <summary>True when a job for <paramref name="key"/> may start now; lifts a pause whose reset has passed.</summary>
    public async Task<bool> TryStartAsync(string key, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        ProviderPause? probed = null;
        var lifted = false;
        bool allowed;
        lock (sync)
        {
            if (!pauses.TryGetValue(key, out var pause)) return true;
            if (pause.ResetsAt is { } resets)
            {
                allowed = now >= resets;
                if (allowed) lifted = pauses.Remove(key);
            }
            else if (pause.NextProbeAt is not { } next || now >= next)
            {
                allowed = true;
                pauses[key] = probed = pause with { NextProbeAt = now + ProbeInterval };
            }
            else allowed = false;
        }

        if (lifted)
        {
            await store.RemoveProviderPauseAsync(key, ct);
            Resumed?.Invoke(key);
            Changed?.Invoke();
        }
        else if (probed is not null)
        {
            await store.SaveProviderPauseAsync(probed, ct);
            Changed?.Invoke();
        }
        return allowed;
    }

    /// <summary>Pauses <paramref name="key"/>, or replaces its pause (keeping when it began).</summary>
    public async Task PauseAsync(string key, UsageLimit limit, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        ProviderPause pause;
        bool isNew;
        lock (sync)
        {
            isNew = !pauses.TryGetValue(key, out var existing);
            pause = new ProviderPause(key, existing?.Since ?? now, limit.ResetsAt,
                limit.ResetsAt is null ? now + ProbeInterval : null);
            pauses[key] = pause;
        }

        await store.SaveProviderPauseAsync(pause, ct);
        if (isNew) Paused?.Invoke(pause);
        Changed?.Invoke();
    }

    /// <summary>A job for <paramref name="key"/> ran, so the provider is usable again.</summary>
    public Task SucceededAsync(string key, CancellationToken ct) => ResumeAsync(key, ct);

    public async Task ResumeAsync(string key, CancellationToken ct)
    {
        bool removed;
        lock (sync) removed = pauses.Remove(key);
        if (!removed) return;

        await store.RemoveProviderPauseAsync(key, ct);
        Resumed?.Invoke(key);
        Changed?.Invoke();
    }
}
