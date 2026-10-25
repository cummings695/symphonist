using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Onboarding;

/// <summary>Statuses use the tracker's spelling; absent fields are null (or empty).</summary>
public sealed record StageMappingProposal(string StageId, IReadOnlyList<string> EntryStatuses, string? OnEnterStatus,
    string? OnGateStatus, string? ApprovedWhenStatus, string Reason);

public sealed record AddedStageProposal(string Id, StageKind Kind, string After, string Reason);

public sealed record RiskAreaProposal(RiskArea Area, string Reason);

/// <summary>
/// What the onboarding agent proposes, already validated item by item (see <see cref="OnboardingProposalParser"/>).
/// <see cref="Notes"/> holds the agent's notes and one line per dropped item.
/// </summary>
public sealed record OnboardingProposal(IReadOnlyList<StageMappingProposal> Stages, IReadOnlyList<AddedStageProposal> AddedStages,
    IReadOnlyList<RiskAreaProposal> RiskAreas, IReadOnlyList<string> Notes, string? Error = null)
{
    public static OnboardingProposal Failed(string error) => new([], [], [], [], error);

    /// <summary>
    /// Inserts the added stages in <paramref name="acceptedAddedStageIds"/> (human gate, after their stage), overwrites
    /// the mapping fields of the stages named, and adds the risk areas whose name is new. Nothing else in
    /// <paramref name="form"/> changes; existing stages and risk areas are never removed.
    /// </summary>
    public void ApplyTo(ProjectForm form, IReadOnlySet<string> acceptedAddedStageIds)
    {
        var inserted = new HashSet<string>();
        foreach (var added in AddedStages.Where(a => acceptedAddedStageIds.Contains(a.Id)))
        {
            if (form.Pipeline.Any(s => s.Id == added.Id)) continue;
            var anchor = form.Pipeline.FindIndex(s => s.Id == added.After);
            if (anchor < 0 || form.Pipeline[anchor].Kind == StageKind.Done) continue;
            // After the stages already inserted behind the same anchor, so proposal order is kept.
            var at = anchor + 1;
            while (at < form.Pipeline.Count && inserted.Contains(form.Pipeline[at].Id)) at++;
            form.Pipeline.Insert(at, new StageForm { Id = added.Id, Kind = added.Kind, Gate = GatePolicy.Human });
            inserted.Add(added.Id);
        }

        foreach (var mapping in Stages)
        {
            // Absent when it maps an added stage the developer did not accept.
            if (form.Pipeline.FirstOrDefault(s => s.Id == mapping.StageId) is not { } stage) continue;
            stage.EntryStatuses = [.. mapping.EntryStatuses];
            stage.OnEnterStatus = mapping.OnEnterStatus;
            stage.OnGateStatus = mapping.OnGateStatus;
            stage.ApprovedWhenStatus = mapping.ApprovedWhenStatus;
        }

        foreach (var proposal in RiskAreas)
        {
            var area = proposal.Area;
            if (form.RiskAreas.Any(a => string.Equals(a.Name, area.Name, StringComparison.OrdinalIgnoreCase))) continue;
            form.RiskAreas.Add(new RiskAreaForm { Name = area.Name, Description = area.Description, Paths = [.. area.Paths] });
        }
    }
}

public static class OnboardingProposalSchema
{
    public const string Json = """{"type":"object","properties":{"stages":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"entry_statuses":{"type":"array","items":{"type":"string"}},"on_enter_status":{"type":"string"},"on_gate_status":{"type":"string"},"approved_when_status":{"type":"string"},"reason":{"type":"string"}},"required":["id","reason"]}},"added_stages":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"kind":{"type":"string","enum":["triage","plan","implement","build","uat","release","deploy"]},"after":{"type":"string"},"reason":{"type":"string"}},"required":["id","kind","after","reason"]}},"risk_areas":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"},"paths":{"type":"array","items":{"type":"string"}},"reason":{"type":"string"}},"required":["name","description","paths","reason"]}},"notes":{"type":"array","items":{"type":"string"}}},"required":["stages","risk_areas"]}""";
}

/// <summary>
/// Reads the onboarding agent's structured output. Fails closed: no output gives <see cref="OnboardingProposal.Failed"/>,
/// and each item that is malformed or does not fit the project is dropped with a note. The result holds values for the
/// form only, never YAML text.
/// </summary>
public static partial class OnboardingProposalParser
{
    public const int MaxAddedStages = 3;
    public const int MaxRiskAreas = 10;
    public const int MaxPathsPerArea = 10;
    public const int MaxAreaNameChars = 40;
    public const int MaxPathChars = 200;
    public const int MaxDropNotes = 50;

    private const int MaxReasonChars = 300;
    private const int MaxNameChars = 100;

    private static readonly StageKind[] AddableKinds =
        [StageKind.Triage, StageKind.Plan, StageKind.Implement, StageKind.Build, StageKind.Uat, StageKind.Release, StageKind.Deploy];

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex StageId();

