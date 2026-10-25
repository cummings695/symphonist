using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Tests.Dashboard;

public sealed class MarkdownViewTests
{
    private static IDocument Parse(string markdown) =>
        new HtmlParser().ParseDocument("<body>" + MarkdownView.ToHtml(markdown) + "</body>");

    /// <summary>Every attribute value in the output, for checks that no URL or handler survives anywhere.</summary>
    private static IEnumerable<(string Name, string Value)> Attributes(IDocument doc) =>
        doc.Body!.QuerySelectorAll("*").SelectMany(e => e.Attributes.Select(a => (a.Name, a.Value)));

    private static void AssertNoActiveContent(IDocument doc)
    {
        Assert.Empty(doc.Body!.QuerySelectorAll("script, img, iframe, object, embed, style, link, svg, video, audio, source, form, input, base, meta"));
        Assert.DoesNotContain(Attributes(doc), a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Attributes(doc), a => a.Name is "src" or "srcset" or "style" or "background");
        foreach (var a in doc.Body.QuerySelectorAll("a"))
        {
            var href = a.GetAttribute("href") ?? "";
            Assert.True(href.Length == 0 || href.StartsWith('#') || href.StartsWith("http://", StringComparison.Ordinal)
                || href.StartsWith("https://", StringComparison.Ordinal) || href.StartsWith("mailto:", StringComparison.Ordinal),
                $"unexpected href {href}");
        }
    }

    [Fact]
    public void Renders_headings_lists_code_blocks_and_tables()
    {
        var doc = Parse("""
            # Plan

            ## Steps

            - one
            - two

            1. first
            2. second

            Some `inline` and **bold** text.

            ```csharp
            var x = "<b>";
            ```

            | Step | Owner |
            |------|-------|
            | a    | b     |
            """);

        Assert.Equal("Plan", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Steps", doc.QuerySelector("h2")!.TextContent);
        Assert.Equal(["one", "two"], doc.QuerySelectorAll("ul > li").Select(l => l.TextContent));
        Assert.Equal(["first", "second"], doc.QuerySelectorAll("ol > li").Select(l => l.TextContent));
        Assert.Equal("inline", doc.QuerySelector("p code")!.TextContent);
        Assert.Equal("bold", doc.QuerySelector("strong")!.TextContent);
        var code = doc.QuerySelector("pre > code")!;
        Assert.Equal("var x = \"<b>\";\n", code.TextContent);
        Assert.Empty(code.QuerySelectorAll("b"));
        Assert.Equal(["Step", "Owner"], doc.QuerySelectorAll("table thead th").Select(t => t.TextContent));
        Assert.Equal(["a", "b"], doc.QuerySelectorAll("table tbody td").Select(t => t.TextContent));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<div onclick=\"alert(1)\">block</div>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<style>body{background:url(https://evil.example/x)}</style>")]
    [InlineData("text <img src=x onerror=alert(1)> inline")]
    [InlineData("text <a href=\"javascript:alert(1)\">x</a> inline")]
    [InlineData("<!-- comment --> <svg onload=alert(1)>")]
    public void Raw_html_is_escaped_as_text(string markdown)
    {
        var html = MarkdownView.ToHtml(markdown);
        var doc = Parse(markdown);

        AssertNoActiveContent(doc);
        Assert.Empty(doc.Body!.QuerySelectorAll("div, a"));
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[click](file:///etc/passwd)")]
    [InlineData("[click](<java\tscript:alert(1)>)")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("[click](//evil.example/x)")]
    [InlineData("[click](../other.md)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<data:text/html,hi>")]
    public void Unsafe_links_become_plain_text(string markdown)
    {
        var doc = Parse(markdown);

        AssertNoActiveContent(doc);
        Assert.Empty(doc.Body!.QuerySelectorAll("a"));
    }

    [Fact]
    public void Unsafe_link_keeps_its_text()
    {
        var doc = Parse("See [the docs](javascript:alert(1)) now.");
        Assert.Equal("See the docs now.", doc.QuerySelector("p")!.TextContent);
    }

    [Theory]
    [InlineData("[site](https://example.com/a?b=1)", "https://example.com/a?b=1")]
    [InlineData("[site](http://example.com)", "http://example.com")]
    [InlineData("[mail](mailto:dev@example.com)", "mailto:dev@example.com")]
    [InlineData("[below](#steps)", "#steps")]
    [InlineData("<https://example.com/auto>", "https://example.com/auto")]
    public void Safe_links_are_kept_and_open_in_a_new_tab(string markdown, string href)
    {
        var a = Assert.Single(Parse(markdown).QuerySelectorAll("a"));
        Assert.Equal(href, a.GetAttribute("href"));
        if (!href.StartsWith('#'))
        {
            Assert.Equal("_blank", a.GetAttribute("target"));
            Assert.Equal("noopener noreferrer", a.GetAttribute("rel"));
        }
    }

    [Theory]
    [InlineData("![a diagram](https://evil.example/track.png)", "a diagram")]
    [InlineData("![local](docs/diagram.png)", "local")]
    [InlineData("![pixel](data:image/png;base64,iVBORw0KGgo=)", "pixel")]
    [InlineData("![x *styled* alt](javascript:alert(1))", "x styled alt")]
    [InlineData("![ref image][img]\n\n[img]: https://evil.example/r.png", "ref image")]
    public void Images_are_not_loaded_and_show_their_alt_text(string markdown, string alt)
    {
        var doc = Parse(markdown);

        AssertNoActiveContent(doc);
        Assert.Equal(alt, doc.QuerySelector("p")!.TextContent);
        Assert.DoesNotContain("evil.example", doc.Body!.InnerHtml);
    }

    [Fact]
    public void Linked_image_keeps_the_link_with_the_alt_text()
    {
        var doc = Parse("[![badge](https://evil.example/b.svg)](https://example.com/build)");

        AssertNoActiveContent(doc);
        var a = Assert.Single(doc.QuerySelectorAll("a"));
        Assert.Equal("https://example.com/build", a.GetAttribute("href"));
        Assert.Equal("badge", a.TextContent);
    }

    [Fact]
    public void Link_titles_and_code_info_strings_are_escaped()
    {
        var doc = Parse("[t](https://example.com \"a\\\" onmouseover=\\\"alert(1)\")\n\n```js\" onclick=\"alert(1)\nx\n```");

        AssertNoActiveContent(doc);
        Assert.Equal("a\" onmouseover=\"alert(1)", doc.QuerySelector("a")!.GetAttribute("title"));
    }
}
