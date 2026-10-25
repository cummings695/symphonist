using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Integrations;

namespace Symphonist.Core.Tests.Onboarding;

public class OnboardingProposalTests
{
    private static readonly SourceStatus[] JiraStatuses =
    [
        new("Backlog", StatusCategory.ToDo, "New work"),
        new("Selected for Development", StatusCategory.ToDo, ""),
        new("In Progress", StatusCategory.InProgress, ""),
        new("In Review", StatusCategory.InProgress, ""),
        new("Done", StatusCategory.Done, ""),
    ];

    private static List<StageForm> Pipeline() => ProjectForm.NewProject("orders", BuiltInProviders.Registry).Pipeline;

    private static readonly RiskAreaForm[] ExistingAreas =
        [new() { Name = "pricing", Description = "Price rules", Paths = ["src/Pricing/**"] }];

    private static OnboardingProposal Parse(string json, IReadOnlyList<SourceStatus>? statuses = null)
    {
        using var doc = JsonDocument.Parse(json);
        return OnboardingProposalParser.Parse(doc.RootElement.Clone(), statuses ?? JiraStatuses, Pipeline(), ExistingAreas);
    }

    [Fact]
    public void Schema_is_valid_json_and_requires_stages_and_risk_areas()
    {
        using var doc = JsonDocument.Parse(OnboardingProposalSchema.Json);

        Assert.Equal(["stages", "risk_areas"], doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Valid_proposal_maps_statuses_with_tracker_spelling()
    {
        var p = Parse("""
            {"stages":[{"id":"plan","entry_statuses":["selected for development"],"on_enter_status":"IN PROGRESS",
              "on_gate_status":"in review","approved_when_status":"in progress","reason":"plan is <b>reviewed</b>"}],
             "risk_areas":[],"notes":["looks like a standard board"]}
            """);

        Assert.Null(p.Error);
        var plan = Assert.Single(p.Stages);
        Assert.Equal("plan", plan.StageId);
        Assert.Equal(["Selected for Development"], plan.EntryStatuses);
        Assert.Equal("In Progress", plan.OnEnterStatus);
        Assert.Equal("In Review", plan.OnGateStatus);
        Assert.Equal("In Progress", plan.ApprovedWhenStatus);
        Assert.Equal("plan is <b>reviewed</b>", plan.Reason);
        Assert.Equal(["looks like a standard board"], p.Notes);
    }

    [Fact]
    public void Unknown_category_statuses_are_accepted()
    {
        SourceStatus[] github =
        [
            new("Todo", StatusCategory.Unknown, ""),
            new("Doing", StatusCategory.Unknown, "Being worked on"),
            new("Shipped", StatusCategory.Unknown, ""),
        ];

        var p = Parse("""
            {"stages":[{"id":"triage","entry_statuses":["Todo"],"on_enter_status":"Doing","reason":"first column"},
                       {"id":"done","on_enter_status":"Shipped","reason":"last column"}],
             "risk_areas":[]}
            """, github);

        Assert.Empty(p.Notes);
        Assert.Equal(["triage", "done"], p.Stages.Select(s => s.StageId));
        Assert.Equal(["Todo"], p.Stages[0].EntryStatuses);
        Assert.Equal("Doing", p.Stages[0].OnEnterStatus);
        Assert.Equal("Shipped", p.Stages[1].OnEnterStatus);
    }

    [Fact]
    public void Invalid_items_are_dropped_with_notes()
    {
        var p = Parse("""
            {"stages":[
                {"id":"triage","entry_statuses":["Backlog","Waiting for Godot"],"reason":"ok"},
                {"id":"nope","entry_statuses":["In Progress"],"reason":"unknown stage"},
                {"id":"build","on_enter_status":"In Progress","on_gate_status":"In Review","reason":"build has no gate"},
                "not an object"],
             "risk_areas":[
                {"name":"payments","description":"Card handling","paths":["src/Payments/**","../x","C:/x"],"reason":"money"},
                {"name":"Payments","description":"again","paths":["src/**"],"reason":"duplicate"}]}
            """);

        Assert.Null(p.Error);
        Assert.Equal(["triage", "build"], p.Stages.Select(s => s.StageId));
        Assert.Equal(["Backlog"], p.Stages[0].EntryStatuses);
        Assert.Equal("In Progress", p.Stages[1].OnEnterStatus);
        Assert.Null(p.Stages[1].OnGateStatus);
        var area = Assert.Single(p.RiskAreas);
        Assert.Equal("payments", area.Area.Name);
        Assert.Equal(["src/Payments/**"], area.Area.Paths);
        Assert.Equal(
        [
            "dropped status 'Waiting for Godot' for stage triage: not a status of this project",
            "dropped mapping for unknown stage 'nope'",
            "dropped status 'In Review' for stage build: stage build has no gate",
            "dropped malformed stage mapping",
            "dropped path '../x' of risk area 'payments': unsafe path",
            "dropped path 'C:/x' of risk area 'payments': unsafe path",
            "dropped risk area 'payments': duplicate name",
        ], p.Notes);
    }

    [Fact]
    public void Missing_output_fails_closed()
    {
        foreach (JsonElement? structured in new JsonElement?[] { null, JsonDocument.Parse("[1]").RootElement.Clone() })
        {
            var p = OnboardingProposalParser.Parse(structured, JiraStatuses, Pipeline(), ExistingAreas);

            Assert.Equal("agent returned no structured output", p.Error);
            Assert.Empty(p.Stages);
            Assert.Empty(p.AddedStages);
            Assert.Empty(p.RiskAreas);
            Assert.Empty(p.Notes);
        }
    }

    [Fact]
    public void Entry_status_claimed_once()
    {
        var p = Parse("""
            {"stages":[{"id":"triage","entry_statuses":["Backlog"],"reason":"a"},
                       {"id":"plan","entry_statuses":["backlog","Selected for Development"],"reason":"b"}],
             "risk_areas":[]}
            """);

        Assert.Equal(["Backlog"], p.Stages[0].EntryStatuses);
        Assert.Equal(["Selected for Development"], p.Stages[1].EntryStatuses);
        Assert.Equal(["dropped status 'backlog' for stage plan: already starts stage triage"], p.Notes);
    }

    [Fact]
    public void Done_stage_keeps_only_on_enter_status()
    {
        var p = Parse("""
            {"stages":[{"id":"done","entry_statuses":["Done"],"on_enter_status":"Done","reason":"end"}],"risk_areas":[]}
            """);

        var done = Assert.Single(p.Stages);
        Assert.Empty(done.EntryStatuses);
        Assert.Equal("Done", done.OnEnterStatus);
        Assert.Equal(["dropped status 'Done' for stage done: a done stage only takes on_enter_status"], p.Notes);
    }

    [Fact]
    public void Mapping_whose_statuses_were_all_dropped_is_dropped_so_the_stage_keeps_its_mapping()
    {
        var p = Parse("""
            {"stages":[{"id":"triage","entry_statuses":["Nope"],"reason":"a"},{"id":"plan","reason":"nothing fits"}],
             "risk_areas":[]}
            """);

        var plan = Assert.Single(p.Stages);
        Assert.Equal("plan", plan.StageId);
        Assert.Equal(
        [
            "dropped status 'Nope' for stage triage: not a status of this project",
            "dropped mapping for stage 'triage': no valid status left",
        ], p.Notes);
    }

    [Fact]
    public void Risk_areas_are_capped_and_normalised()
    {
        var areas = new List<string> { """{"name":"pricing","description":"again","paths":[],"reason":"r"}""" };
        areas.Add("""{"name":"Pricing Engine","description":"Discounts","paths":["src/Engine/**"],"reason":"r"}""");
        for (var i = 0; i < 11; i++)
            areas.Add($$"""{"name":"area{{i}}","description":"d","paths":[],"reason":"r"}""");

        var p = Parse($$"""{"stages":[],"risk_areas":[{{string.Join(",", areas)}}]}""");

        Assert.Equal(10, p.RiskAreas.Count);
        Assert.Equal("pricing-engine", p.RiskAreas[0].Area.Name);
        Assert.Equal(["src/Engine/**"], p.RiskAreas[0].Area.Paths);
        Assert.DoesNotContain(p.RiskAreas, a => a.Area.Name == "pricing");
        Assert.Contains("dropped risk area 'pricing': already configured", p.Notes);
        Assert.Contains("dropped 2 risk areas: at most 10", p.Notes);
    }

    [Fact]
    public void Risk_area_text_is_cleaned_and_capped()
    {
        var p = Parse($$"""
            {"stages":[],"risk_areas":[{"name":"{{new string('x', 60)}}","description":"line\none","paths":["src/a\u0001b"],"reason":"{{new string('r', 400)}}"},
                                       {"name":"blank","description":"  ","paths":[],"reason":"r"}]}
            """);

        var area = Assert.Single(p.RiskAreas);
        Assert.Equal(new string('x', 40), area.Area.Name);
        Assert.Equal("line one", area.Area.Description);
        Assert.Empty(area.Area.Paths);
        Assert.Equal(300, area.Reason.Length);
        Assert.Contains("dropped risk area 'blank': no description", p.Notes);
    }

    [Fact]
    public void Added_stage_must_follow_a_non_done_stage()
    {
        var p = Parse("""
            {"stages":[{"id":"qa","entry_statuses":["In Review"],"reason":"QA column"}],
             "added_stages":[{"id":"qa","kind":"uat","after":"build","reason":"there is a QA column"},
                             {"id":"after-done","kind":"deploy","after":"done","reason":"x"},
                             {"id":"plan","kind":"plan","after":"triage","reason":"x"},
                             {"id":"Bad_Id","kind":"uat","after":"build","reason":"x"},
                             {"id":"ghost","kind":"uat","after":"nowhere","reason":"x"}],
             "risk_areas":[]}
            """);

        var qa = Assert.Single(p.AddedStages);
        Assert.Equal(new AddedStageProposal("qa", StageKind.Uat, "build", "there is a QA column"), qa);
        Assert.Equal(["qa"], p.Stages.Select(s => s.StageId));
        Assert.Equal(
        [
            "dropped added stage 'after-done': it cannot follow the done stage",
            "dropped added stage 'plan': the id is already used",
            "dropped added stage 'Bad_Id': invalid id",
            "dropped added stage 'ghost': no stage 'nowhere' to follow",
        ], p.Notes);
    }

    [Fact]
    public void At_most_three_added_stages()
    {
        var added = string.Join(",", Enumerable.Range(1, 5).Select(i => $$"""{"id":"extra-{{i}}","kind":"uat","after":"build","reason":"r"}"""));

        var p = Parse($$"""{"stages":[],"added_stages":[{{added}}],"risk_areas":[]}""");

        Assert.Equal(["extra-1", "extra-2", "extra-3"], p.AddedStages.Select(s => s.Id));
        Assert.Equal(["dropped 2 added stages: at most 3"], p.Notes);
    }

    [Fact]
    public void Apply_overwrites_mapping_adds_new_areas_and_accepted_stages_only()
    {
        var form = ProjectYamlWriterTests.Filled();
        form.Pipeline[1].EntryStatuses = ["Old"];
        form.Pipeline[1].OnGateStatus = "Old gate";
        form.Pipeline[2].OnEnterStatus = "Kept";
        form.RiskAreas = [ExistingAreas[0].Clone()];
        var before = form.Clone();
        using var doc = JsonDocument.Parse("""
            {"stages":[{"id":"plan","entry_statuses":["Selected for Development"],"on_enter_status":"In Progress","reason":"r"},
                       {"id":"qa","entry_statuses":["In Review"],"reason":"r"},
                       {"id":"uat-two","on_enter_status":"In Review","reason":"r"}],
             "added_stages":[{"id":"qa","kind":"uat","after":"build","reason":"r"},
                             {"id":"uat-two","kind":"uat","after":"build","reason":"r"}],
             "risk_areas":[{"name":"payments","description":"Cards","paths":["src/Payments/**"],"reason":"r"}]}
            """);
        var proposal = OnboardingProposalParser.Parse(doc.RootElement.Clone(), JiraStatuses, form.Pipeline, form.RiskAreas);
        Assert.Empty(proposal.Notes);

        proposal.ApplyTo(form, new HashSet<string> { "qa" });

        Assert.Equal(["triage", "plan", "implement", "build", "qa", "release", "deploy", "done"], form.Pipeline.Select(s => s.Id));
        var plan = form.Pipeline[1];
        Assert.Equal(["Selected for Development"], plan.EntryStatuses);
        Assert.Equal("In Progress", plan.OnEnterStatus);
        Assert.Null(plan.OnGateStatus);
        Assert.Equal("Kept", form.Pipeline[2].OnEnterStatus);
        var qa = form.Pipeline[4];
        Assert.Equal(StageKind.Uat, qa.Kind);
        Assert.Equal(GatePolicy.Human, qa.Gate);
        Assert.Equal(["In Review"], qa.EntryStatuses);
        Assert.Equal(["pricing", "payments"], form.RiskAreas.Select(a => a.Name));
        Assert.Equal(["src/Payments/**"], form.RiskAreas[1].Paths);

        var after = ProjectYamlWriter.Sections(form, BuiltInProviders.Registry);
        var original = ProjectYamlWriter.Sections(before, BuiltInProviders.Registry);
        foreach (var key in ProjectYamlWriter.SectionOrder.Except(["pipeline", "riskAreas"]))
            Assert.Equal(original[key], after[key]);
        Assert.NotEqual(original["pipeline"], after["pipeline"]);
    }
}
