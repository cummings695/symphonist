using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Symphonist.Host.Dashboard;

/// <summary>A file read from a ticket's workspace: its text, or why it is not shown.</summary>
/// <param name="PathRefused">The stored path itself was refused as unsafe (so nothing should be built from it).</param>
public sealed record WorkspaceFileResult(string? Text, string? Problem, bool PathRefused = false);

/// <summary>
/// Reads files the agent wrote into a ticket's workspace (<c>{workRoot}/{project}/{ticket}</c>, as
/// <see cref="Symphonist.Integrations.Git.GitWorkspace"/> lays it out) for the dashboard, read-only.
/// </summary>
/// <remarks>
/// The path comes from the agent, so it is treated as hostile: it must be relative, without <c>..</c> segments, control
/// characters, <c>:</c> (drive letters, NTFS streams), segments ending in a dot or space, or Windows device names, and must
/// resolve inside the workspace. Neither the file nor any folder from the workspace down to it may be a link (which could
/// point anywhere on the host). The agent may still be running in the workspace and could swap a folder or the file for a
/// link after those checks, so what is checked comes from the opened file itself, and the text is read from that same
/// handle: on Windows the real path of the open handle must be inside the workspace's real path; on Linux and macOS the
/// file is opened one component at a time with <c>openat(O_NOFOLLOW)</c> from the workspace folder, so no link is ever
/// followed (Linux also checks the handle's real path).
/// </remarks>
public sealed class WorkspaceFiles(string workRoot, ILogger<WorkspaceFiles>? log = null)
{
    public const int MaxBytes = 1024 * 1024;

