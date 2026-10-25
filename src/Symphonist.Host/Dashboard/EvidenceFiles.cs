using System.Text.RegularExpressions;
using Symphonist.Core;
using Symphonist.Core.Evidence;

namespace Symphonist.Host.Dashboard;

/// <summary>
/// Serves the screenshots <see cref="Orchestration.EvidenceRunner"/> stored under
/// <c>evidence/{project}/{key}/{stamp}/{file}</c> to the signed-in dashboard.
/// </summary>
/// <remarks>
/// Every segment comes from the address bar, so each is checked on its own: the project and key are single safe names,
/// the stamp is exactly the runner's <c>yyyyMMddTHHmmssZ</c>, and the file is a plain image name
/// (<see cref="EvidenceParser.IsScreenshotName"/>, so never SVG or HTML). The resolved path must stay under the evidence
/// folder, and no folder from there down to the file, nor the file, may be a link. Anything else is a 404. The content
/// type comes from the extension and is sent with <c>nosniff</c>, so a browser never reads the bytes as anything else.
/// </remarks>
public static partial class EvidenceFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"\A[0-9]{8}T[0-9]{6}Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex StampName();

    /// <summary>
    /// GET /evidence/{project}/{key}/{stamp}/{file}: sign-in required; 404 unless every segment is a safe name and the
    /// resolved path stays under SymphonistPaths.Resolve("evidence"); image content type by extension; nosniff.
    /// </summary>
    public static WebApplication MapEvidenceFiles(this WebApplication app)
    {
        app.MapGet("/evidence/{project}/{key}/{stamp}/{file}", (string project, string key, string stamp, string file,
                HttpContext context) =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Serve(SymphonistPaths.Resolve("evidence"), project, key, stamp, file);
            })
            .RequireAuthorization();
        return app;
    }

    internal static IResult Serve(string evidenceRoot, string project, string key, string stamp, string file)
    {
        if (Locate(evidenceRoot, project, key, stamp, file) is not { } path || ContentType(file) is not { } type)
            return Results.NotFound();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.File(stream, type);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>The full path of an existing, unlinked stored screenshot, or null.</summary>
    internal static string? Locate(string evidenceRoot, string project, string key, string stamp, string file)
    {
        if (!WorkspaceFiles.IsSafeName(project) || !WorkspaceFiles.IsSafeName(key) || !StampName().IsMatch(stamp)
            || !EvidenceParser.IsScreenshotName(file)) return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(evidenceRoot));
        var path = Path.GetFullPath(Path.Combine(root, project, key, stamp, file));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) return null;

        try
        {
            var current = root;
            foreach (var segment in new[] { project, key, stamp })
            {
                current = Path.Combine(current, segment);
                var dir = new DirectoryInfo(current);
                if (!dir.Exists || IsLink(dir)) return null;
            }
            var info = new FileInfo(path);
            return info.Exists && !IsLink(info) ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null,
    };

    /// <summary>A symbolic link (even a dangling one), junction or other reparse point.</summary>
    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
