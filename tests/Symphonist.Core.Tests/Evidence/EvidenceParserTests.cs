using System.Text.Json;
using Symphonist.Core.Evidence;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Core.Tests.Evidence;

public class EvidenceParserTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Full = """
    {"headline":["Login works","One regression"],
     "regressions":[{"area":"auth","intended":false,"risk":"high","reason":"token path changed"}],
     "coverage":[{"area":"auth","tests":["LoginTests"],"gap":""},{"area":"billing","tests":[],"gap":"no tests"}],
     "tests":{"command":"dotnet test","passed":true,"summary":"42 passed"},
     "walkthrough":{"ran":true,"run_source":"config","features":[{"name":"Login","source":"added",
        "steps":[{"action":"open","result":"page","passed":true,"screenshot":"a.png"}]}]},
     "proposed_config":{"run":{"setup":["npm i"],"start":"npm start","url":"http://localhost:3000","ready":"ready","notes":"n"},
        "features":[{"name":"Login","steps":"open the page"}]}}
    """;

    [Fact]
    public void Parses_full_report()
    {
        var r = EvidenceParser.Parse(J(Full))!;
        Assert.Equal(2, r.Headline.Count);
        var reg = Assert.Single(r.Regressions);
        Assert.Equal(("auth", false, RiskLevel.High), (reg.Area, reg.Intended, reg.Risk));
        Assert.Equal(2, r.Coverage.Count);
        Assert.Equal("no tests", r.Coverage[0].Gap); // gaps first
        Assert.Equal("", r.Coverage[1].Gap);
        Assert.Equal("42 passed", r.Tests!.Summary);
        Assert.True(r.Walkthrough.Ran);
        Assert.Equal(EvidenceSource.Config, r.Walkthrough.RunSource);
        var f = Assert.Single(r.Walkthrough.Features);
        Assert.Equal(EvidenceSource.Added, f.Source);
        Assert.Equal("a.png", f.Steps[0].Screenshot);
        Assert.Equal("npm start", r.ProposedRun!.Start);
        Assert.Equal(["npm i"], r.ProposedRun.Setup);
        Assert.Equal("Login", Assert.Single(r.ProposedFeatures).Name);
    }

    [Fact]
    public void Missing_walkthrough_is_unusable()
    {
        Assert.Null(EvidenceParser.Parse(J("""{"headline":["x"],"regressions":[],"coverage":[]}""")));
        Assert.Null(EvidenceParser.Parse(null));
        Assert.Null(EvidenceParser.Parse(J("[]")));
        Assert.Null(EvidenceReport.FromJson("not json"));
        Assert.Null(EvidenceReport.FromJson(null));
    }

    [Fact]
    public void Not_run_without_reason_gets_placeholder()
    {
        var r = EvidenceParser.Parse(J("""{"headline":[],"regressions":[],"coverage":[],"walkthrough":{"ran":false}}"""))!;
        Assert.Equal("(no reason given)", r.Walkthrough.WhyNot);
    }

    [Fact]
    public void Parser_bounds_and_drops_bad_items()
    {
        var big = new string('h', 1_000_000);
        var regs = string.Join(",", Enumerable.Range(0, 50).Select(i => $$"""{"area":"a{{i}}","intended":true,"risk":"low","reason":"r"}"""));
        var steps = string.Join(",", Enumerable.Range(0, 100).Select(_ => """{"action":"a","result":"r","passed":true}"""));
        var json = $$$"""
        {"headline":["{{{big}}}","b","c","d"],"regressions":[{{{regs}}}],"coverage":[],
         "walkthrough":{"ran":true,"run_source":"config","features":[
           {"name":"many","source":"config","steps":[{{{steps}}}]},
           {"name":"bad","source":"bogus","steps":[]},
           {"name":"s","source":"guessed","steps":[
             {"action":"a","result":"r","passed":true,"screenshot":"../../x.png"},
             {"action":"a","result":"r","passed":true,"screenshot":"/etc/passwd"},
             {"action":"a","result":"r","passed":true,"screenshot":"OK.PNG"}]}]}}
        """;
        var r = EvidenceParser.Parse(J(json))!;
        Assert.Equal(3, r.Headline.Count);
        Assert.True(r.Headline[0].Length <= ReviewerText.MaxSummaryChars);
        Assert.Equal(10, r.Regressions.Count);
        Assert.Equal(2, r.Walkthrough.Features.Count);
        Assert.Equal(EvidenceParser.MaxStepsPerFeature, r.Walkthrough.Features[0].Steps.Count);
        var s = r.Walkthrough.Features[1].Steps;
        Assert.Null(s[0].Screenshot);
        Assert.Null(s[1].Screenshot);
        Assert.Equal("OK.PNG", s[2].Screenshot);
    }

    [Fact]
    public void Bad_news_survives_the_limits()
    {
        var regs = string.Join(",", Enumerable.Range(0, 10).Select(i => $$"""{"area":"a{{i}}","intended":true,"risk":"low","reason":"r"}"""))
            + """,{"area":"eleventh","intended":false,"risk":"high","reason":"bad"}""";
        var steps = string.Join(",", Enumerable.Range(0, 30).Select(_ => """{"action":"a","result":"r","passed":true}"""))
            + """,{"action":"late fail","result":"boom","passed":false}""";
        var json = $$$"""
        {"headline":[],"coverage":[],"regressions":[{{{regs}}}],
         "walkthrough":{"ran":true,"run_source":"config","features":[{"name":"f","source":"config","steps":[{{{steps}}}]}]}}
        """;
        var r = EvidenceParser.Parse(J(json))!;
        Assert.Equal(10, r.Regressions.Count);
        Assert.Equal("eleventh", r.Regressions[0].Area);
        Assert.Equal(EvidenceParser.MaxStepsPerFeature, r.Walkthrough.Features[0].Steps.Count);
        Assert.Equal("late fail", r.Walkthrough.Features[0].Steps[0].Action);
        Assert.True(r.HasIssues);
    }

    [Fact]
    public void Blank_text_or_non_boolean_passed_keeps_bad_news()
    {
        var json = """
        {"headline":[],"coverage":[],
         "regressions":[{"area":"","intended":false,"risk":"high","reason":""}],
         "walkthrough":{"ran":true,"run_source":"config","features":[{"name":"f","source":"config","steps":[
           {"action":"  ","result":"","passed":false},{"action":"x","result":"y","passed":"yes"}]}]}}
        """;
        var r = EvidenceParser.Parse(J(json))!;
        Assert.Equal("(no description given)", r.Regressions[0].Reason);
        var steps = r.Walkthrough.Features[0].Steps;
        Assert.Equal(2, steps.Count);
        Assert.Equal("(no description given)", steps[0].Action);
        Assert.All(steps, s => Assert.False(s.Passed));
        Assert.True(r.HasIssues);
    }

    [Fact]
    public void Odd_risk_counts_as_high()
    {
        var r = EvidenceParser.Parse(J("""{"headline":[],"coverage":[],"walkthrough":{"ran":true},"regressions":[{"area":"a","intended":false,"risk":"medium","reason":"r"}]}"""))!;
        Assert.Equal(RiskLevel.High, r.Regressions[0].Risk);
    }

    private static EvidenceReport Clean() => new(["ok"], [], [new CoverageItem("a", ["t"], "gap")], new TestRun("c", true, "s"),
        new Walkthrough(true, null, EvidenceSource.Config,
            [new WalkthroughFeature("f", EvidenceSource.Config, [new WalkthroughStep("a", "r", true, null)])]), null, []);

    [Fact]
    public void Has_issues_cases()
    {
        var c = Clean();
        Assert.False(c.HasIssues);
        Assert.True((c with { Tests = new TestRun("c", false, "s") }).HasIssues);
        Assert.True((c with { Walkthrough = c.Walkthrough with { Ran = false, WhyNot = "x" } }).HasIssues);
        Assert.True((c with { Walkthrough = c.Walkthrough with { Features = [new("f", EvidenceSource.Config, [new("a", "r", false, null)])] } }).HasIssues);
        Assert.True((c with { Regressions = [new RegressionItem("a", false, RiskLevel.High, "r")] }).HasIssues);
        Assert.False((c with { Regressions = [new RegressionItem("a", true, RiskLevel.High, "r"), new RegressionItem("a", false, RiskLevel.Low, "r")] }).HasIssues);
    }

    [Fact]
    public void Guessed_cases()
    {
        var c = Clean();
        Assert.False(c.Guessed);
        Assert.True((c with { Walkthrough = c.Walkthrough with { RunSource = EvidenceSource.Guessed } }).Guessed);
        Assert.True((c with { Walkthrough = c.Walkthrough with { Features = [new("f", EvidenceSource.Guessed, [])] } }).Guessed);
    }

    [Fact]
    public void Json_round_trip()
    {
        var r = EvidenceParser.Parse(J(Full))!;
        var back = EvidenceReport.FromJson(r.ToJson())!;
        Assert.Equal(r.ToJson(), back.ToJson());
        Assert.Equal("a.png", back.Walkthrough.Features[0].Steps[0].Screenshot);
        Assert.Equal("npm start", back.ProposedRun!.Start);
        var rec = new EvidenceRecord(Guid.NewGuid(), "p", "s", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, r.ToJson(), false, null, [], false);
        Assert.NotNull(rec.Report);
    }

    [Fact]
    public void Schema_is_valid_json_with_required()
    {
        var s = J(EvidenceSchema.Json);
        Assert.Equal("object", s.GetProperty("type").GetString());
        Assert.Equal(4, s.GetProperty("required").GetArrayLength());
    }
}
