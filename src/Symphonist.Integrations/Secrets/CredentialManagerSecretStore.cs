using GitCredentialManager;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Secrets;

/// <summary>
/// Secrets in the OS credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service, via the Git
/// Credential Manager store packaged as Devlooped.CredentialManager) in namespace <c>symphonist</c>, service
/// <c>https://symphonist</c> (GCM requires the service to be an absolute URI), account = name.
/// An environment variable <c>SYMPHONIST_SECRET_&lt;NAME&gt;</c> (upper case, '-' as '_') overrides the store, for CI
/// and headless machines; the OS store is not touched when the override is set.
/// </summary>
public sealed class CredentialManagerSecretStore : ISecretStore
{
    private const string Namespace = "symphonist";
    private const string Service = "https://symphonist";

    private readonly Lazy<ICredentialStore> store = new(() => CredentialManager.Create(Namespace));

    public static string EnvironmentVariable(string name) =>
        "SYMPHONIST_SECRET_" + name.ToUpperInvariant().Replace('-', '_');

    public string? Get(string name)
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentVariable(name));
        if (!string.IsNullOrEmpty(overridden)) return overridden;
        return store.Value.Get(Service, name)?.Password is { Length: > 0 } stored ? stored : null;
    }

    /// <summary>True when <c>SYMPHONIST_SECRET_&lt;NAME&gt;</c> is set (non-empty), so <see cref="Get"/> ignores the store.</summary>
    public bool IsOverridden(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable(name)));

    public void Set(string name, string value) => store.Value.AddOrUpdate(Service, name, value);

    public void Remove(string name) => store.Value.Remove(Service, name);
}
