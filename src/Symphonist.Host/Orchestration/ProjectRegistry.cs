using System.Text.Json;
using Symphonist.Core;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations;

namespace Symphonist.Host.Orchestration;

/// <param name="ProjectIds">The projects now loaded.</param>
/// <param name="Errors">Each skipped file (by file name) and why it was skipped.</param>
public sealed record ProjectReload(IReadOnlyList<string> ProjectIds, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// The configured projects (<c>$SYMPHONIST_HOME/projects/*.yaml</c>) and which of them are paused
/// (<c>$SYMPHONIST_HOME/paused.json</c>, a JSON array of project ids).
/// </summary>
public sealed class ProjectRegistry
{
    private readonly ILogger<ProjectRegistry> log;
    private readonly ProviderRegistry providers;
    private readonly Lock sync = new();
    private readonly HashSet<string> paused;
    private IReadOnlyList<ProjectConfig> projects = [];
    private IReadOnlyDictionary<string, string> files = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> loadErrors = new Dictionary<string, string>();

    public ProjectRegistry(ILogger<ProjectRegistry> log, ProviderRegistry? providers = null)
    {
        this.log = log;
        this.providers = providers ?? BuiltInProviders.Registry;
        paused = LoadPaused();
        Reload();
    }

    public IReadOnlyList<ProjectConfig> Projects => Volatile.Read(ref projects);

    /// <summary>Why each skipped project file (by file name) was skipped at the last <see cref="Reload"/>.</summary>
    public IReadOnlyDictionary<string, string> LoadErrors => Volatile.Read(ref loadErrors);

    /// <summary>Raised after every <see cref="Reload"/>, whether or not anything changed.</summary>
    public event Action? ProjectsChanged;

    public ProjectConfig Get(string id) =>
        Projects.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"Project '{id}' not found");

    /// <summary>The full path of the file declaring the loaded project <paramref name="projectId"/>, or null.</summary>
    public string? FileOf(string projectId) => Volatile.Read(ref files).GetValueOrDefault(projectId);

    /// <summary>
    /// Reloads all project files and swaps the loaded projects in one step; work already started keeps the configuration
    /// it started with. A file that cannot be read or is invalid is skipped, as are all files that declare the same
    /// project id (which of them is meant is ambiguous); the other projects still load.
    /// </summary>
    public ProjectReload Reload()
    {
        ProjectReload result;
        lock (sync)
        {
            result = LoadAll();
        }
        ProjectsChanged?.Invoke();
        return result;
    }

    private ProjectReload LoadAll()
    {
        var dir = Path.GetFullPath(SymphonistPaths.Resolve("projects"));
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = new List<(ProjectConfig Config, string File)>();
        if (!Directory.Exists(dir))
        {
            log.LogWarning("Projects directory {Directory} does not exist; no projects loaded", dir);
        }
        else
        {
            foreach (var file in ProjectFiles(dir))
            {
                var name = Path.GetFileName(file);
                try
                {
                    loaded.Add((ProjectConfigLoader.Parse(File.ReadAllText(file), dir, name, providers), file));
                }
                catch (Exception e) when (e is not OutOfMemoryException) // one bad file must never stop the host
                {
                    // The loader prefixes its errors with the file name, which the error is already keyed by.
                    var message = e.Message.StartsWith($"{name}: ", StringComparison.Ordinal)
                        ? e.Message[(name.Length + 2)..]
                        : e.Message;
                    errors[name] = message;
                    log.LogError("Skipping project file {File}: {Error}", file, message);
                }
            }

            foreach (var duplicate in loaded.GroupBy(p => p.Config.Id).Where(g => g.Count() > 1).ToList())
            {
                log.LogError("Skipping project id '{Id}': declared by {Count} project files", duplicate.Key, duplicate.Count());
                foreach (var (_, file) in duplicate)
                    errors[Path.GetFileName(file)] = $"project id '{duplicate.Key}' is declared by {duplicate.Count()} files";
                loaded.RemoveAll(p => p.Config.Id == duplicate.Key);
            }
        }

        Volatile.Write(ref files, loaded.ToDictionary(p => p.Config.Id, p => p.File, StringComparer.Ordinal));
        Volatile.Write(ref loadErrors, errors);
        Volatile.Write(ref projects, loaded.Select(p => p.Config).ToList());
        return new ProjectReload(loaded.Select(p => p.Config.Id).ToList(), errors);
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