    private sealed record KnownStage(string Id, StageKind Kind, GatePolicy Gate);

    private enum StatusField { Entry, OnEnter, Gate }

    public static OnboardingProposal Parse(JsonElement? structured, IReadOnlyList<SourceStatus> statuses,
        IReadOnlyList<StageForm> pipeline, IReadOnlyList<RiskAreaForm> existingAreas)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } root)
            return OnboardingProposal.Failed("agent returned no structured output");

        var dropped = new List<string>();
        var added = AddedStages(root, pipeline, dropped);
        var known = pipeline.Select(s => new KnownStage(s.Id, s.Kind, s.Gate))
            .Concat(added.Select(a => new KnownStage(a.Id, a.Kind, GatePolicy.Human)))
            .ToList();
        var stages = Mappings(root, statuses, known, dropped);
        var areas = RiskAreas(root, existingAreas, dropped);

        var agentNotes = ReviewerText.Items(Array(root, "notes", required: false, dropped)
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? ""));
        var dropNotes = dropped.Count <= MaxDropNotes
            ? dropped
            : [.. dropped.Take(MaxDropNotes), $"(+{dropped.Count - MaxDropNotes} more dropped)"];
        return new OnboardingProposal(stages, added, areas, [.. agentNotes, .. dropNotes]);
    }

    private static List<AddedStageProposal> AddedStages(JsonElement root, IReadOnlyList<StageForm> pipeline,
        List<string> dropped)
    {
        var result = new List<AddedStageProposal>();
        var overflow = 0;
        foreach (var item in Array(root, "added_stages", required: false, dropped))
        {
            if (Text(item, "id") is not { } rawId || Text(item, "kind") is not { } rawKind
                || Text(item, "after") is not { } rawAfter || Text(item, "reason") is not { } reason)
            {
                dropped.Add("dropped malformed added stage");
                continue;
            }
            if (result.Count == MaxAddedStages)
            {
                overflow++;
                continue;
            }
            var id = ReviewerText.Line(rawId, MaxNameChars);
            var anchor = pipeline.FirstOrDefault(s => s.Id == rawAfter);
            var why = !StageId().IsMatch(rawId) ? "invalid id"
                : pipeline.Any(s => s.Id == rawId) || result.Any(a => a.Id == rawId) ? "the id is already used"
                : Kind(rawKind) is null ? $"unknown kind '{ReviewerText.Line(rawKind, MaxNameChars)}'"
                : anchor is null ? $"no stage '{ReviewerText.Line(rawAfter, MaxNameChars)}' to follow"
                : anchor.Kind == StageKind.Done ? "it cannot follow the done stage"
                : null;
            if (why is not null) dropped.Add($"dropped added stage '{id}': {why}");
            else result.Add(new AddedStageProposal(rawId, Kind(rawKind)!.Value, rawAfter, ReviewerText.Line(reason, MaxReasonChars)));
        }
        if (overflow > 0) dropped.Add($"dropped {overflow} added stages: at most {MaxAddedStages}");
        return result;
    }

    private static StageKind? Kind(string text)
    {
        foreach (var kind in AddableKinds)
            if (kind.ToString().Equals(text, StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }

    private static List<StageMappingProposal> Mappings(JsonElement root, IReadOnlyList<SourceStatus> statuses,
        IReadOnlyList<KnownStage> known, List<string> dropped)
    {
        var result = new List<StageMappingProposal>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // status -> stage it starts
        foreach (var item in Array(root, "stages", required: true, dropped))
        {
            if (Text(item, "id") is not { } rawId || Text(item, "reason") is not { } reason
                || !OptionalText(item, "on_enter_status", out var onEnter)
                || !OptionalText(item, "on_gate_status", out var onGate)
                || !OptionalText(item, "approved_when_status", out var approved)
                || !OptionalTextList(item, "entry_statuses", out var entries))
            {
                dropped.Add("dropped malformed stage mapping");
                continue;
            }
            var id = ReviewerText.Line(rawId, MaxNameChars);
            if (known.FirstOrDefault(s => s.Id == rawId) is not { } stage)
            {
                dropped.Add($"dropped mapping for unknown stage '{id}'");
                continue;
            }
            if (result.Any(m => m.StageId == stage.Id))
            {
                dropped.Add($"dropped another mapping for stage '{id}'");
                continue;
            }

            var proposed = 0;
            string? Status(string? raw, StatusField field)
            {
                var value = ReviewerText.Line(raw, MaxNameChars);
                if (value.Length == 0) return null; // left out
                proposed++;
                var match = statuses.FirstOrDefault(s => string.Equals(s.Name.Trim(), value, StringComparison.OrdinalIgnoreCase));
                var why = match is null ? "not a status of this project"
                    : stage.Kind == StageKind.Done && field != StatusField.OnEnter ? "a done stage only takes on_enter_status"
                    : field == StatusField.Gate && stage.Gate == GatePolicy.None ? $"stage {stage.Id} has no gate"
                    : null;
                if (why is null) return match!.Name;
                dropped.Add($"dropped status '{value}' for stage {stage.Id}: {why}");
                return null;
            }

            var entryStatuses = new List<string>();
            foreach (var raw in entries)
            {
                if (Status(raw, StatusField.Entry) is not { } name) continue;
                if (claimed.TryGetValue(name, out var other))
                {
                    dropped.Add($"dropped status '{ReviewerText.Line(raw, MaxNameChars)}' for stage {stage.Id}: already starts stage {other}");
                    continue;
                }
                claimed[name] = stage.Id;
                entryStatuses.Add(name);
            }
            var enter = Status(onEnter, StatusField.OnEnter);
            var gate = Status(onGate, StatusField.Gate);
            var approvedWhen = Status(approved, StatusField.Gate);

            // Every proposed status was dropped: keep the stage's current mapping rather than clearing it.
            if (proposed > 0 && entryStatuses.Count == 0 && enter is null && gate is null && approvedWhen is null)
            {
                dropped.Add($"dropped mapping for stage '{stage.Id}': no valid status left");
                continue;
            }
            result.Add(new StageMappingProposal(stage.Id, entryStatuses, enter, gate, approvedWhen,
                ReviewerText.Line(reason, MaxReasonChars)));
        }
        return result;
    }

    private static List<RiskAreaProposal> RiskAreas(JsonElement root, IReadOnlyList<RiskAreaForm> existingAreas,
        List<string> dropped)
    {
        var result = new List<RiskAreaProposal>();
        var overflow = 0;
        foreach (var item in Array(root, "risk_areas", required: true, dropped))
        {
            if (Text(item, "name") is not { } rawName || Text(item, "description") is not { } rawDescription
                || Text(item, "reason") is not { } reason
                || !item.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            {
                dropped.Add("dropped malformed risk area");
                continue;
            }
            if (result.Count == MaxRiskAreas)
            {
                overflow++;
                continue;
            }
            var name = AreaName(ReviewerText.Line(rawName, MaxNameChars));
            var description = ReviewerText.Line(rawDescription, MaxReasonChars);
            var why = name.Length == 0 ? "no name"
                : result.Any(a => a.Area.Name == name) ? "duplicate name"
                : existingAreas.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ? "already configured"
                : description.Length == 0 ? "no description"
                : null;
            if (why is not null)
            {
                dropped.Add(name.Length == 0
                    ? $"dropped risk area '{ReviewerText.Line(rawName, MaxNameChars)}': {why}"
                    : $"dropped risk area '{name}': {why}");
                continue;
            }

            var kept = new List<string>();
            var extraPaths = 0;
            foreach (var path in paths.EnumerateArray())
            {
                if (kept.Count == MaxPathsPerArea)
                {
                    extraPaths++;
                    continue;
                }
                var raw = path.ValueKind == JsonValueKind.String ? path.GetString() ?? "" : path.GetRawText();
                if (path.ValueKind != JsonValueKind.String || !IsSafePath(raw))
                    dropped.Add($"dropped path '{ReviewerText.Line(raw, MaxNameChars)}' of risk area '{name}': unsafe path");
                else if (!kept.Contains(raw)) kept.Add(raw);
            }
            if (extraPaths > 0) dropped.Add($"dropped {extraPaths} paths of risk area '{name}': at most {MaxPathsPerArea}");
            result.Add(new RiskAreaProposal(new RiskArea(name, description, kept), ReviewerText.Line(reason, MaxReasonChars)));
        }
        if (overflow > 0) dropped.Add($"dropped {overflow} risk areas: at most {MaxRiskAreas}");
        return result;
    }

    /// <summary>Lowercase, characters outside <c>[a-z0-9-]</c> become '-', no leading or trailing '-', at most 40 characters.</summary>
    private static string AreaName(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '-');
        var name = sb.ToString().Trim('-');
        return name.Length <= MaxAreaNameChars ? name : name[..MaxAreaNameChars].TrimEnd('-');
    }

    /// <summary>A repo-relative glob: single line, not rooted, no ':', no '\', no ".." segment.</summary>
    private static bool IsSafePath(string path) =>
        path.Length is > 0 and <= MaxPathChars
        && !string.IsNullOrWhiteSpace(path)
        && !path.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
                          || c is '\u2028' or '\u2029' or ':' or '\\')
        && !path.StartsWith('/') && !Path.IsPathRooted(path)
        && !path.Split('/').Contains("..");

    private static IEnumerable<JsonElement> Array(JsonElement root, string name, bool required, List<string> dropped)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            if (required) dropped.Add($"missing field '{name}'");
            return [];
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToList();
        dropped.Add($"malformed field '{name}'");
        return [];
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>False when present but not a string (or null).</summary>
    private static bool OptionalText(JsonElement item, string name, out string? text)
    {
        text = null;
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return true;
    }

    /// <summary>False when present but not an array of strings.</summary>
    private static bool OptionalTextList(JsonElement item, string name, out IReadOnlyList<string> items)
    {
        items = [];
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            return false;
        items = value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return true;
    }
}
