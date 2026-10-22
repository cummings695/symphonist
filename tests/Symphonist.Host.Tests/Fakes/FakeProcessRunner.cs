using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Fakes;

/// <summary>Records every process launch; never starts a real process (so tests never touch Docker).</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Lock sync = new();
    private readonly List<ProcessSpec> calls = [];

    /// <summary>Result per spec; default: exit 1 (e.g. "docker is not available").</summary>
    public Func<ProcessSpec, ProcessResult> OnRun { get; set; } = _ => new ProcessResult(1, "", "not available", false);

    public IReadOnlyList<ProcessSpec> Calls
    {
        get { lock (sync) return calls.ToList(); }
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        lock (sync) calls.Add(spec);
        return Task.FromResult(OnRun(spec));
    }
}
