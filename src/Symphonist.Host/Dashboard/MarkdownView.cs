using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Symphonist.Host.Dashboard;

/// <summary>
/// Renders an agent-written markdown document (a ticket's spec or plan) as HTML for the dashboard.
/// </summary>
/// <remarks>
/// This is the one place the dashboard renders agent text as markup (<c>MarkupString</c>); everything else is plain text.
/// It is safe because the HTML comes only from Markdig's renderer, never from the document:
/// <list type="bullet">
/// <item>raw HTML is disabled, so HTML blocks and inline HTML are written out escaped, as text;</item>
/// <item>the renderer escapes all text, attribute values and URLs;</item>
/// <item>only the pipe-tables extension is enabled (no attributes, no auto-identifiers, nothing that emits raw HTML);</item>
/// <item>links keep only http, https and mailto URLs; any other link (javascript:, data:, relative, #fragment) becomes its plain text;</item>
/// <item>images are never loaded (no external requests, no data URLs): each becomes its alt text.</item>
/// </list>
/// </remarks>
public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        Sanitize(document);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static void Sanitize(MarkdownDocument document)
    {
        // Images first, so a linked image leaves its link holding the alt text.
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            image.InsertBefore(new LiteralInline(PlainText(image)));
            image.Remove();
        }

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
            if (IsSafeUrl(url)) MarkExternal(link);
            else Unwrap(link);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
            if (IsSafeUrl(url)) MarkExternal(autolink);
            else
            {
                autolink.InsertBefore(new LiteralInline(autolink.Url));
                autolink.Remove();
            }
        }
    }

    /// <summary>
    /// http(s) and mailto only. Anything with whitespace or control characters is refused, since browsers strip those
    /// (e.g. <c>java&#9;script:</c>); relative paths would resolve against the dashboard, and #fragments go nowhere
    /// (headings get no ids), so both are refused too.
    /// </summary>
    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Links (all leave the page) open in a new tab without telling the target where they came from.</summary>
    private static void MarkExternal(Inline link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
    }

    /// <summary>Replaces the link with its children (its text, and any formatting inside it).</summary>
    private static void Unwrap(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }
        link.Remove();
    }

    private static string PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content); break;
                case CodeInline code: text.Append(code.Content); break;
                case AutolinkInline auto: text.Append(auto.Url); break;
                case LineBreakInline: text.Append(' '); break;
            }
        }
        return text.ToString();
    }
}
