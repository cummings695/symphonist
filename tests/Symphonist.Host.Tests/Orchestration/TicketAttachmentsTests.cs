using Symphonist.Core.Integrations;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;

namespace Symphonist.Host.Tests.Orchestration;

[Collection(SymphonistHomeCollection.Name)]
public sealed class TicketAttachmentsTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T10:00:00Z");

    private readonly TestHome home = new();
    private readonly FakeTracker tracker = new();
    private int downloads;

    public TicketAttachmentsTests()
    {
        tracker.OnDownload = async (a, s, ct) =>
        {
            downloads++;
            if (!tracker.AttachmentContent.TryGetValue(a.Id, out var bytes))
                throw new HttpRequestException($"attachment {a.Id} not found");
            await s.WriteAsync(bytes, ct);
        };
    }

    public void Dispose() => home.Dispose();

    private string Dir => TicketAttachments.DirectoryFor("orders", "ORD-1");

    private SourceAttachment Add(string id, string name, string content, int minutes = 0)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        tracker.AttachmentContent[id] = bytes;
        return new SourceAttachment(id, name, "text/plain", bytes.Length, Now.AddMinutes(minutes));
    }

    private static SourceAttachment Meta(string id, string name, long size, int minutes = 0) =>
        new(id, name, "text/plain", size, Now.AddMinutes(minutes));

    private static SourceTicket Ticket(params SourceAttachment[] attachments) =>
        new("ORD-1", "Add export", "", "Selected", [], new Uri("https://jira.example/browse/ORD-1"), attachments);

    private Task<AttachmentSet> Sync(SourceTicket? source, CancellationToken ct = default) =>
        TicketAttachments.SyncAsync(tracker, "orders", "ORD-1", source, ct);

    private string[] FilesOnDisk() =>
        Directory.Exists(Dir) ? Directory.GetFiles(Dir).Select(f => Path.GetFileName(f)).Order().ToArray() : [];

    [Fact]
    public async Task Downloads_new_attachments_and_lists_them_available()
    {
        var csv = Add("10002", "orders.csv", "id,total\n1,9\n");
        var png = Add("10003", "screen.png", "png-bytes", minutes: 1);

        var set = await Sync(Ticket(csv, png));

        Assert.Equal(Path.Combine(home.Path, "attachments", "orders", "ORD-1"), set.Directory);
        Assert.True(set.HasFiles);
        Assert.Collection(set.Entries,
            e => Assert.Equal(new AttachmentEntry("orders.csv", "text/plain", csv.Size, AttachmentState.Available,
                "10002-orders.csv", null), e),
            e => Assert.Equal(new AttachmentEntry("screen.png", "text/plain", png.Size, AttachmentState.Available,
                "10003-screen.png", null), e));
        Assert.Equal("id,total\n1,9\n", File.ReadAllText(Path.Combine(Dir, "10002-orders.csv")));
        Assert.Equal(["10002-orders.csv", "10003-screen.png"], FilesOnDisk());
    }

    [Fact]
    public async Task Keeps_existing_file_without_redownloading()
    {
        var csv = Add("10002", "orders.csv", "abc");
        await Sync(Ticket(csv));
        downloads = 0;

        var set = await Sync(Ticket(csv));

        Assert.Equal(0, downloads);
        Assert.Equal(AttachmentState.Available, Assert.Single(set.Entries).State);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(Dir, "10002-orders.csv")));
    }

    [Fact]
    public async Task Deletes_files_no_longer_on_the_ticket()
    {
        var csv = Add("10002", "orders.csv", "abc");
        var old = Add("10001", "old.txt", "gone");
        await Sync(Ticket(old, csv));
        File.WriteAllText(Path.Combine(Dir, "10009-stray.txt.part"), "partial");

        await Sync(Ticket(csv));

        Assert.Equal(["10002-orders.csv"], FilesOnDisk());
    }

    [Fact]
    public async Task Skips_file_over_25_MB_with_reason()
    {
        var big = Meta("10002", "dump.sql", TicketAttachments.MaxFileBytes + 1);
        tracker.AttachmentContent["10002"] = [1, 2, 3];

        var set = await Sync(Ticket(big));

        var entry = Assert.Single(set.Entries);
        Assert.Equal(AttachmentState.Skipped, entry.State);
        Assert.Null(entry.LocalName);
        Assert.Contains("25 MB", entry.Reason);
        Assert.False(set.HasFiles);
        Assert.Equal(0, downloads);
        Assert.Empty(FilesOnDisk());
    }

    [Fact]
    public async Task Skips_once_ticket_total_exceeds_100_MB()
    {
        // Listed newest first to prove limits apply oldest-first.
        var files = Enumerable.Range(1, 5).Reverse()
            .Select(i => Meta($"1000{i}", $"part{i}.bin", 24 * MB, minutes: i)).ToArray();
        foreach (var f in files) tracker.AttachmentContent[f.Id] = [1];

        var set = await Sync(Ticket(files));

        Assert.Equal(["part1.bin", "part2.bin", "part3.bin", "part4.bin", "part5.bin"], set.Entries.Select(e => e.FileName));
        Assert.All(set.Entries.Take(4), e => Assert.Equal(AttachmentState.Available, e.State));
        var last = set.Entries[4];
        Assert.Equal(AttachmentState.Skipped, last.State);
        Assert.Contains("100 MB", last.Reason);
        Assert.Equal(4, downloads);
    }

    [Fact]
    public async Task Skips_beyond_50_files()
    {
        var files = Enumerable.Range(0, TicketAttachments.MaxFiles + 2)
            .Select(i => Add($"{20000 + i}", $"f{i}.txt", "x", minutes: i)).ToArray();

        var set = await Sync(Ticket(files));

        Assert.Equal(TicketAttachments.MaxFiles, set.Entries.Count(e => e.State == AttachmentState.Available));
        Assert.All(set.Entries.Skip(TicketAttachments.MaxFiles), e =>
        {
            Assert.Equal(AttachmentState.Skipped, e.State);
            Assert.Contains("50", e.Reason);
        });
        Assert.Equal(TicketAttachments.MaxFiles, FilesOnDisk().Length);
    }

    [Fact]
    public async Task Failed_download_is_unavailable_and_others_still_download()
    {
        var missing = Meta("10001", "missing.txt", 3);
        var csv = Add("10002", "orders.csv", "abc", minutes: 1);

        var set = await Sync(Ticket(missing, csv));

        Assert.Equal(AttachmentState.Unavailable, set.Entries[0].State);
        Assert.Null(set.Entries[0].LocalName);
        Assert.Equal("attachment 10001 not found", set.Entries[0].Reason);
        Assert.Equal(AttachmentState.Available, set.Entries[1].State);
        Assert.Equal(["10002-orders.csv"], FilesOnDisk());
    }

    [Fact]
    public async Task Cancelled_download_leaves_no_file_and_next_sync_retries()
    {
        var csv = Add("10002", "orders.csv", "abcdef");
        using var cts = new CancellationTokenSource();
        var normal = tracker.OnDownload;
        tracker.OnDownload = async (_, s, _) =>
        {
            await s.WriteAsync(new byte[] { 1, 2, 3 });
            await cts.CancelAsync();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync(Ticket(csv), cts.Token));
        Assert.Empty(FilesOnDisk());

        tracker.OnDownload = normal;
        var set = await Sync(Ticket(csv));

        Assert.Equal(AttachmentState.Available, Assert.Single(set.Entries).State);
        Assert.Equal("abcdef", File.ReadAllText(Path.Combine(Dir, "10002-orders.csv")));
    }

    [Fact]
    public async Task Null_source_leaves_directory_untouched_and_lists_disk()
    {
        var csv = Add("10002", "orders.csv", "abc");
        await Sync(Ticket(csv));
        downloads = 0;

        var set = await Sync(null);

        Assert.Equal(0, downloads);
        Assert.Equal(new AttachmentEntry("10002-orders.csv", "application/octet-stream", 3, AttachmentState.Available,
            "10002-orders.csv", null), Assert.Single(set.Entries));
        Assert.Equal(["10002-orders.csv"], FilesOnDisk());
    }

    [Fact]
    public async Task Null_source_with_no_directory_lists_nothing()
    {
        var set = await Sync(null);

        Assert.Empty(set.Entries);
        Assert.False(set.HasFiles);
        Assert.False(Directory.Exists(Dir));
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("a\nb.csv")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData(".env")]
    [InlineData("...")]
    [InlineData("")]
    public void Local_name_is_safe(string fileName) => AssertSafe(fileName);

    [Fact]
    public void Local_name_of_long_file_name_is_capped() => AssertSafe(new string('a', 300));

    private void AssertSafe(string fileName)
    {
        var name = TicketAttachments.LocalName(Meta("10002", fileName, 1));

        Assert.StartsWith("10002-", name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain("..", name);
        Assert.DoesNotContain('\n', name);
        Assert.True(name.Length <= 100 + "10002-".Length, $"{name.Length} chars");
        var dir = Dir + Path.DirectorySeparatorChar;
        Assert.StartsWith(dir, Path.GetFullPath(Path.Combine(dir, name)));
    }

    [Fact]
    public async Task Same_file_name_twice_gets_distinct_local_names()
    {
        var a = Add("10002", "orders.csv", "first");
        var b = Add("10003", "orders.csv", "second", minutes: 1);

        var set = await Sync(Ticket(a, b));

        Assert.NotEqual(set.Entries[0].LocalName, set.Entries[1].LocalName);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Dir, set.Entries[0].LocalName!)));
        Assert.Equal("second", File.ReadAllText(Path.Combine(Dir, set.Entries[1].LocalName!)));
    }

    [Fact]
    public async Task Delete_removes_the_directory_and_ignores_a_missing_one()
    {
        await Sync(Ticket(Add("10002", "orders.csv", "abc")));

        TicketAttachments.Delete("orders", "ORD-1");
        TicketAttachments.Delete("orders", "ORD-1");

        Assert.False(Directory.Exists(Dir));
    }
}
