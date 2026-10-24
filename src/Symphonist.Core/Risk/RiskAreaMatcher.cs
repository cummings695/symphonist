using Microsoft.Extensions.FileSystemGlobbing;
using Symphonist.Core.Config;

namespace Symphonist.Core.Risk;

public static class RiskAreaMatcher
{
    /// <summary>(area name, file) for every changed file matching an area's Paths; '/'-separated, case-insensitive globs.</summary>
    public static IReadOnlyList<(string Area, string File)> Touched(IReadOnlyList<RiskArea> areas, IEnumerable<string> files)
    {
        var normalised = files.Select(f => f.Trim().Replace('\\', '/').TrimStart('/')).Where(f => f.Length > 0).ToList();
        var touched = new List<(string, string)>();
        foreach (var area in areas)
        {
            if (area.Paths.Count == 0) continue;
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var raw in area.Paths)
            {
                var path = raw.Trim().Replace('\\', '/');
                if (path.Length == 0) continue;
                matcher.AddInclude(path);
                // A plain directory (or file) path with no wildcard also covers everything beneath it.
                if (path.IndexOfAny(['*', '?', '[']) < 0) matcher.AddInclude(path.TrimEnd('/') + "/**");
            }
            foreach (var file in normalised)
                if (matcher.Match(file).HasMatches) touched.Add((area.Name, file));
        }
        return touched;
    }
}
