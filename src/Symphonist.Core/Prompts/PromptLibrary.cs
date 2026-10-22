using System.Reflection;
using Symphonist.Core.Config;

namespace Symphonist.Core.Prompts;

public static class PromptLibrary
{
    /// <summary>Returns the project's override file for the stage kind if configured, else the embedded default. The result is validated.</summary>
    public static string Get(ProjectConfig project, StageKind kind)
    {
        string template;
        if (project.PromptOverrides.TryGetValue(kind, out var path))
        {
            try { template = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new PromptTemplateException($"cannot read prompt override '{path}': {e.Message}");
            }
        }
        else
        {
            template = ReadDefault(kind) ?? throw new PromptTemplateException($"no prompt template for stage kind '{kind}'");
        }
        PromptRenderer.Validate(template);
        return template;
    }

    private static string? ReadDefault(StageKind kind)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Defaults.{kind.ToString().ToLowerInvariant()}.md", StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
