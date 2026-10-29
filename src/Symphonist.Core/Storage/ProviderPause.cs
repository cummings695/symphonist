namespace Symphonist.Core.Storage;

/// <summary>An agent provider held back after a usage limit; persisted so a restart cannot start jobs early.</summary>
/// <param name="Key">The provider key (see <see cref="Agents.UsageLimits"/>).</param>
/// <param name="ResetsAt">When the provider says the limit lifts; null when unknown.</param>
/// <param name="NextProbeAt">With no known reset, when one job may next probe the provider.</param>
public sealed record ProviderPause(string Key, DateTimeOffset Since, DateTimeOffset? ResetsAt, DateTimeOffset? NextProbeAt);