    internal const string Unsafe = "The stored path is not a safe path inside the ticket's workspace, so the file is not shown.";
    internal const string Linked = "The file, or a folder on its path, is a symbolic link, so it is not shown.";
    internal const string HardLinked = "The file has more than one hard link, so it is not shown.";
    internal const string CannotVerify = "The file's location cannot be verified on this system, so it is not shown.";
    private const string Missing = "The file is not in the ticket's workspace.";
    private const string Unreadable = "The file could not be read.";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }),
         .. new[] { "¹", "²", "³" }.SelectMany(d => new[] { "COM" + d, "LPT" + d })],
        StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspaceFileResult> ReadAsync(string projectId, string ticketKey, string relativePath, CancellationToken ct)
    {
        if (!IsSafeName(projectId) || !IsSafeName(ticketKey) || !IsSafeRelative(relativePath, out var segments))
            return new(null, Unsafe, PathRefused: true);

        var root = Path.GetFullPath(workRoot);
        var workspace = Path.GetFullPath(Path.Combine(root, projectId, ticketKey));
        if (!IsInside(root, workspace)) return new(null, Unsafe, PathRefused: true);
        if (!Directory.Exists(workspace))
            return new(null, "The ticket's workspace is not on this machine (it is prepared when the ticket's first agent runs).");

        var full = Path.GetFullPath(Path.Combine([workspace, .. segments]));
        if (!IsInside(workspace, full)) return new(null, Unsafe, PathRefused: true);

        try
        {
            // Every folder from the workspace itself down to the file, then the file: none may be a link. (A first,
            // readable answer; the check on the open handle below is the one that holds against a swap meanwhile.)
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

            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
                handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            else if (OpenConfined(workspace, segments, out var opened) is { } openProblem) return new(null, openProblem);
            else handle = opened!;

            await using var stream = new FileStream(handle, FileAccess.Read, 4096, handle.IsAsync);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                if (RealPath(workspace) is not { } realWorkspace) return new(null, CannotVerify);
                if (CheckOpened(handle, realWorkspace) is { } problem) return new(null, problem);
            }

            // Read at most one byte past the limit, in case the file grew since it was measured.
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
            if (read > MaxBytes) return new(null, TooLarge);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return new(text.StartsWith('\uFEFF') ? text[1..] : text, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, Missing); // removed meanwhile
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The message names host paths: log it, show a generic one.
            log?.LogWarning(e, "Reading {Path} from the workspace of {Project}/{Key} failed", relativePath, projectId, ticketKey);
            return new(null, Unreadable);
        }
    }

    private static string TooLarge => $"The file is too large to show (over {MaxBytes / (1024 * 1024)} MB).";

    /// <summary>
    /// Why the open file must not be shown, or null when its real location is inside <paramref name="realWorkspace"/> and
    /// (where the system tells) it has no other hard link.
    /// </summary>
    internal static string? CheckOpened(SafeFileHandle handle, string realWorkspace)
    {
        if (RealPathOf(handle) is not { } real) return CannotVerify;
        if (!IsInside(realWorkspace, real)) return Linked;
        // A second hard link could be a file outside the workspace. Only Windows is checked: the link count on Linux
        // and macOS needs fstat, whose struct layout differs per platform. (Container agents cannot link to host files.)
        if (OperatingSystem.IsWindows() && Windows.LinkCount(handle) is not 1) return HardLinked;
        return null;
    }

    /// <summary>The real path (links resolved) of an open file, or null when this system cannot tell.</summary>
    internal static string? RealPathOf(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows()) return Windows.FinalPath(handle);
        if (OperatingSystem.IsLinux())
            return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget is { } target && Path.IsPathRooted(target)
                ? target
                : null;
        // macOS would need fcntl(F_GETPATH), whose third argument is variadic: never called (unsafe on Apple arm64).
        return null;
    }

    /// <summary>
    /// Linux and macOS: opens <paramref name="segments"/> under <paramref name="workspace"/> one component at a time with
    /// <c>openat(O_NOFOLLOW)</c>, so a link anywhere (the workspace folder included) fails the open instead of being
    /// followed, whatever is swapped meanwhile. Returns why it is not shown, or null with the open file in
    /// <paramref name="handle"/>.
    /// </summary>
    internal static string? OpenConfined(string workspace, string[] segments, out SafeFileHandle? handle)
    {
        handle = null;
        if (Unix.Flags() is not { } f) return CannotVerify;
        var fds = new List<int>();
        try
        {
            int fd;
            try
            {
                fd = Unix.open(workspace, f.ReadOnly | f.Directory | f.NoFollow | f.CloseOnExec);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return CannotVerify;
            }
            if (fd < 0) return Unix.Problem(Marshal.GetLastPInvokeError(), f);
            fds.Add(fd);
            for (var i = 0; i < segments.Length; i++)
            {
                var last = i == segments.Length - 1;
                // The file itself without O_DIRECTORY, and non-blocking so a FIFO cannot hang the open.
                var flags = f.ReadOnly | f.NoFollow | f.CloseOnExec | (last ? f.NonBlock : f.Directory);
                var next = Unix.openat(fds[^1], segments[i], flags);
                if (next < 0) return Unix.Problem(Marshal.GetLastPInvokeError(), f);
                if (last)
                {
                    handle = new SafeFileHandle(next, ownsHandle: true);
                    return null;
                }
                fds.Add(next);
            }
            return Missing; // no segments: unreachable after validation
        }
        finally
        {
            foreach (var open in fds) Unix.close(open);
        }
    }

    /// <summary>The real path (links resolved) of an existing file or directory, or null when it cannot be resolved.</summary>
    internal static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = Windows.OpenForAttributes(path);
            return handle.IsInvalid ? null : Windows.FinalPath(handle);
        }
        return Unix.RealPath(path);
    }

    /// <summary>One path segment (a project id or ticket key) that cannot climb or name a root.</summary>
    private static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\']) < 0 && IsSafeSegment(name);

    private static bool IsSafeRelative(string path, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(path) || path[0] is '/' or '\\' || Path.IsPathRooted(path)) return false;
        segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(IsSafeSegment);
    }

    /// <summary>
    /// No control characters or ':'; nothing ending in '.' or a space (Windows drops those, so <c>".. "</c> is <c>..</c>);
    /// no Windows device name, with or without an extension (<c>NUL.md</c>).
    /// </summary>
    private static bool IsSafeSegment(string segment) =>
        segment.Trim().Length > 0 && !segment.Any(c => char.IsControl(c) || c == ':')
        && !segment.EndsWith('.') && !segment.EndsWith(' ')
        && !DeviceNames.Contains(segment.Split('.')[0].TrimEnd());

    private static bool IsInside(string parent, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>A symbolic link (even a dangling one), junction or other reparse point.</summary>
    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static class Windows
    {
        private const uint FileReadAttributes = 0x80;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000; // required to open a directory

        public static SafeFileHandle OpenForAttributes(string path) =>
            CreateFileW(path, FileReadAttributes, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics, IntPtr.Zero);

        /// <summary>The normalized DOS path of the handle, without the <c>\\?\</c> prefix; null on failure.</summary>
        public static string? FinalPath(SafeFileHandle handle)
        {
            var buffer = new char[512];
            while (true)
            {
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0); // normalized, DOS volume name
                if (length == 0) return null;
                if (length < buffer.Length)
                {
                    var path = new string(buffer, 0, (int)length);
                    return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
                        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
                        : path;
                }
                buffer = new char[length];
            }
        }

        /// <summary>The number of hard links to the open file, or 0 when unknown.</summary>
        public static uint LinkCount(SafeFileHandle handle) =>
            GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }

    private static class Unix
    {
        public sealed record OpenFlags(int ReadOnly, int Directory, int NoFollow, int CloseOnExec, int NonBlock, int Loop);

        /// <summary>The open(2) flags and ELOOP of this system, or null where they are not known (fail closed).</summary>
        public static OpenFlags? Flags()
        {
            if (OperatingSystem.IsMacOS()) return new(0, 0x100000, 0x100, 0x1000000, 0x4, 62);
            if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64)
                return new(0, 0x10000, 0x20000, 0x80000, 0x800, 40);
            return null;
        }

        private const int NoEntry = 2, AccessDenied = 13, NotDirectory = 20;

        /// <summary>
        /// Why a component could not be opened. A link fails O_NOFOLLOW with ELOOP (ENOTDIR when a directory was asked
        /// for); ENOTDIR also covers a file where a folder should be, which the name checks report as missing first.
        /// </summary>
        public static string Problem(int errno, OpenFlags f) =>
            errno == f.Loop || errno == NotDirectory ? Linked
            : errno == NoEntry ? Missing
            : errno == AccessDenied ? Unreadable
            : CannotVerify;

        // open and openat are variadic (a mode follows the flags, only with O_CREAT). Declared with fixed arguments only
        // and never given a mode, they are ABI-safe on Apple arm64 too, where only variadic arguments go on the stack.
        [DllImport("libc", ExactSpelling = true, SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", ExactSpelling = true, SetLastError = true)]
        public static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", ExactSpelling = true, SetLastError = true)]
        public static extern int close(int fd);

        public static string? RealPath(string path)
        {
            var resolved = realpath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUTF8(resolved);
            }
            finally
            {
                free(resolved);
            }
        }

        [DllImport("libc", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

        [DllImport("libc", ExactSpelling = true)]
        private static extern void free(IntPtr pointer);
    }
}
