using System.Diagnostics;
using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Tests.Dashboard;

/// <summary>A fact that only runs where this process can create file symbolic links (Windows needs admin or developer mode).</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!WorkspaceLinks.CanCreateFileSymlinks) Skip = "This process cannot create file symbolic links";
    }
}

/// <summary>A fact that only runs on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Only Windows reports the hard link count";
    }
}

/// <summary>Creates links the way an agent could inside a workspace.</summary>
public static class WorkspaceLinks
{
    public static readonly bool CanCreateFileSymlinks = Probe();

    private static bool Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "symphonist-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a"), "a");
            File.CreateSymbolicLink(Path.Combine(dir, "b"), Path.Combine(dir, "a"));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A second hard link to <paramref name="target"/> (Windows; no privilege needed).</summary>
    public static void HardLink(string link, string target) => Cmd("/c", "mklink", "/H", link, target);

    /// <summary>A directory link: a symbolic link where possible, else (Windows without the privilege) a junction.</summary>
    public static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows() || CanCreateFileSymlinks)
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        Cmd("/c", "mklink", "/J", link, target);
    }

    private static void Cmd(params string[] arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }
}

public sealed class WorkspaceFilesTests : IDisposable
{
    private const string Secret = "TOP-SECRET-CONTENT";

    private readonly string root = Path.Combine(Path.GetTempPath(), "symphonist-ws-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string outside;
    private readonly WorkspaceFiles files;

    public WorkspaceFilesTests()
    {
        outside = root + "-outside";
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "secret.md"), Secret);
        File.WriteAllText(Path.Combine(outside, "secret.md"), Secret);
        files = new WorkspaceFiles(Path.Combine(root, "work"));
    }

    private string Workspace => Path.Combine(root, "work", "orders", "ORD-1");

    public void Dispose()
    {
        // Links first, so deleting the tree never follows one.
        foreach (var dir in new[] { root, outside }.Where(Directory.Exists))
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                         .Where(e => e.LinkTarget is not null).ToList())
                entry.Delete();
            Directory.Delete(dir, true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<WorkspaceFileResult> Read(string relative, string project = "orders", string key = "ORD-1") =>
        files.ReadAsync(project, key, relative, default);

    [Fact]
    public async Task Reads_a_file_inside_the_workspace()
    {
        Write("docs/specs/a.md", "# Spec\n");
        var result = await Read("docs/specs/a.md");
        Assert.Null(result.Problem);
        Assert.Equal("# Spec\n", result.Text);
    }

    [Fact]
    public async Task Reads_a_file_with_backslashes_and_a_bom()
    {
        Write("docs/a.md", "\uFEFFhello");
        var result = await Read(@"docs\a.md");
        Assert.Equal("hello", result.Text);
    }

    [Theory]
    [InlineData("../../secret.md")]
    [InlineData("../../../secret.md")]
    [InlineData(@"..\..\secret.md")]
    [InlineData("docs/../../../secret.md")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docs/a.md\0.txt")]
    [InlineData("docs/a\n.md")]
    [InlineData("docs/a.md:stream")]
    [InlineData("docs/.. /../../secret.md")]
    [InlineData(".. /.. /secret.md")]
    [InlineData("docs/... /a.md")]
    [InlineData("docs/a.md.")]
    [InlineData("docs/a.md ")]
    [InlineData("CON")]
    [InlineData("docs/nul.md")]
    [InlineData("docs/NUL")]
    [InlineData("COM1/a.md")]
    [InlineData("docs/lpt9.txt")]
    [InlineData("docs/Aux .md")]
    [InlineData("docs/conin$")]
    [InlineData("docs/CONOUT$.md")]
    [InlineData("docs/COM\u00b9")]
    public async Task Unsafe_paths_are_refused(string relative)
    {
        Write("docs/a.md", "fine");
        var result = await Read(relative);
        Assert.Null(result.Text);
        Assert.Contains("not a safe path", result.Problem);
    }

    [Fact]
    public async Task Rooted_paths_are_refused()
    {
        foreach (var rooted in new[]
                 {
                     Path.Combine(outside, "secret.md"), "/etc/passwd", @"\secret.md", "C:/secret.md", @"\\server\share\x.md",
                     @"\\?\" + Path.Combine(outside, "secret.md"), @"\\.\" + Path.Combine(outside, "secret.md"),
                     @"\\?\UNC\server\share\x.md", @"\\.\CON",
                 })
        {
            var result = await Read(rooted);
            Assert.Null(result.Text);
            Assert.Contains("not a safe path", result.Problem);
            Assert.True(result.PathRefused);
        }
    }

    [Theory]
    [InlineData("..", "ORD-1")]
    [InlineData("orders", "..")]
    [InlineData("orders", "../orders/ORD-1")]
    [InlineData("", "ORD-1")]
    public async Task Unsafe_project_or_key_is_refused(string project, string key)
    {
        Write("docs/a.md", "fine");
        var result = await Read("docs/a.md", project, key);
        Assert.Null(result.Text);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task Missing_file_and_missing_workspace_say_so()
    {
        var missing = await Read("docs/none.md");
        Assert.Null(missing.Text);
        Assert.Contains("not in the ticket's workspace", missing.Problem);

        var noWorkspace = await Read("docs/none.md", key: "ORD-2");
        Assert.Null(noWorkspace.Text);
        Assert.Contains("workspace is not on this machine", noWorkspace.Problem);
    }

    [Fact]
    public async Task A_directory_is_not_a_file()
    {
        Write("docs/a.md", "fine");
        var result = await Read("docs");
        Assert.Null(result.Text);
        Assert.Contains("not in the ticket's workspace", result.Problem);
    }

    [Fact]
    public async Task Oversized_file_is_refused()
    {
        Write("docs/big.md", new string('x', WorkspaceFiles.MaxBytes + 1));
        var result = await Read("docs/big.md");
        Assert.Null(result.Text);
        Assert.Contains("too large to show", result.Problem);

        Write("docs/edge.md", new string('x', WorkspaceFiles.MaxBytes));
        Assert.Equal(WorkspaceFiles.MaxBytes, (await Read("docs/edge.md")).Text!.Length);
    }

    [SymlinkFact]
    public async Task Symlinked_file_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        File.CreateSymbolicLink(Path.Combine(Workspace, "docs", "spec.md"), Path.Combine(outside, "secret.md"));

        var result = await Read("docs/spec.md");
        Assert.Null(result.Text);
        Assert.Contains("symbolic link", result.Problem);
    }

    [Fact]
    public async Task Linked_directory_is_refused()
    {
        WorkspaceLinks.LinkDirectory(Path.Combine(Workspace, "docs"), outside);

        var result = await Read("docs/secret.md");
        Assert.Null(result.Text);
        Assert.Contains("symbolic link", result.Problem);
    }

    [Fact]
    public async Task Linked_workspace_is_refused()
    {
        Directory.Delete(Workspace);
        WorkspaceLinks.LinkDirectory(Workspace, outside);

        var result = await Read("secret.md");
        Assert.Null(result.Text);
        Assert.Contains("symbolic link", result.Problem);
    }

    [Fact]
    public async Task Device_names_inside_other_words_are_fine()
    {
        Write("docs/console.md", "a");
        Write("docs/com10.md", "b");
        Assert.Equal("a", (await Read("docs/console.md")).Text);
        Assert.Equal("b", (await Read("docs/com10.md")).Text);
    }

    private string RealWorkspace => WorkspaceFiles.RealDirectoryPath(Workspace)!;

    [Fact]
    public void Open_file_inside_the_workspace_passes_the_handle_check()
    {
        Write("docs/a.md", "fine");
        using var stream = File.OpenRead(Path.Combine(Workspace, "docs", "a.md"));
        Assert.Null(WorkspaceFiles.CheckOpened(stream.SafeFileHandle, RealWorkspace));
    }

    [Fact]
    public void File_opened_through_a_linked_directory_fails_the_handle_check()
    {
        // As if docs/ were swapped for a link after the name checks: the open handle still shows where the file really is.
        WorkspaceLinks.LinkDirectory(Path.Combine(Workspace, "docs"), outside);
        using var stream = File.OpenRead(Path.Combine(Workspace, "docs", "secret.md"));
        Assert.Equal(WorkspaceFiles.Linked, WorkspaceFiles.CheckOpened(stream.SafeFileHandle, RealWorkspace));
    }

    [SymlinkFact]
    public void File_opened_through_a_symlink_fails_the_handle_check()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        File.CreateSymbolicLink(Path.Combine(Workspace, "docs", "spec.md"), Path.Combine(outside, "secret.md"));
        using var stream = File.OpenRead(Path.Combine(Workspace, "docs", "spec.md"));
        Assert.Equal(WorkspaceFiles.Linked, WorkspaceFiles.CheckOpened(stream.SafeFileHandle, RealWorkspace));
    }

    [Fact]
    public async Task Workspace_under_a_linked_work_root_is_still_readable()
    {
        // A work root under a linked folder (e.g. a home on another drive) is the developer's choice, not the agent's.
        var linkedRoot = root + "-linkedroot";
        WorkspaceLinks.LinkDirectory(linkedRoot, root);
        try
        {
            Write("docs/a.md", "fine");
            var result = await new WorkspaceFiles(Path.Combine(linkedRoot, "work")).ReadAsync("orders", "ORD-1", "docs/a.md", default);
            Assert.Equal("fine", result.Text);
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    [WindowsFact]
    public async Task Hard_linked_file_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        WorkspaceLinks.HardLink(Path.Combine(Workspace, "docs", "spec.md"), Path.Combine(outside, "secret.md"));

        var result = await Read("docs/spec.md");
        Assert.Null(result.Text);
        Assert.Equal(WorkspaceFiles.HardLinked, result.Problem);
    }
}
