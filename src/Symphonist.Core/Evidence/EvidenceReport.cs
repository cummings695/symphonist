using System.Text.Json;
using System.Text.RegularExpressions;
using Symphonist.Core.Config;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Evidence;

public enum EvidenceSource { Config, Added, Guessed }

public sealed record RegressionItem(string Area, bool Intended, RiskLevel Risk, string Reason);

/// <param name="Gap">What is not covered; "" = none.</param>
public sealed record CoverageItem(string Area, IReadOnlyList<string> Tests, string Gap);

public sealed record TestRun(string Command, bool Passed, string Summary);

public sealed record WalkthroughStep(string Action, string Result, bool Passed, string? Screenshot);

public sealed record WalkthroughFeature(string Name, EvidenceSource Source, IReadOnlyList<WalkthroughStep> Steps);

public sealed record Walkthrough(bool Ran, string? WhyNot, EvidenceSource RunSource, IReadOnlyList<WalkthroughFeature> Features);

public sealed record EvidenceReport(IReadOnlyList<string> Headline, IReadOnlyList<RegressionItem> Regressions,
    IReadOnlyList<CoverageItem> Coverage, TestRun? Tests, Walkthrough Walkthrough,
    EvidenceRun? ProposedRun, IReadOnlyList<EvidenceFeature> ProposedFeatures)
{
    /// <summary>Tests failed, the app did not run, a walkthrough step failed, or an unintended high-risk regression.</summary>
    public bool HasIssues =>
        Tests is { Passed: false }
        || !Walkthrough.Ran
        || Walkthrough.Features.Any(f => f.Steps.Any(s => !s.Passed))
        || Regressions.Any(r => !r.Intended && r.Risk == RiskLevel.High);

    /// <summary>The run instructions or any feature were guessed.</summary>
    public bool Guessed =>
        (Walkthrough.Ran && Walkthrough.RunSource == EvidenceSource.Guessed)
        || Walkthrough.Features.Any(f => f.Source == EvidenceSource.Guessed);

    public string ToJson() => EvidenceJson.Write(this);

    /// <summary>Null for null, blank or unparseable JSON, or an unusable shape.</summary>
    public static EvidenceReport? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return EvidenceParser.Parse(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }
}

public static class EvidenceSchema
{
    public const string Json = """{"type":"object","properties":{"headline":{"type":"array","items":{"type":"string"}},"regressions":{"type":"array","items":{"type":"object","properties":{"area":{"type":"string"},"intended":{"type":"boolean"},"risk":{"type":"string","enum":["low","high"]},"reason":{"type":"string"}},"required":["area","intended","risk","reason"]}},"coverage":{"type":"array","items":{"type":"object","properties":{"area":{"type":"string"},"tests":{"type":"array","items":{"type":"string"}},"gap":{"type":"string"}},"required":["area","tests","gap"]}},"tests":{"type":"object","properties":{"command":{"type":"string"},"passed":{"type":"boolean"},"summary":{"type":"string"}},"required":["command","passed","summary"]},"walkthrough":{"type":"object","properties":{"ran":{"type":"boolean"},"why_not":{"type":"string"},"run_source":{"type":"string","enum":["config","guessed"]},"features":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"source":{"type":"string","enum":["config","added","guessed"]},"steps":{"type":"array","items":{"type":"object","properties":{"action":{"type":"string"},"result":{"type":"string"},"passed":{"type":"boolean"},"screenshot":{"type":"string"}},"required":["action","result","passed"]}}},"required":["name","source","steps"]}}},"required":["ran","features"]},"proposed_config":{"type":"object","properties":{"run":{"type":"object","properties":{"setup":{"type":"array","items":{"type":"string"}},"start":{"type":"string"},"url":{"type":"string"},"ready":{"type":"string"},"notes":{"type":"string"}}},"features":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"steps":{"type":"string"}},"required":["name","steps"]}}}}},"required":["headline","regressions","coverage","walkthrough"]}""";
}

