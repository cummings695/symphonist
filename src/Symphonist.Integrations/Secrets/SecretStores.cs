using Symphonist.Core;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Secrets;

/// <summary>
/// Picks the secret store from <c>SYMPHONIST_SECRET_STORE</c>: unset or <c>os</c> = the OS credential store;
/// <c>memory</c> = kept in this process only (development and manual testing: nothing is written anywhere);
/// <c>file</c> = <c>secrets.json</c> under <c>SYMPHONIST_HOME</c>, readable by the current user only. Every store honours
/// the <c>SYMPHONIST_SECRET_&lt;NAME&gt;</c> overrides.
/// </summary>
public static class SecretStores
{
    public const string Variable = "SYMPHONIST_SECRET_STORE";
    public const string FileName = "secrets.json";

    /// <exception cref="InvalidOperationException">An unknown store kind.</exception>
    public static ISecretStore Create(string? kind, string home) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" or "os" => new CredentialManagerSecretStore(),
        "memory" => new MemorySecretStore(),
        "file" => new FileSecretStore(Path.Combine(home, FileName)),
        _ => throw new InvalidOperationException($"{Variable} must be os, memory or file (got '{kind}')"),
    };

    /// <summary><see cref="Create"/> from the environment.</summary>
    public static ISecretStore FromEnvironment() =>
        Create(Environment.GetEnvironmentVariable(Variable), SymphonistPaths.Home);

    /// <summary>The <c>SYMPHONIST_SECRET_&lt;NAME&gt;</c> value overriding <paramref name="name"/>, or null.</summary>
    internal static string? Override(string name) =>
        Environment.GetEnvironmentVariable(CredentialManagerSecretStore.EnvironmentVariable(name)) is { Length: > 0 } value
            ? value
            : null;
}
