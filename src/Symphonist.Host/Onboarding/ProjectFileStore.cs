using Symphonist.Core;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Storage;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Onboarding;

/// <param name="Edited">The form as the developer saves it.</param>
/// <param name="Original">The form as loaded from <paramref name="ExistingFile"/> (null for a new project).</param>
/// <param name="ExistingFile">Null for a new project.</param>
/// <param name="LoadedText">The file text the screen loaded (null for a new project); a different current text refuses the save.</param>
public sealed record ProjectSaveRequest(ProjectForm Edited, ProjectForm? Original, string? ExistingFile, string? LoadedText);

/// <param name="Saved">True when the file was written and the project loaded.</param>
/// <param name="Errors">Why the save was refused; or, with <paramref name="File"/> set and nothing else wrong, why the
/// written file did not load.</param>
/// <param name="File">The project file's full path once written.</param>
public sealed record ProjectSaveResult(bool Saved, IReadOnlyList<string> Errors, string? File);

/// <summary>
/// Writes project files for the setup screen. Only text that passes the form's validation and loads with
/// <see cref="ProjectConfigLoader"/> is written, atomically (temp file, then a move), keeping the previous file as
/// <c>.bak</c>; then the projects are reloaded. Everything from reading the file to the reload runs under the
/// orchestrator's lock, so no tick or command moves a ticket into a stage between the check and the reload.
/// </summary>
public sealed class ProjectFileStore(ProjectRegistry registry, ITicketStore store, Orchestrator orchestrator, ProviderRegistry providers)
{
    private const string ByHand = "edit this file by hand";

    public async Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken ct)
    {
        var errors = request.Edited.Validate(providers);
        if (errors.Count > 0) return Refused(errors);
        if (request.ExistingFile is not null && request.Original is null)
            throw new ArgumentException("an existing file is saved with the form it was loaded as", nameof(request));

        var result = await orchestrator.WithProjectsLockedAsync(() => SaveLockedAsync(request, ct), ct);
        if (result.Saved) orchestrator.RequestTick();
        return result;
    }

    private async Task<ProjectSaveResult> SaveLockedAsync(ProjectSaveRequest request, CancellationToken ct)
    {
        var edited = request.Edited;
        var directory = Path.GetFullPath(SymphonistPaths.Resolve("projects"));
        string file;
        string? current = null;
        string text;
        if (request.ExistingFile is { } existing)
        {
            file = Path.GetFullPath(existing);
            if (!string.Equals(Path.GetDirectoryName(file), directory,
                    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return Refused($"'{existing}' is not a project file in {directory}");
            if (edited.Id != request.Original!.Id) return Refused("the project id cannot be changed");
            try
            {
                current = File.ReadAllText(file);
                // Before Update: a changed file may not splice, and that message would hide the real reason.
                if (current != request.LoadedText)
                    return Refused("the file changed on disk since you opened it; reload the page");
                text = ProjectYamlWriter.Update(current, request.Original, edited, providers);
            }
            catch (ProjectYamlEditException e)
            {
                return Refused(e.Message.EndsWith(ByHand, StringComparison.Ordinal) ? e.Message : $"{e.Message}; {ByHand}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Refused($"cannot read {Path.GetFileName(file)}: {e.Message}");
            }
        }
        else
        {
            file = Path.Combine(directory, $"{edited.Id}.yaml");
            try
            {
                text = ProjectYamlWriter.NewFile(edited, providers);
            }
            catch (ProjectYamlEditException e)
            {
                return Refused($"{e.Message}; {ByHand}");
            }
        }

        var fileName = Path.GetFileName(file);
        ProjectConfig config;
        try
        {
            // The exact text about to be written.
            config = ProjectConfigLoader.Parse(text, directory, fileName, providers);
        }
        catch (ProjectConfigException e)
        {
            return Refused(e.Message);
        }

        if (request.ExistingFile is null)
        {
            if (registry.Projects.Any(p => p.Id == config.Id) || File.Exists(file))
                return Refused($"project '{config.Id}' already exists");
            // Ids differing only by case would share folders and files on Windows and macOS.
            var similar = registry.Projects.FirstOrDefault(p => string.Equals(p.Id, config.Id, StringComparison.OrdinalIgnoreCase))?.Id
                ?? (Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory).Select(Path.GetFileName)
                        .FirstOrDefault(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase))
                    : null);
            if (similar is not null)
                return Refused($"project '{config.Id}' differs only by case from the existing '{similar}'; pick another id");
        }

        var stages = config.Pipeline.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var stranded = (await store.ListAsync(config.Id, ct))
            .Where(t => t.Status != StageStatus.Done && !stages.Contains(t.StageId))
            .GroupBy(t => t.StageId)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"stage '{g.Key}' is removed but active tickets are at it: "
                + string.Join(", ", g.Select(t => t.Key).Order(StringComparer.Ordinal)))
            .ToList();
        if (stranded.Count > 0) return Refused(stranded);

        var promptErrors = PromptErrors(config);
        if (promptErrors.Count > 0) return Refused(promptErrors);

        var temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temp, text, ct);
            if (request.ExistingFile is not null) File.Copy(file, file + ".bak", overwrite: true);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temp);
            if (e is OperationCanceledException) throw;
            return Refused($"cannot write {fileName}: {e.Message}");
        }

        registry.Reload();
        return registry.LoadErrors.TryGetValue(fileName, out var loadError)
            ? new ProjectSaveResult(false, [loadError], file)
            : new ProjectSaveResult(true, [], file);
    }

    private static List<string> PromptErrors(ProjectConfig config)
    {
        var errors = new List<string>();
        foreach (var kind in config.PromptOverrides.Keys)
            Check(() => PromptLibrary.Get(config, kind), $"prompts.{kind.ToString().ToLowerInvariant()}");
        if (config.Review.PromptPath is not null) Check(() => PromptLibrary.GetReview(config), "review.prompt");
        return errors;

        void Check(Func<string> load, string field)
        {
            try
            {
                load();
            }
            catch (PromptTemplateException e)
            {
                errors.Add($"{field}: {e.Message}");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: never loaded (not *.yaml) and overwritten by the next save.
        }
    }

    private static ProjectSaveResult Refused(params IReadOnlyList<string> errors) => new(false, errors, null);
}
