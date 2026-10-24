using System.Text.Json;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Risk;

public class GateVerdictParserTests
{
    private static GateVerdict Parse(string json) => GateVerdictParser.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Evidenced_low_is_low()
    {
        var v = Parse("""{"risk":"low","summary":"s","reasons":["r"],"checked":["c"]}""");
        Assert.Equal(RiskLevel.Low, v.Risk);
        Assert.Equal(["c"], v.Checked);
        Assert.Equal("s", v.Summary);
        Assert.Null(v.Error);
    }

    [Fact]
    public void High_is_high()
    {
        var v = Parse("""{"risk":"high","summary":"s","reasons":["touches billing"],"checked":["c"]}""");
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Equal(["touches billing"], v.Reasons);
    }

    [Fact]
    public void Low_without_checked_items_is_high()
    {
        var v = Parse("""{"risk":"low","summary":"s","reasons":[],"checked":["  "]}""");
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Contains(v.Reasons, r => r.Contains("without saying what it checked"));
    }

    [Fact]
    public void Low_naming_touched_areas_is_high()
    {
        var v = Parse("""{"risk":"low","summary":"s","reasons":[],"checked":["c"],"risk_areas_touched":["pricing","auth"]}""");
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Contains("reviewer named risk areas touched: pricing, auth", v.Reasons);
    }

    [Fact]
    public void Missing_output_is_high()
    {
        var v = GateVerdictParser.Parse(null);
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Equal("reviewer returned no structured output", v.Error);
        Assert.Equal("reviewer failed: reviewer returned no structured output", v.Summary);
        Assert.Equal(RiskLevel.High, Parse("[1]").Risk);
    }

    [Fact]
    public void Unknown_risk_value_is_high()
    {
        var v = Parse("""{"risk":"medium","summary":"s","reasons":[],"checked":["c"]}""");
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Contains("unrecognised risk 'medium'", v.Reasons);
    }

    [Fact]
    public void Forced_high_appends_reason()
    {
        var v = Parse("""{"risk":"low","summary":"s","reasons":["r"],"checked":["c"]}""").WithForcedHigh("x");
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Equal(["r", "x"], v.Reasons);
    }

    [Theory]
    [InlineData("""{"risk":"low","summary":"s","reasons":[],"checked":["c"],"risk_areas_touched":"pricing"}""", "malformed field 'risk_areas_touched'")]
    [InlineData("""{"risk":"low","summary":"s","reasons":[],"checked":["c"],"risk_areas_touched":[{"name":"pricing"}]}""", "malformed field 'risk_areas_touched'")]
    [InlineData("""{"risk":"low","summary":"s","reasons":[],"checked":["c"],"risk_areas_touched":[1]}""", "malformed field 'risk_areas_touched'")]
    [InlineData("""{"risk":"low","summary":"s","reasons":[],"checked":"c"}""", "malformed field 'checked'")]
    [InlineData("""{"risk":"low","summary":"s","reasons":"r","checked":["c"]}""", "malformed field 'reasons'")]
    [InlineData("""{"risk":"low","reasons":[],"checked":["c"]}""", "'summary'")]
    [InlineData("""{"risk":"low","summary":" ","reasons":[],"checked":["c"]}""", "'summary'")]
    [InlineData("""{"risk":"low","summary":"s","checked":["c"]}""", "missing field 'reasons'")]
    public void Malformed_or_missing_fields_make_low_high(string json, string reason)
    {
        var v = Parse(json);
        Assert.Equal(RiskLevel.High, v.Risk);
        Assert.Contains(v.Reasons, x => x.Contains(reason));
    }
}
