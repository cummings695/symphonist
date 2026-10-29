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

    private const int MaxNameLength = 100;
    private const string PartSuffix = ".part";

    /// <summary>The host directory holding the ticket's attachments.</summary>
    public static string DirectoryFor(string projectId, string key) => SymphonistPaths.Resolve("attachments", projectId, key);

    /// <summary>
    /// <c>&lt;id&gt;-&lt;file name&gt;</c>, keeping only letters, digits, <c>.</c>, <c>-</c> and <c>_</c> (anything else becomes
    /// <c>_</c>), with no <c>..</c>, no leading or trailing dots, and the file name part capped at 100 characters.
    /// </summary>
    public static string LocalName(SourceAttachment a)
    {
        var name = Sanitize(a.FileName);
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd('.');
        return $"{Sanitize(a.Id)}-{(name.Length == 0 ? "file" : name)}";
    }

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

        Directory.CreateDirectory(dir);
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

        foreach (var file in Directory.EnumerateFiles(dir))
            if (!keep.Contains(Path.GetFileName(file))) TryDelete(() => File.Delete(file));
        foreach (var sub in Directory.EnumerateDirectories(dir))
            TryDelete(() => Directory.Delete(sub, true));

        return new AttachmentSet(dir, entries);
    }

    /// <summary>Removes the ticket's attachment directory; no-op when it does not exist.</summary>
    public static void Delete(string projectId, string key)
    {
        var dir = DirectoryFor(projectId, key);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>Downloads via a <c>.part</c> file and renames it into place; returns the error message on failure.</summary>
    private static async Task<string?> DownloadAsync(ITracker tracker, string key, SourceAttachment a, string path,
        CancellationToken ct)
    {
        var part = path + PartSuffix;
        try
        {
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                await tracker.DownloadAttachmentAsync(key, a, stream, ct);
            File.Move(part, path, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            TryDelete(() => File.Delete(part));
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            return e.Message;
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
