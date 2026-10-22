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

    /// <summary>Reloads all project files; on an invalid file throws and keeps the previously loaded projects.</summary>
    public void Reload()
    {
        var dir = SymphonistPaths.Resolve("projects");
        if (!Directory.Exists(dir))
        {
            log.LogWarning("Projects directory {Directory} does not exist; no projects loaded", dir);
            Volatile.Write(ref projects, []);
            return;
        }

        var loaded = ProjectConfigLoader.LoadAll(dir);
        var duplicate = loaded.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ProjectConfigException($"{dir}: duplicate project id '{duplicate.Key}'");
        Volatile.Write(ref projects, loaded);
    }

    public bool IsPaused(string id)
    {
        lock (sync) return paused.Contains(id);
    }

    public void SetPaused(string id, bool paused)
    {
        lock (sync)
        {
            var changed = paused ? this.paused.Add(id) : this.paused.Remove(id);
            if (!changed) return;
            Directory.CreateDirectory(SymphonistPaths.Home);
            File.WriteAllText(PausedPath, JsonSerializer.Serialize(this.paused.Order(StringComparer.Ordinal)));
        }
    }

    private static string PausedPath => SymphonistPaths.Resolve("paused.json");

    private HashSet<string> LoadPaused()
    {
        if (!File.Exists(PausedPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(PausedPath)) ?? [];
        }
        catch (JsonException e)
        {
            log.LogWarning(e, "Ignoring unreadable {Path}; no projects paused", PausedPath);
            return [];
        }
    }
}
