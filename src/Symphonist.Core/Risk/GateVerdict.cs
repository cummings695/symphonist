using System.Text.Json;

namespace Symphonist.Core.Risk;

public sealed record GateVerdict(RiskLevel Risk, string Summary, IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Checked, IReadOnlyList<string> AreasTouched, string? Error = null)
{
    public static GateVerdict Failed(string error) =>
        new(RiskLevel.High, $"reviewer failed: {error}", [error], [], [], error);

    public GateVerdict WithForcedHigh(string reason) =>
        this with { Risk = RiskLevel.High, Reasons = [.. Reasons, reason] };
}

public static class GateReviewSchema
{
    public const string Json = """{"type":"object","properties":{"risk":{"type":"string","enum":["low","high"]},"summary":{"type":"string"},"reasons":{"type":"array","items":{"type":"string"}},"checked":{"type":"array","items":{"type":"string"}},"risk_areas_touched":{"type":"array","items":{"type":"string"}}},"required":["risk","summary","reasons","checked"]}""";
}

public static class GateVerdictParser
{
    /// <summary>Fails closed: anything but a well-formed, evidenced "low" is High (with a reason saying why).</summary>
    public static GateVerdict Parse(JsonElement? structured)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } root)
            return GateVerdict.Failed("reviewer returned no structured output");

        var problems = new List<string>();
        var summary = Str(root, "summary", problems);
        var reasons = List(root, "reasons", required: true, problems);
        var checkedItems = List(root, "checked", required: false, problems);
        var areas = List(root, "risk_areas_touched", required: false, problems);
        var risk = root.TryGetProperty("risk", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";

        var verdict = new GateVerdict(RiskLevel.Low, summary, reasons, checkedItems, areas);
        if (risk == "high") return verdict with { Risk = RiskLevel.High };
        if (risk != "low") return verdict.WithForcedHigh($"unrecognised risk '{risk}'");
        if (problems.Count > 0)
            return verdict with { Risk = RiskLevel.High, Reasons = [.. reasons, .. problems] };
        if (checkedItems.Count == 0)
            return verdict.WithForcedHigh("reviewer reported low risk without saying what it checked");
        if (areas.Count > 0)
            return verdict.WithForcedHigh($"reviewer named risk areas touched: {string.Join(", ", areas)}");
        return verdict;
    }

    private static string Str(JsonElement root, string name, List<string> problems)
    {
        if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()))
            return p.GetString()!;
        problems.Add($"missing or malformed field '{name}'");
        return "";
    }

    private static IReadOnlyList<string> List(JsonElement root, string name, bool required, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var p))
        {
            if (required) problems.Add($"missing field '{name}'");
            return [];
        }
        if (p.ValueKind != JsonValueKind.Array || p.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            problems.Add($"malformed field '{name}'");
            return [];
        }
        return p.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
