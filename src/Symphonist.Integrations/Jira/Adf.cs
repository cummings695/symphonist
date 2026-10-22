using System.Text;
using System.Text.Json;

namespace Symphonist.Integrations.Jira;

/// <summary>Minimal Atlassian Document Format conversion (plain text only).</summary>
public static class Adf
{
    private static readonly HashSet<string> InlineTypes =
        ["text", "hardBreak", "mention", "emoji", "inlineCard", "status", "date"];

    public static string ToPlainText(JsonElement adf)
    {
        if (adf.ValueKind != JsonValueKind.Object) return "";
        return Render(adf).Trim();
    }

    private static string Render(JsonElement node)
    {
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "text":
                return node.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
            case "hardBreak":
                return "\n";
            case "mention":
            case "emoji":
                if (node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("text", out var at))
                    return at.GetString() ?? "";
                return "";
        }

        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var children = content.EnumerateArray().ToList();
        var allInline = children.All(c =>
            c.TryGetProperty("type", out var ct) && ct.GetString() is { } s && InlineTypes.Contains(s));
        if (allInline)
        {
            var sb = new StringBuilder();
            foreach (var c in children) sb.Append(Render(c));
            return sb.ToString();
        }

        return string.Join("\n", children.Select(Render).Where(s => s.Length > 0));
    }

    public static object FromMarkdown(string markdown)
    {
        var blocks = markdown.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.None)
            .Select(b => b.Trim('\n'))
            .Where(b => !string.IsNullOrWhiteSpace(b));

        var paragraphs = new List<object>();
        foreach (var block in blocks)
        {
            var inline = new List<object>();
            var lines = block.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) inline.Add(new { type = "hardBreak" });
                if (lines[i].Length > 0) inline.Add(new { type = "text", text = lines[i] });
            }
            paragraphs.Add(new { type = "paragraph", content = inline });
        }

        return new { type = "doc", version = 1, content = paragraphs };
    }
}