public static partial class EvidenceParser
{
    public const int MaxStepsPerFeature = 30;
    public const int MaxHeadlineLines = 3;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.(png|jpg|jpeg|webp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotName();

    /// <summary>Null when the shape is unusable (missing required parts); bad items are dropped, text bounded.</summary>
    public static EvidenceReport? Parse(JsonElement? structured)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } root) return null;
        if (!Arr(root, "headline", out var headline) || !Arr(root, "regressions", out var regressions)
            || !Arr(root, "coverage", out var coverage)) return null;
        if (!root.TryGetProperty("walkthrough", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        var walkthrough = ParseWalkthrough(w);
        if (walkthrough is null) return null;

        return new EvidenceReport(
            [.. headline.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => ReviewerText.Line(e.GetString(), ReviewerText.MaxSummaryChars))
                .Where(l => l.Length > 0).Take(MaxHeadlineLines)],
            [.. regressions.EnumerateArray().Select(ParseRegression).OfType<RegressionItem>().Take(ReviewerText.MaxItems)],
            [.. coverage.EnumerateArray().Select(ParseCoverage).OfType<CoverageItem>().Take(ReviewerText.MaxItems)],
            root.TryGetProperty("tests", out var t) ? ParseTests(t) : null,
            walkthrough,
            ParseProposedRun(root),
            ParseProposedFeatures(root));
    }

    private static RegressionItem? ParseRegression(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var area = Text(e, "area", ReviewerText.MaxItemChars);
        var reason = Text(e, "reason", ReviewerText.MaxItemChars);
        if (area is null || reason is null || !Bool(e, "intended", out var intended)) return null;
        var risk = Text(e, "risk", 20) == "low" ? RiskLevel.Low : RiskLevel.High;
        return new RegressionItem(area, intended, risk, reason);
    }

    private static CoverageItem? ParseCoverage(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var area = Text(e, "area", ReviewerText.MaxItemChars);
        if (area is null) return null;
        var tests = e.TryGetProperty("tests", out var t) && t.ValueKind == JsonValueKind.Array
            ? ReviewerText.Items(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? ""))
            : [];
        return new CoverageItem(area, tests, Text(e, "gap", ReviewerText.MaxItemChars) ?? "");
    }

    private static TestRun? ParseTests(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !Bool(e, "passed", out var passed)) return null;
        return new TestRun(Text(e, "command", ReviewerText.MaxItemChars) ?? "", passed,
            Text(e, "summary", ReviewerText.MaxSummaryChars) ?? "");
    }

    private static Walkthrough? ParseWalkthrough(JsonElement w)
    {
        if (!Bool(w, "ran", out var ran)) return null;
        var whyNot = ran ? null : Text(w, "why_not", ReviewerText.MaxSummaryChars) ?? "(no reason given)";
        var runSource = Text(w, "run_source", 20) == "config" ? EvidenceSource.Config : EvidenceSource.Guessed;
        var features = new List<WalkthroughFeature>();
        if (w.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray().Take(EvidenceLimits.MaxFeatures))
                if (ParseFeature(f) is { } feature) features.Add(feature);
        return new Walkthrough(ran, whyNot, runSource, features);
    }

    private static WalkthroughFeature? ParseFeature(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) return null;
        var name = Text(f, "name", EvidenceLimits.MaxFeatureNameChars);
        if (name is null) return null;
        EvidenceSource source;
        switch (Text(f, "source", 20))
        {
            case "config": source = EvidenceSource.Config; break;
            case "added": source = EvidenceSource.Added; break;
            case "guessed": source = EvidenceSource.Guessed; break;
            default: return null;
        }
        var steps = new List<WalkthroughStep>();
        if (f.TryGetProperty("steps", out var ss) && ss.ValueKind == JsonValueKind.Array)
            foreach (var s in ss.EnumerateArray())
            {
                if (steps.Count >= MaxStepsPerFeature) break;
                if (ParseStep(s) is { } step) steps.Add(step);
            }
        return new WalkthroughFeature(name, source, steps);
    }

    private static WalkthroughStep? ParseStep(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var action = Text(s, "action", ReviewerText.MaxItemChars);
        if (action is null || !Bool(s, "passed", out var passed)) return null;
        string? shot = null;
        if (s.TryGetProperty("screenshot", out var sh) && sh.ValueKind == JsonValueKind.String
            && sh.GetString() is { } name && ScreenshotName().IsMatch(name))
            shot = name;
        return new WalkthroughStep(action, Text(s, "result", ReviewerText.MaxItemChars) ?? "", passed, shot);
    }

    private static EvidenceRun? ParseProposedRun(JsonElement root)
    {
        if (!root.TryGetProperty("proposed_config", out var pc) || pc.ValueKind != JsonValueKind.Object
            || !pc.TryGetProperty("run", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        var setup = r.TryGetProperty("setup", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => ReviewerText.Line(x.GetString(), EvidenceLimits.MaxRunFieldChars))
                .Where(x => x.Length > 0).Take(EvidenceLimits.MaxSetupCommands).ToList()
            : [];
        var run = new EvidenceRun(setup, Text(r, "start", EvidenceLimits.MaxRunFieldChars), Text(r, "url", EvidenceLimits.MaxRunFieldChars),
            Text(r, "ready", EvidenceLimits.MaxRunFieldChars), Text(r, "notes", EvidenceLimits.MaxNotesChars));
        return run.IsEmpty ? null : run;
    }

    private static IReadOnlyList<EvidenceFeature> ParseProposedFeatures(JsonElement root)
    {
        if (!root.TryGetProperty("proposed_config", out var pc) || pc.ValueKind != JsonValueKind.Object
            || !pc.TryGetProperty("features", out var fs) || fs.ValueKind != JsonValueKind.Array) return [];
        var list = new List<EvidenceFeature>();
        foreach (var f in fs.EnumerateArray())
        {
            if (list.Count >= EvidenceLimits.MaxFeatures) break;
            if (f.ValueKind != JsonValueKind.Object) continue;
            var name = Text(f, "name", EvidenceLimits.MaxFeatureNameChars);
            var steps = Text(f, "steps", EvidenceLimits.MaxFeatureStepsChars);
            if (name is not null && steps is not null) list.Add(new EvidenceFeature(name, steps));
        }
        return list;
    }

    private static bool Arr(JsonElement root, string name, out JsonElement array)
    {
        array = default;
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return false;
        array = p;
        return true;
    }

    private static bool Bool(JsonElement e, string name, out bool value)
    {
        value = false;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = p.GetBoolean();
        return true;
    }

    /// <summary>Bounded one-line text of a string property; null when absent, not a string, or blank.</summary>
    private static string? Text(JsonElement e, string name, int max)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var line = ReviewerText.Line(p.GetString(), max);
        return line.Length == 0 ? null : line;
    }
}

