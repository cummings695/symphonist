namespace Symphonist.Core.Integrations;

/// <summary>Named secrets (API tokens) kept in the OS credential store, never in config files.</summary>
public interface ISecretStore
{
    /// <summary>The secret's value, or null when it is not set.</summary>
    string? Get(string name);

    void Set(string name, string value);

    void Remove(string name);
}
