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

    /// <summary>A directory link: a symbolic link where possible, else (Windows without the privilege) a junction.</summary>
    public static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows() || CanCreateFileSymlinks)
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
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
        foreach (var rooted in new[] { Path.Combine(outside, "secret.md"), "/etc/passwd", @"\secret.md", "C:/secret.md", @"\\server\share\x.md" })
        {
            var result = await Read(rooted);
            Assert.Null(result.Text);
            Assert.Contains("not a safe path", result.Problem);
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
}
