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
    [InlineData("\u00A0Orders\u3000")]
    [InlineData("...")]
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
        form.Tracker.Values["jql"] = "a\rb";
        form.Pipeline[0].EntryStatuses = ["ok", "x\r"];
        form.RiskAreas.Add(new RiskAreaForm { Name = "x", Description = "a\u2028b", Paths = [] });

        var errors = form.Validate(Registry);

        Assert.Contains("name must be a single line", errors);
        Assert.Contains("tracker.jql contains an unsupported line break (only plain line breaks are allowed)", errors);
        Assert.Contains("pipeline stage 'triage' entryStatuses must be a single line", errors);
        Assert.Contains("riskAreas 'x' description contains an unsupported line break (only plain line breaks are allowed)",
            errors);
    }

    [Fact]
    public void Descriptions_and_jql_may_span_lines()
    {
        var form = Filled();
        form.Tracker.Values["jql"] = "project = ACME\nAND labels = Claude";
        form.RiskAreas.Add(new RiskAreaForm { Name = "pricing", Description = "Prices.\nAnd tax.\n", Paths = [] });

        Assert.Empty(form.Validate(Registry));
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("line one\nline two\n")]
    [InlineData("line one\nline two\n\n")]
    [InlineData(" leading space\nnext")]
    [InlineData("\nstarts with a break")]
    [InlineData("a: b # not a comment\n  indented\n\n- not a list\n   ")]
    public void Multi_line_descriptions_and_jql_are_literal_blocks_that_round_trip(string value)
    {
        var form = Filled();
        form.Tracker.Values["jql"] = value;
        form.RiskAreas.Add(new RiskAreaForm { Name = "pricing", Description = value, Paths = ["src/**"] });

        var text = ProjectYamlWriter.NewFile(form, Registry);

        Assert.Matches(@"\n  jql: \|", text);
        Assert.Matches(@"\n    description: \|", text);
        var config = Parse(text);
        Assert.Equal(value, ((Symphonist.Integrations.Jira.JiraTrackerConfig)config.Tracker).Jql);
        Assert.Equal(value, Assert.Single(config.RiskAreas).Description);
        Assert.Equal(["src/**"], config.RiskAreas[0].Paths);
    }

    private const string FoldedDescription =
        "    description: >\n      Anything that changes how order prices,\n      discounts or tax are computed.\n";

    private static (string Text, ProjectForm Form) WithFoldedDescription()
    {
        var text = File.ReadAllText(SamplePath("orders.example.yaml")).ReplaceLineEndings("\n");
        text = text.Replace("    description: Anything that changes how order prices, discounts or tax are computed.\n",
            FoldedDescription);
        Assert.Contains(FoldedDescription, text);
        return (text, ProjectForm.From(Parse(text), Registry));
    }

    [Fact]
    public void Folded_description_is_kept_byte_for_byte_by_an_unrelated_edit()
    {
        var (text, original) = WithFoldedDescription();
        Assert.Equal("Anything that changes how order prices, discounts or tax are computed.\n",
            original.RiskAreas[0].Description);
        var edited = original.Clone();
        edited.Pipeline[3].Gate = GatePolicy.Human;

        Assert.Empty(edited.Validate(Registry));
        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        var start = text.IndexOf("riskAreas:", StringComparison.Ordinal);
        var end = text.IndexOf("# Risk reviewer", StringComparison.Ordinal);
        Assert.Contains(text[start..end], result);
        Assert.Equal(GatePolicy.Human, Parse(result).Pipeline[3].Gate);
        // The draft the setup steps run with (NewFile of the form) loads too.
        Assert.Equal(original.RiskAreas[0].Description, Parse(ProjectYamlWriter.NewFile(edited, Registry)).RiskAreas[0].Description);
    }

    [Fact]
    public void Editing_a_folded_description_to_two_lines_writes_a_literal_block()
    {
        var (text, original) = WithFoldedDescription();
        var edited = original.Clone();
        edited.RiskAreas[0].Description = "Prices and discounts.\nTax too.";

        var result = ProjectYamlWriter.Update(text, original, edited, Registry);

        Assert.Contains("    description: |-\n      Prices and discounts.\n      Tax too.\n", result);
        Assert.Equal("Prices and discounts.\nTax too.", Parse(result).RiskAreas[0].Description);
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

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u0085b")]
    public void Multi_line_values_are_refused_by_the_writer(string value)
    {
        var form = Filled();
        form.Name = value;
        Assert.Throws<ProjectYamlEditException>(() => ProjectYamlWriter.NewFile(form, Registry));
        Assert.Contains("name must be a single line", form.Validate(Registry));
    }

    [Fact]
    public void Unicode_line_separators_fail_validation_in_list_items()
    {
        var form = Filled();
        form.Pipeline[0].EntryStatuses = ["a\u2029b"];
        Assert.Contains("pipeline stage 'triage' entryStatuses must be a single line", form.Validate(Registry));
    }

    // Unmodelled keys the loader would refuse still must not be dropped or mangled by a splice.
    private static (string Text, ProjectForm Form) WithTrackerExtras(string extras)
    {
        var form = Filled("github-projects", "github");
        var text = ProjectYamlWriter.NewFile(form, Registry);
        var tracker = ProjectYamlWriter.Sections(form, Registry)["tracker"]!;
        return (text.Replace(tracker, tracker + extras), form);
    }

    private static Dictionary<object, object?> RawTracker(string text) =>
        (Dictionary<object, object?>)new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, object?>>(text)["tracker"]!;

    [Fact]
    public void Update_copies_list_map_and_block_scalar_unmodelled_keys_from_source()
    {
        var (text, form) = WithTrackerExtras(
            "  extraList: [a, 'b: c']   # kept comment\n" +
            "  extraMap:\n" +
            "    k: v\n" +
            "  notes: |\n" +
            "    line1\n" +
            "    # not a comment\n");
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        var result = ProjectYamlWriter.Update(text, form, edited, Registry);

        Assert.Contains("  extraList: [a, 'b: c']   # kept comment\n  extraMap:\n    k: v\n  notes: |\n    line1\n    # not a comment\n", result);
        var tracker = RawTracker(result);
        Assert.Equal("Robot", tracker["label"]);
        Assert.Equal(new List<object?> { "a", "b: c" }, tracker["extraList"]);
        Assert.Equal("v", ((Dictionary<object, object?>)tracker["extraMap"]!)["k"]);
        Assert.Equal("line1\n# not a comment\n", tracker["notes"]);
    }

    [Fact]
    public void Update_reindents_unmodelled_keys_of_a_hand_indented_section()
    {
        var form = Filled("github-projects", "github");
        var text = ProjectYamlWriter.NewFile(form, Registry);
        var tracker = ProjectYamlWriter.Sections(form, Registry)["tracker"]!;
        var indented = tracker.Replace("\n  ", "\n    ") + "    extraMap:\n      k: v\n    notes: >\n      folded\n";
        text = text.Replace(tracker, indented);
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        var result = ProjectYamlWriter.Update(text, form, edited, Registry);

        Assert.Contains("  label: Robot\n", result);
        Assert.Contains("  extraMap:\n    k: v\n  notes: >\n    folded\n", result);
        Assert.Equal("v", ((Dictionary<object, object?>)RawTracker(result)["extraMap"]!)["k"]);
    }

    [Fact]
    public void Update_renders_unmodelled_keys_of_a_flow_section()
    {
        var form = Filled("github-projects", "github");
        var text = ProjectYamlWriter.NewFile(form, Registry);
        var tracker = ProjectYamlWriter.Sections(form, Registry)["tracker"]!;
        text = text.Replace(tracker,
            "tracker: { type: github-projects, owner: acme, project: 7, label: Claude, tokenSecret: github-orders, extraList: [a, 'b, c'], extraMap: { k: v } }\n");
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        var result = ProjectYamlWriter.Update(text, form, edited, Registry);

        var raw = RawTracker(result);
        Assert.Equal("Robot", raw["label"]);
        Assert.Equal(new List<object?> { "a", "b, c" }, raw["extraList"]);
        Assert.Equal("v", ((Dictionary<object, object?>)raw["extraMap"]!)["k"]);
    }

    [Fact]
    public void Update_refuses_a_multi_line_unmodelled_value_it_cannot_copy()
    {
        var form = Filled("github-projects", "github");
        var text = ProjectYamlWriter.NewFile(form, Registry);
        var tracker = ProjectYamlWriter.Sections(form, Registry)["tracker"]!;
        text = text.Replace(tracker,
            "tracker: { type: github-projects, owner: acme, project: 7, label: Claude, tokenSecret: github-orders, notes: \"a\nb\" }\n");
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        Assert.Throws<ProjectYamlEditException>(() => ProjectYamlWriter.Update(text, form, edited, Registry));
    }

    [Fact]
    public void Update_refuses_duplicate_nested_keys()
    {
        var (text, form) = WithTrackerExtras("  label: twice\n");
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        Assert.Throws<ProjectYamlEditException>(() => ProjectYamlWriter.Update(text, form, edited, Registry));
    }

    [Fact]
    public void Update_wraps_deserializer_errors()
    {
        // The splicer accepts the tag; the raw deserializer cannot read "abc" as an int.
        var (text, form) = WithTrackerExtras("  extra: !!int abc\n");
        var edited = form.Clone();
        edited.Tracker.Values["label"] = "Robot";

        var ex = Assert.Throws<ProjectYamlEditException>(() => ProjectYamlWriter.Update(text, form, edited, Registry));
        Assert.Contains("edit this file by hand", ex.Message);
    }
}
