using System.Text.RegularExpressions;

namespace Symphonist.Core.Prompts;

public sealed class PromptTemplateException(string message) : Exception(message);

public static partial class PromptRenderer
{
    public static readonly IReadOnlySet<string> KnownPlaceholders = new HashSet<string>
    {
        "ticket.key", "ticket.title", "ticket.description", "ticket.comments", "ticket.url", "ticket.attachments",
        "project.name", "branch", "feedback", "spec_path", "plan_path", "risk.areas",
        "gate.stage", "gate.kind", "review.diff_range", "review.changed_files", "review.diff", "review.history",
        "review.stage_summary", "review.fence_id",
        "onboarding.tracker", "onboarding.statuses", "onboarding.pipeline", "onboarding.fence_id",
        "evidence.fence_id", "evidence.diff_range", "evidence.changed_files", "evidence.diff", "evidence.run",
        "evidence.features", "evidence.output_dir",
        "pr.fence_id", "pr.comments", "pr.code",
    };

    [GeneratedRegex(@"\{\{([a-z_.]+)\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Single-pass substitution: inserted values are never re-scanned, so user text cannot inject placeholders.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        PlaceholderPattern().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var value) ? value ?? "" : "");

    public static void Validate(string template)
    {
        foreach (Match m in PlaceholderPattern().Matches(template))
            if (!KnownPlaceholders.Contains(m.Groups[1].Value))
                throw new PromptTemplateException($"unknown placeholder '{m.Value}'");
    }
}
