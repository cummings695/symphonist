using System.Text.Json;
using System.Text.Json.Nodes;
using Symphonist.Core.Agents;
using Symphonist.Core.Evidence;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Risk;
using Symphonist.Integrations.Codex;

namespace Symphonist.Integrations.Tests.Codex;

public class CodexSchemaTests
{
    public static IEnumerable<object[]> AllSchemas() =>
    [
        [StageOutputSchema.Json], [PrFixOutputSchema.Json], [GateReviewSchema.Json],
        [EvidenceSchema.Json], [ContainerCheckSchema.Json], [OnboardingProposalSchema.Json],
    ];

    [Theory, MemberData(nameof(AllSchemas))]
    public void Strict_schema_requires_every_property_and_forbids_extras(string schema)
    {
        var strict = JsonNode.Parse(CodexSchema.Strict(schema))!;
        var objects = 0;
        Walk(strict);
        Assert.True(objects > 0);

        void Walk(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                if (o["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "object")
                {
                    objects++;
                    Assert.False(o["additionalProperties"]!.GetValue<bool>());
                    var props = o["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
                    var required = o["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet();
                    Assert.True(props.IsSubsetOf(required));
                }
                foreach (var (_, child) in o) Walk(child);
            }
            else if (node is JsonArray a) foreach (var child in a) Walk(child);
        }
    }

    [Fact]
    public void Optional_property_becomes_nullable_required_property()
    {
        var strict = JsonNode.Parse(CodexSchema.Strict(StageOutputSchema.Json))!;
        var specPath = strict["properties"]!["spec_path"]!["type"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(["string", "null"], specPath);
        Assert.Contains("spec_path", strict["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("string", strict["properties"]!["status"]!["type"]!.GetValue<string>());
        Assert.Equal(["completed", "failed"],
            strict["properties"]!["status"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Optional_enum_property_gets_null_in_enum()
    {
        var strict = JsonNode.Parse(CodexSchema.Strict(EvidenceSchema.Json))!;
        var runSource = strict["properties"]!["walkthrough"]!["properties"]!["run_source"]!;
        Assert.Equal(["config", "guessed", null],
            runSource["enum"]!.AsArray().Select(n => n?.GetValue<string>()));
        Assert.Equal(["string", "null"], runSource["type"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void DropNulls_removes_null_properties_recursively()
    {
        using var doc = JsonDocument.Parse("""{"status":"completed","summary":"s","spec_path":null,"replies":[{"comment_id":"1","reply":"r","x":null}]}""");
        var result = CodexSchema.DropNulls(doc.RootElement);
        Assert.False(result.TryGetProperty("spec_path", out _));
        var reply = result.GetProperty("replies")[0];
        Assert.False(reply.TryGetProperty("x", out _));
        Assert.Equal("r", reply.GetProperty("reply").GetString());
    }

    [Fact]
    public void Null_optional_fields_read_as_absent_stage_output()
    {
        using var doc = JsonDocument.Parse("""{"status":"completed","summary":"s","spec_path":null,"tests_passed":null}""");
        var output = StageOutputSchema.Read(CodexSchema.DropNulls(doc.RootElement));
        Assert.NotNull(output);
        Assert.Equal("completed", output.Status);
        Assert.Null(output.SpecPath);
    }
}