internal static class EvidenceJson
{
    public static string Write(EvidenceReport r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            Strings(w, "headline", r.Headline);
            w.WriteStartArray("regressions");
            foreach (var x in r.Regressions)
            {
                w.WriteStartObject();
                w.WriteString("area", x.Area);
                w.WriteBoolean("intended", x.Intended);
                w.WriteString("risk", x.Risk == RiskLevel.Low ? "low" : "high");
                w.WriteString("reason", x.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("coverage");
            foreach (var x in r.Coverage)
            {
                w.WriteStartObject();
                w.WriteString("area", x.Area);
                Strings(w, "tests", x.Tests);
                w.WriteString("gap", x.Gap);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (r.Tests is { } t)
            {
                w.WriteStartObject("tests");
                w.WriteString("command", t.Command);
                w.WriteBoolean("passed", t.Passed);
                w.WriteString("summary", t.Summary);
                w.WriteEndObject();
            }
            var wt = r.Walkthrough;
            w.WriteStartObject("walkthrough");
            w.WriteBoolean("ran", wt.Ran);
            if (wt.WhyNot is not null) w.WriteString("why_not", wt.WhyNot);
            w.WriteString("run_source", wt.RunSource == EvidenceSource.Config ? "config" : "guessed");
            w.WriteStartArray("features");
            foreach (var f in wt.Features)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteString("source", f.Source.ToString().ToLowerInvariant());
                w.WriteStartArray("steps");
                foreach (var s in f.Steps)
                {
                    w.WriteStartObject();
                    w.WriteString("action", s.Action);
                    w.WriteString("result", s.Result);
                    w.WriteBoolean("passed", s.Passed);
                    if (s.Screenshot is not null) w.WriteString("screenshot", s.Screenshot);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            if (r.ProposedRun is not null || r.ProposedFeatures.Count > 0)
            {
                w.WriteStartObject("proposed_config");
                if (r.ProposedRun is { } run)
                {
                    w.WriteStartObject("run");
                    Strings(w, "setup", run.Setup);
                    if (run.Start is not null) w.WriteString("start", run.Start);
                    if (run.Url is not null) w.WriteString("url", run.Url);
                    if (run.Ready is not null) w.WriteString("ready", run.Ready);
                    if (run.Notes is not null) w.WriteString("notes", run.Notes);
                    w.WriteEndObject();
                }
                w.WriteStartArray("features");
                foreach (var f in r.ProposedFeatures)
                {
                    w.WriteStartObject();
                    w.WriteString("name", f.Name);
                    w.WriteString("steps", f.Steps);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }
}
