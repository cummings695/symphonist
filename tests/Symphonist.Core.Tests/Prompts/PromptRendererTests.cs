using Symphonist.Core.Config;
using Symphonist.Core.Prompts;

namespace Symphonist.Core.Tests.Prompts;

public class PromptRendererTests
{
    private static ProjectConfig ConfigWithoutOverrides() =>
        ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.yaml"))
            with { PromptOverrides = new Dictionary<StageKind, string>() };

    [Fact]
    public void Renders_known_placeholders_and_blanks_nulls()
        => Assert.Equal("ORD-1: Fix tax / ", PromptRenderer.Render("{{ticket.key}}: {{ticket.title}} / {{feedback}}",
            new Dictionary<string, string?> { ["ticket.key"] = "ORD-1", ["ticket.title"] = "Fix tax", ["feedback"] = null }));

    [Fact]
    public void Does_not_expand_placeholders_inside_values()
        => Assert.Equal("say {{ticket.key}}", PromptRenderer.Render("say {{feedback}}",
            new Dictionary<string, string?> { ["feedback"] = "{{ticket.key}}", ["ticket.key"] = "X" }));

    [Fact]
    public void Validate_rejects_unknown_placeholder()
        => Assert.Contains("unknown placeholder '{{ticket.nope}}'",
            Assert.Throws<PromptTemplateException>(() => PromptRenderer.Validate("{{ticket.nope}}")).Message);

    [Theory, InlineData(StageKind.Triage), InlineData(StageKind.Plan), InlineData(StageKind.Implement)]
    public void Embedded_defaults_exist_and_validate(StageKind kind)
        => Assert.Contains("{{ticket.key}}", PromptLibrary.Get(ConfigWithoutOverrides(), kind));

    [Fact]
    public void Missing_default_without_override_throws()
        => Assert.Equal("no prompt template for stage kind 'Build'",
            Assert.Throws<PromptTemplateException>(() => PromptLibrary.Get(ConfigWithoutOverrides(), StageKind.Build)).Message);

    [Fact]
    public void Override_file_wins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphonist-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "custom {{ticket.key}}");
        try
        {
            var config = ConfigWithoutOverrides() with
            {
                PromptOverrides = new Dictionary<StageKind, string> { [StageKind.Plan] = path }
            };
            Assert.Equal("custom {{ticket.key}}", PromptLibrary.Get(config, StageKind.Plan));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Override_file_with_unknown_placeholder_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphonist-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "bad {{nope}}");
        try
        {
            var config = ConfigWithoutOverrides() with
            {
                PromptOverrides = new Dictionary<StageKind, string> { [StageKind.Plan] = path }
            };
            Assert.Throws<PromptTemplateException>(() => PromptLibrary.Get(config, StageKind.Plan));
        }
        finally { File.Delete(path); }
    }
}
