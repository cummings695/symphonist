using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

/// <summary>Secrets in memory; names in <see cref="Overridden"/> report an environment variable override.</summary>
public sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Overridden { get; } = new(StringComparer.Ordinal);

    public string? Get(string name) => Values.GetValueOrDefault(name);

    public void Set(string name, string value) => Values[name] = value;

    public void Remove(string name) => Values.Remove(name);

    public bool IsOverridden(string name) => Overridden.Contains(name);
}
