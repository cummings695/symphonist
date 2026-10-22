using System.Buffers.Text;
using System.Security.Cryptography;

namespace Symphonist.Core;

/// <summary>The shared secret that local API clients (the CLI) send to the host in <see cref="Header"/>.</summary>
public static class ApiToken
{
    public const string Header = "X-Symphonist-Token";

    /// <summary>Reads the token without creating it; null when the file is missing or empty (the host never ran).</summary>
    public static string? TryLoad(string path) =>
        File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null;

    /// <summary>
    /// Reads the token from <paramref name="path"/>, creating it first if missing or empty: 32 random bytes,
    /// base64url, in a file only the current user can read and write.
    /// </summary>
    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing)
        {
            // The file may predate the mode check or have been loosened since: keep it readable by this user only.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(token);
        // UnixCreateMode applies only to newly created files; also tighten a pre-existing empty file.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }
}
