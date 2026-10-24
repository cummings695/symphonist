using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;

namespace Symphonist.Host.Orchestration;

/// <summary>The prompt values stage runs and gate reviews share, so both see the ticket the same way.</summary>
internal static class PromptValues
{
    public static Dictionary<string, string?> For(ProjectConfig p, Ticket t, SourceTicket? source, string branch,
        string feedback) => new()
    {
        ["ticket.key"] = t.Key,
        ["ticket.title"] = t.Title,
        ["ticket.description"] = source?.Description ?? "",
        ["ticket.comments"] = source is null
            ? ""
            : string.Join("\n", source.Comments.OrderBy(c => c.Created).Select(c => $"{c.Author}: {c.Body}")),
        ["ticket.url"] = source?.WebUrl.ToString() ?? "",
        ["project.name"] = p.Name,
        ["branch"] = branch,
        ["feedback"] = feedback,
        ["spec_path"] = t.SpecPath ?? "",
        ["plan_path"] = t.PlanPath ?? "",
        ["risk.areas"] = GateReviewer.RiskAreasText(p.RiskAreas),
    };
}
