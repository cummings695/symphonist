using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Integrations;
using Symphonist.Integrations.GitHub;

namespace Symphonist.Core.Tests.Onboarding;

public class ProjectYamlWriterTests
{
    private static readonly ProviderRegistry Registry = BuiltInProviders.Registry;
    private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

    private static string SamplePath(string name) => Path.Combine(ConfigDir, name);

    private static ProjectConfig Parse(string yaml) => ProjectConfigLoader.Parse(yaml, ConfigDir, "test.yaml", Registry);

    private static ProjectForm Sample(string name) => ProjectForm.From(ProjectConfigLoader.Load(SamplePath(name), Registry), Registry);

    /// A new project with every required provider field filled.
    internal static ProjectForm Filled(string trackerType = "jira", string repoType = "bitbucket")
    {
        var form = ProjectForm.NewProject("orders", Registry, trackerType, repoType);
        Fill(form.Tracker, Registry.Tracker(form.Tracker.Type).Fields);
        Fill(form.Repo, Registry.Repo(form.Repo.Type).Fields);
        return form;
    }

    private static void Fill(ProviderSectionForm section, IReadOnlyList<ProviderField> fields)
    {
        foreach (var field in fields.Where(f => f.Required && !section.Values.ContainsKey(f.Key)))
            section.Values[field.Key] = field.Kind switch
            {
                ProviderFieldKind.Url => "https://acme.atlassian.net",
                ProviderFieldKind.Number => "7",
                ProviderFieldKind.Path => Path.Combine(Path.GetTempPath(), "acme-orders"),
                _ => "acme",
            };
    }

    [Theory]
    [InlineData("jira", "bitbucket")]
    [InlineData("github-projects", "github")]
    [InlineData("jira", "github")]
    public void New_file_loads_and_matches_the_form(string tracker, string repo)
    {
        var form = Filled(tracker, repo);

        var p = Parse(ProjectYamlWriter.NewFile(form, Registry));

        Assert.Equal("orders", p.Id);
        Assert.Equal(tracker, p.Tracker.Type);
        Assert.Equal(repo, p.Repo.Type);
        Assert.Equal(Registry.BuildsFor(repo)[0].Type, p.Build.Type);
        Assert.Equal(["triage", "plan", "implement", "build", "release", "deploy", "done"], p.Pipeline.Select(s => s.Id));
        Assert.Equal(ReviewMode.Off, p.Review.Mode);
        Assert.Equal(Isolation.Container, p.Agent.Isolation);
        Assert.Equal(["api.nuget.org", "registry.npmjs.org"], p.Agent.AllowedDomains);
    }

    [Theory]
    [InlineData("orders.example.yaml")]
    [InlineData("orders-github.example.yaml")]
    public void Rendering_is_idempotent(string sample)
    {
        var form = Sample(sample);
        var again = ProjectForm.From(Parse(ProjectYamlWriter.NewFile(form, Registry)), Registry);

        var first = ProjectYamlWriter.Sections(form, Registry);
        var second = ProjectYamlWriter.Sections(again, Registry);
        Assert.Equal(ProjectYamlWriter.SectionOrder, first.Keys);
        foreach (var key in ProjectYamlWriter.SectionOrder)
            Assert.Equal(first[key], second[key]);
    }

    [Theory]
    [InlineData("Done: QA #2")]
    [InlineData("*Ready")]
    [InlineData("\"Quoted\"")]
    [InlineData("[x]")]
    [InlineData(" In Review")]
    [InlineData("yes")]
    [InlineData("a, b")]
    [InlineData("null")]
    [InlineData("https://x.example/a#b")]
    public void Awkward_scalars_round_trip(string status)
    {
        var form = Filled();
        form.Pipeline[0].ApprovedWhenStatus = status;
        form.Pipeline[1].EntryStatuses = [status, "Planned"];
        form.Name = status;

        var p = Parse(ProjectYamlWriter.NewFile(form, Registry));

        Assert.Equal(status, p.Pipeline[0].ApprovedWhenStatus);
        Assert.Equal([status, "Planned"], p.Pipeline[1].EntryStatuses);
        Assert.Equal(status, p.Name);
    }

    [Theory]
    [InlineData("Done: QA #2")]
    [InlineData("[x]")]
    [InlineData("yes")]
    public void Awkward_list_items_in_provider_fields_round_trip(string column)
    {
        var form = Filled("github-projects", "github");
        form.Tracker.Lists["statuses"] = [column];

        var p = Parse(ProjectYamlWriter.NewFile(form, Registry));

        Assert.Equal([column], ((GitHubProjectsTrackerConfig)p.Tracker).Statuses!);
    }

    [Fact]
    public void Provider_defaults_are_omitted()
    {
        var form = Filled();
        form.Repo.Values["mainBranch"] = "main";
        form.Repo.Values["mergeStrategy"] = "merge_commit";
        form.Build.Values["timeoutMinutes"] = "45";

        var sections = ProjectYamlWriter.Sections(form, Registry);

        Assert.DoesNotContain("mainBranch", sections["repo"]);
        Assert.DoesNotContain("mergeStrategy", sections["repo"]);
        Assert.DoesNotContain("timeoutMinutes", sections["build"]);
        Assert.StartsWith("repo:\n  type: bitbucket\n", sections["repo"]);
        Assert.Null(sections["maxAttemptsPerStage"]);
        Assert.Null(sections["riskAreas"]);
        Assert.Null(sections["prompts"]);
        Assert.Equal("review:\n  mode: off\n", sections["review"]);
    }

