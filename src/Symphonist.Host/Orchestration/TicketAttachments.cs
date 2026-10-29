using System.Security.Cryptography;
using System.Text;
using Symphonist.Core;
using Symphonist.Core.Integrations;

namespace Symphonist.Host.Orchestration;

/// <summary>Whether an attachment's file is on disk for the agent, and if not, why.</summary>
public enum AttachmentState { Available, Skipped, Unavailable }

/// <summary>One ticket attachment as the agent sees it; <see cref="LocalName"/> is set only when Available.</summary>
public sealed record AttachmentEntry(string FileName, string MimeType, long Size, AttachmentState State,
    string? LocalName, string? Reason);

/// <summary>The ticket's attachment directory and what it holds (or failed to hold).</summary>
public sealed record AttachmentSet(string Directory, IReadOnlyList<AttachmentEntry> Entries)
{
    public bool HasFiles => Entries.Any(e => e.State == AttachmentState.Available);
}

/// <summary>
/// Keeps <c>SYMPHONIST_HOME/attachments/&lt;project&gt;/&lt;key&gt;/</c> in step with a ticket's attachments, within size and
/// count limits. File names come from ticket authors, so local names are sanitized and prefixed with the attachment id.
/// </summary>
public static class TicketAttachments
{
    public const long MaxFileBytes = 25L * 1024 * 1024, MaxTicketBytes = 100L * 1024 * 1024;
    public const int MaxFiles = 50;

    /// <summary>
    /// How long one file's download may take. HTTP timeouts bound only the time to response headers, so a stalled body
    /// would otherwise hold the stage forever. Settable for tests.
    /// </summary>
    internal static TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private const int MaxNameLength = 100, MaxExtensionLength = 20;
    private const string PartSuffix = ".part";

    /// <summary>The host directory holding the ticket's attachments.</summary>
    public static string DirectoryFor(string projectId, string key) => SymphonistPaths.Resolve("attachments", projectId, key);

