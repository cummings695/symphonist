using System.Text;

namespace Symphonist.Host.Dashboard;

/// <summary>A file read from a ticket's workspace: its text, or why it is not shown.</summary>
public sealed record WorkspaceFileResult(string? Text, string? Problem);

/// <summary>
/// Reads files the agent wrote into a ticket's workspace (<c>{workRoot}/{project}/{ticket}</c>, as
/// <see cref="Symphonist.Integrations.Git.GitWorkspace"/> lays it out) for the dashboard, read-only.
/// </summary>
/// <remarks>
/// The path comes from the agent, so it is treated as hostile: it must be relative, without <c>..</c> segments, control
/// characters or <c>:</c> (drive letters, NTFS streams), must resolve inside the workspace, and neither the file nor any
/// folder from the workspace down to it may be a symbolic link or junction (which could point anywhere on the host).
/// </remarks>
public sealed class WorkspaceFiles(string workRoot)
{
    public const int MaxBytes = 1024 * 1024;

    private const string Unsafe = "The stored path is not a safe path inside the ticket's workspace, so the file is not shown.";
    private const string Linked = "The file, or a folder on its path, is a symbolic link, so it is not shown.";
    private const string Missing = "The file is not in the ticket's workspace.";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<WorkspaceFileResult> ReadAsync(string projectId, string ticketKey, string relativePath, CancellationToken ct)
    {
        if (!IsSafeName(projectId) || !IsSafeName(ticketKey) || !IsSafeRelative(relativePath, out var segments))
            return new(null, Unsafe);

        var root = Path.GetFullPath(workRoot);
        var workspace = Path.GetFullPath(Path.Combine(root, projectId, ticketKey));
        if (!IsInside(root, workspace)) return new(null, Unsafe);
        if (!Directory.Exists(workspace))
            return new(null, "The ticket's workspace is not on this machine (it is prepared when the ticket's first agent runs).");

        var full = Path.GetFullPath(Path.Combine([workspace, .. segments]));
        if (!IsInside(workspace, full)) return new(null, Unsafe);

        try
        {
            // Every folder from the workspace itself down to the file, then the file: none may be a link.
            var directory = workspace;
            if (IsLink(new DirectoryInfo(directory))) return new(null, Linked);
            foreach (var segment in segments[..^1])
            {
                directory = Path.Combine(directory, segment);
                var info = new DirectoryInfo(directory);
                if (!info.Exists) return new(null, Missing);
                if (IsLink(info)) return new(null, Linked);
            }

            var file = new FileInfo(full);
            if (IsLink(file)) return new(null, Linked);
            if (!file.Exists) return new(null, Missing);
            if (file.Length > MaxBytes) return new(null, TooLarge);

            // Read at most one byte past the limit, in case the file grew since it was measured.
            await using var stream = new FileStream(full, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, Options = FileOptions.Asynchronous,
            });
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
            if (read > MaxBytes) return new(null, TooLarge);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return new(text.StartsWith('﻿') ? text[1..] : text, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, $"The file could not be read: {e.Message}");
        }
    }

    private static string TooLarge => $"The file is too large to show (over {MaxBytes / (1024 * 1024)} MB).";

    /// <summary>One path segment (a project id or ticket key) that cannot climb or name a root.</summary>
    private static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Any(char.IsControl);

    private static bool IsSafeRelative(string path, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Contains(':')
            || path[0] is '/' or '\\' || Path.IsPathRooted(path))
            return false;
        segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(s => s != ".." && s.Trim().Length > 0);
    }

    private static bool IsInside(string parent, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>A symbolic link (even a dangling one), junction or other reparse point.</summary>
    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