    [Fact]
    public void Pipeline_stages_are_one_flow_mapping_per_line()
    {
        var form = Filled();
        form.Pipeline[0].EntryStatuses = ["Backlog"];
        form.Pipeline[0].ApprovedWhenStatus = "Planned";

        var pipeline = ProjectYamlWriter.Sections(form, Registry)["pipeline"]!;

        Assert.StartsWith(
            "pipeline:\n  - { id: triage, kind: triage, gate: human, entryStatuses: [Backlog], approvedWhenStatus: Planned }\n",
            pipeline);
        Assert.Contains("  - { id: build, kind: build }\n", pipeline);
    }

    [Fact]
    public void Line_breaks_in_values_fail_validation()
    {
        var form = Filled();
        form.Name = "a\nb";
        form.Tracker.Values["jql"] = "a\nb";
        form.Pipeline[0].EntryStatuses = ["ok", "x\r"];

        var errors = form.Validate(Registry);

        Assert.Contains("name must be a single line", errors);
        Assert.Contains("tracker.jql must be a single line", errors);
        Assert.Contains("pipeline stage 'triage' entryStatuses must be a single line", errors);
    }

    [Fact]
    public void Invalid_secret_name_fails_validation()
    {
        var form = Filled();
        form.Tracker.Values["tokenSecret"] = "Jira Acme";

        Assert.Contains("invalid secret name 'Jira Acme'", form.Validate(Registry));
    }

    [Fact]
    public void Validation_reports_numbers_duplicate_stages_and_blank_items()
    {
        var form = Filled("github-projects", "github");
        form.Tracker.Values["project"] = "seven";
        form.Pipeline[1].Id = "triage";
        form.Tracker.Lists["statuses"] = ["Backlog", " "];

        var errors = form.Validate(Registry);

        Assert.Contains("tracker.project must be a whole number", errors);
        Assert.Contains("duplicate stage id 'triage'", errors);
        Assert.Contains("tracker.statuses contains an empty item", errors);
        Assert.Empty(Filled().Validate(Registry));
    }

    [Fact]
    public void Update_rewrites_only_changed_sections()
    {
        var text = File.ReadAllText(SamplePath("orders.example.yaml"));
        var original = Sample("orders.example.yaml");
        var edited = original.Clone();
        edited.RiskAreas.Add(new RiskAreaForm { Name = "auth", Description = "Sign-in and tokens.", Paths = ["src/Auth/**"] });

        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        var start = text.IndexOf("riskAreas:", StringComparison.Ordinal);
        var end = text.IndexOf("# Risk reviewer", StringComparison.Ordinal);
        var tail = text[end..];
        Assert.StartsWith(text[..start], result);
        Assert.EndsWith(tail, result);
        var middle = result[start..^tail.Length];
        Assert.Contains("  - name: auth\n", middle);
        Assert.Contains("# Example Symphonist project.", result);
        Assert.Contains("# Stages run in order", result);
        var p = Parse(result);
        Assert.Equal(["pricing", "auth"], p.RiskAreas.Select(a => a.Name));
    }

    [Theory]
    [InlineData("orders.example.yaml")]
    [InlineData("orders-github.example.yaml")]
    public void Update_with_no_changes_returns_text_unchanged(string sample)
    {
        var text = File.ReadAllText(SamplePath(sample));
        var original = Sample(sample);

        Assert.Equal(text, ProjectYamlWriter.Update(text, original, original.Clone(), Registry));
        var crlf = text.ReplaceLineEndings("\r\n");
        Assert.Equal(crlf, ProjectYamlWriter.Update(crlf, original, original.Clone(), Registry));
    }

    [Fact]
    public void Update_uses_the_files_line_ending()
    {
        var text = File.ReadAllText(SamplePath("orders.example.yaml")).ReplaceLineEndings("\r\n");
        var original = Sample("orders.example.yaml");
        var edited = original.Clone();
        edited.MaxAttemptsPerStage = 5;

        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        Assert.DoesNotMatch("[^\r]\n", result);
        Assert.Equal(5, Parse(result).MaxAttemptsPerStage);
    }

    [Fact]
    public void Update_keeps_hand_formatting_of_unchanged_sections()
    {
        var form = Filled();
        var text = ProjectYamlWriter.NewFile(form, Registry);
        var tracker = ProjectYamlWriter.Sections(form, Registry)["tracker"]!;
        var odd = "tracker:\n    # hand-written note\n    " + string.Join("\n    ", tracker.TrimEnd('\n').Split('\n').Skip(1).Select(l => l.Trim())) + "\n";
        text = text.Replace(tracker, odd);
        var original = ProjectForm.From(Parse(text), Registry);
        var edited = original.Clone();
        edited.Pipeline[3].Gate = GatePolicy.Human;

        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        Assert.Contains(odd, result);
        Assert.Equal(GatePolicy.Human, Parse(result).Pipeline[3].Gate);
    }

    [Fact]
    public void Update_keeps_provider_keys_the_fields_do_not_model()
    {
        var text = File.ReadAllText(SamplePath("orders-github.example.yaml"))
            .Replace("  # assignee: me ", "  assignee: me   ");
        var original = ProjectForm.From(Parse(text), Registry);
        var edited = original.Clone();
        edited.Tracker.Values["label"] = "Robot";

        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        var tracker = ProjectYamlDocumentTests.Section(result, "tracker");
        Assert.Contains("label: Robot", tracker);
        Assert.Contains("assignee: me", tracker);
        Assert.Equal("Robot", ((GitHubProjectsTrackerConfig)Parse(result).Tracker).Label);
    }

    [Fact]
    public void Update_refuses_unsafe_files()
    {
        var form = Filled();
        Assert.Throws<ProjectYamlEditException>(() => ProjectYamlWriter.Update("{id: orders}", form, form.Clone(), Registry));
    }
}
