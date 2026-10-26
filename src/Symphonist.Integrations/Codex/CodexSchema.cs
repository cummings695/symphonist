using System.Text.Json;
using System.Text.Json.Nodes;

namespace Symphonist.Integrations.Codex;

/// <summary>
/// Adapts the shared output schemas to Codex's strict structured-output mode, which needs every object to list all its
/// properties as required and forbid extras. Optional properties become nullable instead; <see cref="DropNulls"/> undoes
/// that on the way back so the result reads like Claude Code's.
/// </summary>
public static class CodexSchema
{
    public static string Strict(string schemaJson)
    {
        var root = JsonNode.Parse(schemaJson)!;
        Adapt(root);
        return root.ToJsonString();
    }

    private static void Adapt(JsonNode? node)
    {
        if (node is not JsonObject schema) return;

        if (schema["properties"] is JsonObject props)
        {
            var originallyRequired = (schema["required"] as JsonArray)?
                .Select(r => r?.GetValue<string>()).ToHashSet() ?? [];
            foreach (var (name, prop) in props.ToList())
            {
                if (prop is not JsonObject p) continue;
                Adapt(p);
                if (!originallyRequired.Contains(name)) MakeNullable(p);
            }
            schema["required"] = new JsonArray(props.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)).ToArray());
            schema["additionalProperties"] = false;
        }
        if (schema["items"] is JsonObject items) Adapt(items);
    }

    private static void MakeNullable(JsonObject p)
    {
        switch (p["type"])
        {
            case JsonValue v when v.TryGetValue<string>(out var type):
                p["type"] = new JsonArray(type, "null");
                break;
            case JsonArray types when !types.Any(t => t?.GetValue<string>() == "null"):
                types.Add("null");
                break;
        }
        if (p["enum"] is JsonArray values && !values.Any(v => v is null)) values.Add(null);
    }

    /// <summary>A copy of <paramref name="value"/> with every null-valued object property removed, recursively.</summary>
    public static JsonElement DropNulls(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText());
        Strip(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) o.Remove(key);
                foreach (var (_, child) in o) Strip(child);
                break;
            case JsonArray a:
                foreach (var child in a) Strip(child);
                break;
        }
    }
}
