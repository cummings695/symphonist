using System.Collections.Concurrent;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Secrets;

/// <summary>Secrets held by this process only and lost when it exits (<c>SYMPHONIST_SECRET_STORE=memory</c>).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> values = new(StringComparer.Ordinal);

    public string? Get(string name) =>
        SecretStores.Override(name) ?? (values.TryGetValue(name, out var value) && value.Length > 0 ? value : null);

    public bool IsOverridden(string name) => SecretStores.Override(name) is not null;

    public void Set(string name, string value) => values[name] = value;

    public void Remove(string name) => values.TryRemove(name, out _);
}
