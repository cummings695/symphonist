using System.Globalization;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Orchestration;

/// <summary>The prompt values stage runs and gate reviews share, so both see the ticket the same way.</summary>
internal static class PromptValues
{
    public static Dictionary<string, string?> For(ProjectConfig p, Ticket t, SourceTicket? source, string branch,
        string feedback, AttachmentSet? attachments = null) => new()
    {
        ["ticket.key"] = t.Key,
        ["ticket.title"] = t.Title,
        ["ticket.description"] = source?.Description ?? "",
        ["ticket.comments"] = source is null
            ? ""
            : string.Join("\n", source.Comments.OrderBy(c => c.Created).Select(c => $"{c.Author}: {c.Body}")),
        ["ticket.url"] = source?.WebUrl.ToString() ?? "",
        ["ticket.attachments"] = AttachmentsText(attachments, p.Agent.Isolation),
        ["project.name"] = p.Name,
        ["branch"] = branch,
        ["feedback"] = feedback,
        ["spec_path"] = t.SpecPath ?? "",
        ["plan_path"] = t.PlanPath ?? "",
        ["risk.areas"] = GateReviewer.RiskAreasText(p.RiskAreas),
    };

    /// <summary>
    /// One line per attachment inside a freshly fenced block: where an available file is (the container mount, or the
    /// host directory when the agent runs on the host), or why it is missing. Names, MIME types and reasons come from the
    /// ticket, so control characters are stripped to keep each entry on its own line.
    /// </summary>
    internal static string AttachmentsText(AttachmentSet? set, Isolation isolation)
    {
        if (set is null || set.Entries.Count == 0) return "(none)";

        var lines = set.Entries.Select(e => e.State switch
        {
            AttachmentState.Available =>
                $"{PathFor(set, e.LocalName!, isolation)}  {OneLine(e.MimeType)}  {Kilobytes(e.Size)} KB",
            AttachmentState.Skipped => $"{OneLine(e.FileName)}  skipped: {OneLine(e.Reason ?? "")}",
            _ => $"{OneLine(e.FileName)}  unavailable: {OneLine(e.Reason ?? "")}",
        });
        return UntrustedFence.Wrap(UntrustedFence.NewNonce(), string.Join("\n", lines));
    }

    private static string PathFor(AttachmentSet set, string localName, Isolation isolation) =>
        isolation == Isolation.Container
            ? $"{DockerLauncher.AttachmentsMount}/{localName}"
            : Path.Combine(set.Directory, localName);

    private static string Kilobytes(long size) => ((size + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

    private static string OneLine(string text) => string.Concat(text.Where(c => !char.IsControl(c)));
}
