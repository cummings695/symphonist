using Symphonist.Core.Config;

namespace Symphonist.Core.Prompts;

public static class PromptLibrary
{
    /// <summary>Returns the project's override file for the stage kind if configured, else the embedded default. The result is validated.</summary>
    public static string Get(ProjectConfig project, StageKind kind) =>
        Load(project.PromptOverrides.GetValueOrDefault(kind), kind.ToString().ToLowerInvariant(),
            $"no prompt template for stage kind '{kind}'");

    /// <summary>Returns the review prompt override (<see cref="ReviewConfig.PromptPath"/>) if configured, else the embedded default. The result is validated.</summary>
    public static string GetReview(ProjectConfig project) =>
        Load(project.Review.PromptPath, "review", "no review prompt template");

    private static string Load(string? overridePath, string defaultName, string missing)
    {
        string template;
        if (overridePath is not null)
        {
            try { template = File.ReadAllText(overridePath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new PromptTemplateException($"cannot read prompt override '{overridePath}': {e.Message}");
            }
        }
        else
        {
            template = ReadDefault(defaultName) ?? throw new PromptTemplateException(missing);
        }
        PromptRenderer.Validate(template);
        return template;
    }

    private static string? ReadDefault(string name)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Defaults.{name}.md", StringComparison.Ordinal));
        if (resource is null) return null;
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
