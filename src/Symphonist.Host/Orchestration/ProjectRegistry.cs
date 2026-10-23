using System.Text.Json;
using Symphonist.Core;
using Symphonist.Core.Config;

namespace Symphonist.Host.Orchestration;

/// <summary>
/// The configured projects (<c>$SYMPHONIST_HOME/projects/*.yaml</c>) and which of them are paused
/// (<c>$SYMPHONIST_HOME/paused.json</c>, a JSON array of project ids).
/// </summary>
public sealed class ProjectRegistry
{
    private readonly ILogger<ProjectRegistry> log;
    private readonly Lock sync = new();
    private readonly HashSet<string> paused;
    private IReadOnlyList<ProjectConfig> projects = [];

    public ProjectRegistry(ILogger<ProjectRegistry> log)
    {
        this.log = log;
        paused = LoadPaused();
        Reload();
    }

    public IReadOnlyList<ProjectConfig> Projects => Volatile.Read(ref projects);

    public ProjectConfig Get(string id) =>
        Projects.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"Project '{id}' not found");

    /// <summary>
    /// Reloads all project files. A file that cannot be read or is invalid is logged and skipped, as are all files
    /// that declare the same project id (which of them is meant is ambiguous); the other projects still load.
    /// </summary>
    public void Reload()
    {
        var dir = SymphonistPaths.Resolve("projects");
        if (!Directory.Exists(dir))
        {
            log.LogWarning("Projects directory {Directory} does not exist; no projects loaded", dir);
            Volatile.Write(ref projects, []);
            return;
        }

        var loaded = new List<ProjectConfig>();
        foreach (var file in ProjectFiles(dir))
        {
            try
            {
                loaded.Add(ProjectConfigLoader.Load(file));
            }
            catch (Exception e) when (e is not OutOfMemoryException) // one bad file must never stop the host
            {
                log.LogError("Skipping project file {File}: {Error}", file, e.Message);
            }
        }

        foreach (var duplicate in loaded.GroupBy(p => p.Id).Where(g => g.Count() > 1).ToList())
        {
            log.LogError("Skipping project id '{Id}': declared by {Count} project files", duplicate.Key, duplicate.Count());
            loaded.RemoveAll(p => p.Id == duplicate.Key);
        }
        Volatile.Write(ref projects, loaded);
    }

    private IReadOnlyList<string> ProjectFiles(string dir)
    {
        try
        {
            return ProjectConfigLoader.ProjectFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError("Cannot list project files in {Directory}: {Error}", dir, e.Message);
            return [];
        }
    }

    public bool IsPaused(string id)
    {
        lock (sync) return paused.Contains(id);
    }

    /// <summary>Raised by <see cref="SetPaused"/> only when the paused set actually changed.</summary>
    public event Action? PausedChanged;

    public void SetPaused(string id, bool paused)
    {
        lock (sync)
        {
            var changed = paused ? this.paused.Add(id) : this.paused.Remove(id);
            if (!changed) return;
            Directory.CreateDirectory(SymphonistPaths.Home);
            File.WriteAllText(PausedPath, JsonSerializer.Serialize(this.paused.Order(StringComparer.Ordinal)));
        }
        PausedChanged?.Invoke();
    }

    private static string PausedPath => SymphonistPaths.Resolve("paused.json");

    private HashSet<string> LoadPaused()
    {
        if (!File.Exists(PausedPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(PausedPath)) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Ignoring unreadable {Path}; no projects paused", PausedPath);
            return [];
        }
    }
}