    /// <summary>
    /// <c>&lt;id&gt;-&lt;file name&gt;</c>, keeping only letters, digits, <c>.</c>, <c>-</c> and <c>_</c> (anything else becomes
    /// <c>_</c>), with no <c>..</c>, no leading or trailing dots, and the file name part capped at 100 characters. A long
    /// name keeps its extension (up to <see cref="MaxExtensionLength"/> characters): Read detects images by it.
    /// </summary>
    public static string LocalName(SourceAttachment a)
    {
        var name = Sanitize(a.FileName);
        if (name.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > MaxExtensionLength) ext = "";
            name = name[..(MaxNameLength - ext.Length)].TrimEnd('.') + ext;
        }
        return $"{Sanitize(a.Id)}-{(name.Length == 0 ? "file" : name)}";
    }

    /// <summary>
    /// <paramref name="text"/> without control characters, so author-controlled text (file names, MIME types) stays on
    /// one line and cannot carry terminal escapes into prompts, events or the CLI.
    /// </summary>
    internal static string OneLine(string text) => string.Concat(text.Where(c => !char.IsControl(c)));

    /// <summary>
    /// Downloads attachments not yet present, deletes files no longer on the ticket, and lists the result. Limits apply
    /// oldest attachment first. A failed download is listed as Unavailable and retried on the next sync. With no
    /// <paramref name="source"/> the directory is left as it is and its files are listed.
    /// </summary>
    public static async Task<AttachmentSet> SyncAsync(ITracker tracker, string projectId, string key, SourceTicket? source,
        CancellationToken ct)
    {
        var dir = DirectoryFor(projectId, key);
        if (source is null) return new AttachmentSet(dir, ListDisk(dir));

        var entries = new List<AttachmentEntry>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var accepted = 0;

        foreach (var a in source.AttachmentList.OrderBy(a => a.Created))
        {
            var reason = a.Size > MaxFileBytes ? $"larger than the {MaxFileBytes / (1024 * 1024)} MB per-file limit"
                : accepted >= MaxFiles ? $"more than {MaxFiles} files on the ticket"
                : total + a.Size > MaxTicketBytes ? $"ticket attachments exceed the {MaxTicketBytes / (1024 * 1024)} MB total limit"
                : null;
            if (reason is not null)
            {
                entries.Add(Entry(a, AttachmentState.Skipped, null, reason));
                continue;
            }

            accepted++;
            total += a.Size;
            var local = LocalName(a);
            var path = Path.Combine(dir, local);
            if (!(File.Exists(path) && new FileInfo(path).Length == a.Size))
            {
                Directory.CreateDirectory(dir); // only when there is something to download
                var error = await DownloadAsync(tracker, key, a, path, ct);
                if (error is not null)
                {
                    entries.Add(Entry(a, AttachmentState.Unavailable, null, error));
                    continue;
                }
            }

            keep.Add(local);
            entries.Add(Entry(a, AttachmentState.Available, local, null));
        }

        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                if (!keep.Contains(Path.GetFileName(file))) TryDelete(() => File.Delete(file));
            foreach (var sub in Directory.EnumerateDirectories(dir))
                TryDelete(() => Directory.Delete(sub, true));
        }

        return new AttachmentSet(dir, entries);
    }

    /// <summary>Every attachment on the ticket listed as Unavailable for <paramref name="reason"/>, oldest first.</summary>
    public static AttachmentSet Unavailable(string projectId, string key, SourceTicket? source, string reason) =>
        new(DirectoryFor(projectId, key), (source?.AttachmentList ?? []).OrderBy(a => a.Created)
            .Select(a => Entry(a, AttachmentState.Unavailable, null, reason)).ToList());

    /// <summary>
    /// The error to fail the stage with when one of <paramref name="paths"/> ('/'-separated, relative to
    /// <paramref name="workspace"/>) is a byte-identical copy of an Available attachment; null otherwise. Empty attachments
    /// are skipped (they would match every empty file), as are paths that are not files inside the workspace.
    /// </summary>
    public static async Task<string?> FindCopyAsync(AttachmentSet set, string workspace, IEnumerable<string> paths,
        CancellationToken ct)
    {
        var attachments = new List<(AttachmentEntry Entry, long Length, byte[] Hash)>();
        foreach (var e in set.Entries)
        {
            if (e is not { State: AttachmentState.Available, LocalName: { } local }) continue;
            var file = new FileInfo(Path.Combine(set.Directory, local));
            if (!file.Exists || file.Length == 0) continue;
            attachments.Add((e, file.Length, await HashAsync(file.FullName, ct)));
        }
        if (attachments.Count == 0) return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, PathComparison)) continue;
            var file = new FileInfo(full);
            if (!file.Exists) continue;

            byte[]? hash = null;
            foreach (var a in attachments)
            {
                if (a.Length != file.Length) continue;
                hash ??= await HashAsync(full, ct);
                if (hash.AsSpan().SequenceEqual(a.Hash))
                    return $"agent copied attachment \"{OneLine(a.Entry.FileName)}\" into {path}; attachments must not be committed";
            }
        }
        return null;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await SHA256.HashDataAsync(stream, ct);
    }

    /// <summary>
    /// Removes the ticket's attachment directory; no-op when it does not exist. Best-effort: a locked file leaves the
    /// directory behind rather than throwing.
    /// </summary>
    public static void Delete(string projectId, string key)
    {
        var dir = DirectoryFor(projectId, key);
        if (Directory.Exists(dir)) TryDelete(() => Directory.Delete(dir, true));
    }

    /// <summary>
    /// Downloads via a <c>.part</c> file and renames it into place; returns the error message on failure. A download
    /// that outlasts <see cref="DownloadTimeout"/> or sends more bytes than the attachment's size fails.
    /// </summary>
    private static async Task<string?> DownloadAsync(ITracker tracker, string key, SourceAttachment a, string path,
        CancellationToken ct)
    {
        var part = path + PartSuffix;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);
        try
        {
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var capped = new CappedStream(stream, a.Size))
                await tracker.DownloadAttachmentAsync(key, a, capped, deadline.Token);
            File.Move(part, path, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            TryDelete(() => File.Delete(part));
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            return e is OperationCanceledException && deadline.IsCancellationRequested
                ? $"download timed out after {DownloadTimeout.TotalMinutes:0.##} minutes"
                : e.Message;
        }
    }

    /// <summary>Passes writes through until they would exceed <c>limit</c> bytes, then throws.</summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count(count);
            inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Count(buffer.Length);
            return inner.WriteAsync(buffer, ct);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Count(int count)
        {
            written += count;
            if (written > limit)
                throw new IOException($"download is larger than the attachment's size of {limit} bytes");
        }
    }

    /// <summary>
    /// Best-effort cleanup: a file that is locked (another sync, antivirus) or otherwise undeletable stays until the next
    /// sync rather than failing the stage.
    /// </summary>
    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<AttachmentEntry> ListDisk(string dir) => !Directory.Exists(dir)
        ? []
        : Directory.EnumerateFiles(dir)
            .Where(f => !f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new AttachmentEntry(f.Name, "application/octet-stream", f.Length, AttachmentState.Available,
                f.Name, null))
            .ToList();

    private static AttachmentEntry Entry(SourceAttachment a, AttachmentState state, string? local, string? reason) =>
        new(a.FileName, a.MimeType, a.Size, state, local, reason);

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var safe = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
            if (safe == '.' && sb.Length > 0 && sb[^1] == '.') continue;
            sb.Append(safe);
        }
        return sb.ToString().Trim('.');
    }
}
