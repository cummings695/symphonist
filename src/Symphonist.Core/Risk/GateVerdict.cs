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

        var summary = Str(root, "summary");
        var reasons = List(root, "reasons");
        var checkedItems = List(root, "checked");
        var areas = List(root, "risk_areas_touched");
        var risk = Str(root, "risk");

        var verdict = new GateVerdict(RiskLevel.Low, summary, reasons, checkedItems, areas);
        if (risk == "high") return verdict with { Risk = RiskLevel.High };
        if (risk != "low") return verdict.WithForcedHigh($"unrecognised risk '{risk}'");
        if (checkedItems.Count == 0)
            return verdict.WithForcedHigh("reviewer reported low risk without saying what it checked");
        if (areas.Count > 0)
            return verdict.WithForcedHigh($"reviewer named risk areas touched: {string.Join(", ", areas)}");
        return verdict;
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static IReadOnlyList<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return [];
        return p.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
