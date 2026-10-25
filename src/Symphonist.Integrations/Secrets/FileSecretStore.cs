using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Secrets;

/// <summary>
/// Secrets in one JSON file (<c>SYMPHONIST_SECRET_STORE=file</c>), for machines without a usable OS credential store.
/// The file is written atomically and readable by the current user only: mode 0600 on macOS and Linux; on Windows its
/// content is encrypted for the current user with DPAPI. Anyone who can act as that user can read the secrets.
/// </summary>
public sealed class FileSecretStore(string path) : ISecretStore
{
    private static readonly byte[] Entropy = "symphonist-secrets"u8.ToArray();

    private readonly Lock sync = new();

    public string Path { get; } = path;

    public string? Get(string name)
    {
        if (SecretStores.Override(name) is { } overridden) return overridden;
        lock (sync) return Read().TryGetValue(name, out var value) && value.Length > 0 ? value : null;
    }

    public bool IsOverridden(string name) => SecretStores.Override(name) is not null;

    public void Set(string name, string value)
    {
        lock (sync)
        {
            var values = Read();
            values[name] = value;
            Write(values);
        }
    }

    public void Remove(string name)
    {
        lock (sync)
        {
            var values = Read();
            if (values.Remove(name)) Write(values);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(Path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var bytes = File.ReadAllBytes(Path);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)
            ?? throw new InvalidDataException($"secret file '{Path}' is empty or not a JSON object");
        return new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, string> values)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values));
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temp, options)) stream.Write(bytes);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
