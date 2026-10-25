using Microsoft.Extensions.Hosting;

namespace Symphonist.Host.Tests.Fakes;

/// <summary>A host lifetime whose stopping token tests can trigger.</summary>
public sealed class FakeHostLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => stopping.Cancel();

    public void Dispose() => stopping.Dispose();
}
