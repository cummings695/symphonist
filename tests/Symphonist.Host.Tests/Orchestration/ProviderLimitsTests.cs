using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Orchestration;

public sealed class ProviderLimitsTests
{
    private const string Claude = "claude-code";
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeTicketStore store = new();
    private readonly CancellationToken ct = CancellationToken.None;

    private ProviderLimits New() => new(store, time);

    [Fact]
    public async Task Not_paused_starts()
    {
        Assert.True(await New().TryStartAsync(Claude, ct));
    }

    [Fact]
    public async Task Known_reset_blocks_until_reset_then_lifts()
    {
        var limits = New();
        var resumed = new List<string>();
        limits.Resumed += resumed.Add;
        await limits.PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(2)), ct);

        time.Advance(TimeSpan.FromMinutes(119));
        Assert.False(await limits.TryStartAsync(Claude, ct));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await limits.TryStartAsync(Claude, ct));
        Assert.Empty(limits.Pauses);
        Assert.Equal([Claude], resumed);
        Assert.Empty(await store.ProviderPausesAsync(ct));
    }

    [Fact]
    public async Task Unknown_reset_lets_one_probe_through_every_30_minutes()
    {
        var limits = New();
        await limits.PauseAsync(Claude, new UsageLimit(null), ct);

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(await limits.TryStartAsync(Claude, ct));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await limits.TryStartAsync(Claude, ct));
        Assert.False(await limits.TryStartAsync(Claude, ct));
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await limits.TryStartAsync(Claude, ct));
    }

    [Fact]
    public async Task Pause_survives_a_restart()
    {
        await New().PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(2)), ct);
        time.Advance(TimeSpan.FromMinutes(5));

        var restarted = New();
        await restarted.LoadAsync(ct);

        Assert.False(await restarted.TryStartAsync(Claude, ct));
        Assert.Single(restarted.Pauses);
    }

    [Fact]
    public async Task Repeat_pause_keeps_since_and_raises_paused_once()
    {
        var limits = New();
        var paused = 0;
        limits.Paused += _ => paused++;
        var since = time.GetUtcNow();
        await limits.PauseAsync(Claude, new UsageLimit(null), ct);
        time.Advance(TimeSpan.FromMinutes(10));
        await limits.PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(1)), ct);

        var pause = Assert.Single(limits.Pauses);
        Assert.Equal(since, pause.Since);
        Assert.Equal(time.GetUtcNow().AddHours(1), pause.ResetsAt);
        Assert.Null(pause.NextProbeAt);
        Assert.Equal(1, paused);
    }

    [Fact]
    public async Task Success_or_manual_resume_lifts_the_pause()
    {
        var limits = New();
        var resumed = 0;
        limits.Resumed += _ => resumed++;
        await limits.PauseAsync(Claude, new UsageLimit(null), ct);
        await limits.SucceededAsync(Claude, ct);
        Assert.Empty(limits.Pauses);

        await limits.PauseAsync(Claude, new UsageLimit(null), ct);
        await limits.ResumeAsync(Claude, ct);
        Assert.Empty(limits.Pauses);
        Assert.Empty(await store.ProviderPausesAsync(ct));
        Assert.Equal(2, resumed);
    }

    [Fact]
    public async Task Other_keys_are_unaffected()
    {
        var limits = New();
        await limits.PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(1)), ct);
        Assert.False(await limits.TryStartAsync(Claude, ct));
        Assert.True(await limits.TryStartAsync("codex:chatgpt", ct));
    }

    [Fact]
    public async Task A_pause_during_a_lift_is_not_lost_from_the_store()
    {
        var limits = New();
        await limits.PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(1)), ct);
        time.Advance(TimeSpan.FromHours(1));

        var hold = store.HoldPauseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lift = limits.TryStartAsync(Claude, ct);
        var repause = limits.PauseAsync(Claude, new UsageLimit(time.GetUtcNow().AddHours(3)), ct);
        await Task.Delay(50);
        hold.SetResult();
        await Task.WhenAll(lift, repause);

        var stored = Assert.Single(await store.ProviderPausesAsync(ct));
        Assert.Equal(limits.Pauses, [stored]);
        Assert.Equal(time.GetUtcNow().AddHours(3), stored.ResetsAt);
    }
}
