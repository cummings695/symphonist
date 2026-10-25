using System.Text.RegularExpressions;
using Symphonist.Core.Onboarding;

namespace Symphonist.Core.Tests.Onboarding;

public class ProjectYamlDocumentTests
{
    private const string Text =
        "# header\n" +
        "id: orders\n" +
        "\n" +
        "tracker:\n" +
        "  type: jira   # inline\n" +
        "  # inside tracker\n" +
        "  jql: x\n" +
        "\n" +
        "# about repo\n" +
        "repo:\n" +
        "  type: bitbucket\n" +
        "\n" +
        "# about pipeline\n" +
        "pipeline:\n" +
        "  - { id: done, kind: done }\n";

    /// The lines of top-level section <paramref name="key"/>, up to the next column-0 key.
    internal static string Section(string text, string key) =>
        Regex.Match(text, $@"(?ms)^{key}:.*?(?=^[A-Za-z]|\z)").Value;

    [Fact]
    public void Keys_are_listed_in_file_order()
    {
        Assert.Equal(["id", "tracker", "repo", "pipeline"], ProjectYamlDocument.Parse(Text).Keys);
    }

    [Fact]
    public void Trailing_comments_stay_with_the_next_section()
    {
        var result = ProjectYamlDocument.Parse(Text).Replace(
            new Dictionary<string, string?> { ["tracker"] = "tracker:\n  type: github-projects\n" });

        Assert.Equal(Text.Replace("  type: jira   # inline\n  # inside tracker\n  jql: x\n", "  type: github-projects\n"), result);
        Assert.Contains("\n\n# about repo\nrepo:", result);
    }

    [Fact]
    public void Removing_a_section_keeps_its_leading_comment()
    {
        var result = ProjectYamlDocument.Parse(Text).Replace(new Dictionary<string, string?> { ["repo"] = null });

        Assert.Equal(Text.Replace("repo:\n  type: bitbucket\n", ""), result);
        Assert.Contains("# about repo\n", result);
    }

    [Fact]
    public void Missing_section_is_appended()
    {
        var result = ProjectYamlDocument.Parse(Text).Replace(
            new Dictionary<string, string?> { ["review"] = "review:\n  mode: off\n", ["build"] = null });

        Assert.Equal(Text + "\nreview:\n  mode: off\n", result);
    }

    [Fact]
    public void Inserted_text_uses_the_files_line_ending()
    {
        var crlf = Text.ReplaceLineEndings("\r\n");

        var result = ProjectYamlDocument.Parse(crlf).Replace(
            new Dictionary<string, string?> { ["id"] = "id: billing\n", ["review"] = "review:\n  mode: off\n" });

        Assert.Equal(crlf.Replace("id: orders", "id: billing") + "\r\nreview:\r\n  mode: off\r\n", result);
    }

    [Fact]
    public void Last_section_without_final_newline_is_replaced()
    {
        var result = ProjectYamlDocument.Parse("id: a\nname: b").Replace(
            new Dictionary<string, string?> { ["name"] = "name: c\n" });

        Assert.Equal("id: a\nname: c\n", result);
    }

    [Fact]
    public void No_changes_keep_the_text()
    {
        Assert.Equal(Text, ProjectYamlDocument.Parse(Text).Replace(new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData("{id: a}")]
    [InlineData("id: &x a\nname: *x")]
    [InlineData("id: a\n---\nid: b")]
    [InlineData("- a\n- b")]
    [InlineData("id: a\nid: b")]
    [InlineData("")]
    public void Unsafe_documents_are_refused(string text)
    {
        Assert.Throws<ProjectYamlEditException>(() => ProjectYamlDocument.Parse(text));
    }
}
