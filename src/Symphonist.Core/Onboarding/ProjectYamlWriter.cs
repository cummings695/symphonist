using System.Globalization;
using System.Text;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Symphonist.Core.Onboarding;

/// <summary>Renders a <see cref="ProjectForm"/> as project-file YAML, section by section.</summary>
public static class ProjectYamlWriter
{
    public static readonly IReadOnlyList<string> SectionOrder =
        ["id", "name", "tracker", "repo", "build", "agent", "maxAttemptsPerStage", "pipeline", "riskAreas", "review", "prompts"];

    private static readonly string[] ProviderSlots = ["tracker", "repo", "build"];

    private static readonly EmitterSettings Settings = EmitterSettings.Default.WithBestWidth(int.MaxValue);

    /// <summary>
    /// Each section's YAML text ("key: …" plus nested lines, ending "\n"), or null when the section is omitted (empty
    /// riskAreas/prompts, values equal to loader defaults). Provider sections: "type" then the provider's fields in order.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Sections(ProjectForm form, ProviderRegistry providers)
    {
        var tracker = providers.Tracker(form.Tracker.Type);
        var repo = providers.Repo(form.Repo.Type);
        var build = providers.Build(form.Build.Type);
        return new Dictionary<string, string?>
        {
            ["id"] = $"id: {Scalar(form.Id)}\n",
            ["name"] = string.IsNullOrWhiteSpace(form.Name) || form.Name == form.Id ? null : $"name: {Scalar(form.Name)}\n",
            ["tracker"] = ProviderSection("tracker", form.Tracker, tracker.Fields),
            ["repo"] = ProviderSection("repo", form.Repo, repo.Fields),
            ["build"] = ProviderSection("build", form.Build, build.Fields),
            ["agent"] = Agent(form.Agent),
            ["maxAttemptsPerStage"] = form.MaxAttemptsPerStage == 3 ? null
                : $"maxAttemptsPerStage: {form.MaxAttemptsPerStage.ToString(CultureInfo.InvariantCulture)}\n",
            ["pipeline"] = Pipeline(form.Pipeline),
            ["riskAreas"] = RiskAreas(form.RiskAreas),
            ["review"] = Review(form.Review),
            ["prompts"] = Prompts(form.Prompts),
        };
    }

    /// <summary>A complete new file: a header comment and each section preceded by a short explanatory comment.</summary>
    public static string NewFile(ProjectForm form, ProviderRegistry providers)
    {
        var sections = Sections(form, providers);
        var text = new StringBuilder();
        text.Append("# Symphonist project. Field names are camelCase. You can edit this file by hand: the setup screen only\n");
        text.Append("# rewrites the sections you change there, and keeps comments outside them.\n");
        foreach (var key in SectionOrder)
        {
            if (sections[key] is not { } section) continue;
            text.Append('\n');
            foreach (var line in Comment(key, form, providers)) text.Append("# ").Append(line).Append('\n');
            text.Append(section);
        }
        return text.ToString();
    }

    /// <summary>
    /// Rewrites only the sections whose rendering differs between <paramref name="original"/> and
    /// <paramref name="edited"/>; every other byte of <paramref name="existingText"/> is kept. A rewritten provider
    /// section keeps the keys of the existing section its provider's fields do not model.
    /// </summary>
    /// <exception cref="ProjectYamlEditException">The file cannot be spliced safely.</exception>
    public static string Update(string existingText, ProjectForm original, ProjectForm edited, ProviderRegistry providers)
    {
        var document = ProjectYamlDocument.Parse(existingText);
        var before = Sections(original, providers);
        var after = Sections(edited, providers);
        var changed = new Dictionary<string, string?>();
        foreach (var key in SectionOrder)
            if (before[key] != after[key])
                changed[key] = after[key];

        Dictionary<string, object?>? raw = null;
        foreach (var slot in ProviderSlots)
        {
            if (!changed.TryGetValue(slot, out var text) || text is null) continue;
            raw ??= Deserialize(existingText);
            if (raw.GetValueOrDefault(slot) is not Dictionary<object, object?> existing) continue;
            var section = slot switch { "tracker" => edited.Tracker, "repo" => edited.Repo, _ => edited.Build };
            if (!Equals(existing.GetValueOrDefault("type"), section.Type)) continue;
            var fields = slot switch
            {
                "tracker" => providers.Tracker(section.Type).Fields,
                "repo" => providers.Repo(section.Type).Fields,
                _ => providers.Build(section.Type).Fields,
            };
            // Unmodelled keys are copied from their source text when the section is a block mapping (keeping block
            // scalars and comments), else rendered from their parsed value.
            var sources = document.Entries(slot);
            var extra = new StringBuilder(text);
            foreach (var (key, value) in existing)
            {
                var name = key as string;
                if (name is null || name == "type" || fields.Any(f => f.Key == name)) continue;
                if (sources is not null && sources.TryGetValue(name, out var source)) extra.Append(source);
                else extra.Append($"  {Scalar(name)}: {Value(value)}\n");
            }
            changed[slot] = extra.ToString();
        }

        return document.Replace(changed);
    }

    private static Dictionary<string, object?> Deserialize(string text)
    {
        try
        {
            return new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(text) ?? [];
        }
        catch (Exception ex) when (ex is not ProjectYamlEditException)
        {
            throw new ProjectYamlEditException(
                $"cannot update this project file safely because it could not be read ({ex.Message}); edit this file by hand");
        }
    }

    private static string ProviderSection(string slot, ProviderSectionForm section, IReadOnlyList<ProviderField> fields)
    {
        var text = new StringBuilder($"{slot}:\n  type: {Scalar(section.Type)}\n");
        foreach (var field in fields)
        {
            if (field.Kind == ProviderFieldKind.List)
            {
                if (section.Lists.TryGetValue(field.Key, out var items) && items.Count > 0)
                    text.Append($"  {field.Key}: {List(items)}\n");
                continue;
            }
            if (!section.Values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)
                || value == field.Default) continue;
            text.Append($"  {field.Key}: ");
            text.Append(field.Kind == ProviderFieldKind.MultilineText ? Multiline(value, "    ") : $"{Scalar(value)}\n");
        }
        return text.ToString();
    }

    private static string Agent(AgentForm agent)
    {
        var text = new StringBuilder("agent:\n  type: claude-code\n");
        if (agent.Isolation != Isolation.Container) text.Append($"  isolation: {Enum(agent.Isolation)}\n");
        if (agent.AllowedDomains.Count > 0) text.Append($"  allowedDomains: {List(agent.AllowedDomains)}\n");
        if (agent.AllowedTools.Count > 0) text.Append($"  allowedTools: {List(agent.AllowedTools)}\n");
        if (agent.TimeoutMinutes != 60)
            text.Append($"  timeoutMinutes: {agent.TimeoutMinutes.ToString(CultureInfo.InvariantCulture)}\n");
        if (!string.IsNullOrWhiteSpace(agent.Model)) text.Append($"  model: {Scalar(agent.Model)}\n");
        if (!string.IsNullOrWhiteSpace(agent.Image)) text.Append($"  image: {Scalar(agent.Image)}\n");
        if (agent.PackagesApt.Count + agent.PackagesPip.Count + agent.PackagesNpm.Count > 0)
        {
            text.Append("  packages:\n");
            if (agent.PackagesApt.Count > 0) text.Append($"    apt: {List(agent.PackagesApt)}\n");
            if (agent.PackagesPip.Count > 0) text.Append($"    pip: {List(agent.PackagesPip)}\n");
            if (agent.PackagesNpm.Count > 0) text.Append($"    npm: {List(agent.PackagesNpm)}\n");
        }
        return text.ToString();
    }

    private static string Pipeline(List<StageForm> stages)
    {
        var text = new StringBuilder("pipeline:\n");
        foreach (var stage in stages)
        {
            var entries = new List<string> { $"id: {FlowScalar(stage.Id)}", $"kind: {Enum(stage.Kind)}" };
            if (stage.Gate != GatePolicy.None)
                entries.Add($"gate: {(stage.Gate == GatePolicy.AutoWhenLowRisk ? "auto" : Enum(stage.Gate))}");
            if (stage.EntryStatuses.Count > 0) entries.Add($"entryStatuses: {List(stage.EntryStatuses)}");
            if (!string.IsNullOrEmpty(stage.OnEnterStatus)) entries.Add($"onEnterStatus: {FlowScalar(stage.OnEnterStatus)}");
            if (!string.IsNullOrEmpty(stage.OnGateStatus)) entries.Add($"onGateStatus: {FlowScalar(stage.OnGateStatus)}");
            if (!string.IsNullOrEmpty(stage.ApprovedWhenStatus))
                entries.Add($"approvedWhenStatus: {FlowScalar(stage.ApprovedWhenStatus)}");
            text.Append($"  - {{ {string.Join(", ", entries)} }}\n");
        }
        return text.ToString();
    }

    private static string? RiskAreas(List<RiskAreaForm> areas)
    {
        if (areas.Count == 0) return null;
        var text = new StringBuilder("riskAreas:\n");
        foreach (var area in areas)
        {
            text.Append($"  - name: {Scalar(area.Name)}\n");
            text.Append("    description: ").Append(Multiline(area.Description, "      "));
            if (area.Paths.Count > 0) text.Append($"    paths: {List(area.Paths)}\n");
        }
        return text.ToString();
    }

    private static string Review(ReviewForm review)
    {
        var text = new StringBuilder($"review:\n  mode: {Enum(review.Mode)}\n");
        if (!string.IsNullOrWhiteSpace(review.PromptPath)) text.Append($"  prompt: {Scalar(review.PromptPath)}\n");
        var agent = new List<string>();
        if (!string.IsNullOrWhiteSpace(review.Model)) agent.Add($"model: {FlowScalar(review.Model)}");
        if (review.TimeoutMinutes != 20)
            agent.Add($"timeoutMinutes: {review.TimeoutMinutes.ToString(CultureInfo.InvariantCulture)}");
        if (review.AllowedTools.Count > 0) agent.Add($"allowedTools: {List(review.AllowedTools)}");
        if (agent.Count > 0) text.Append($"  agent: {{ {string.Join(", ", agent)} }}\n");
        return text.ToString();
    }

    private static string? Prompts(Dictionary<StageKind, string> prompts)
    {
        if (prompts.Count == 0) return null;
        var text = new StringBuilder("prompts:\n");
        foreach (var (kind, path) in prompts.OrderBy(e => e.Key))
            text.Append($"  {Enum(kind)}: {Scalar(path)}\n");
        return text.ToString();
    }

    private static IEnumerable<string> Comment(string key, ProjectForm form, ProviderRegistry providers) => key switch
    {
        "id" => ["Unique project id, used in CLI commands. Stored tickets are keyed by it: do not change it."],
        "name" => ["Display name (defaults to id)."],
        "tracker" => [$"Where tickets come from: {providers.Tracker(form.Tracker.Type).DisplayName}. tokenSecret names a secret",
            "in the secret store (symphonist secret set <name>); the token itself is never written here."],
        "repo" => [$"The repository the agents work in: {providers.Repo(form.Repo.Type).DisplayName}. localPath is an existing",
            "local clone; a relative path resolves against the folder holding this file."],
        "build" => [$"Build and deployment status is read from {providers.Build(form.Build.Type).DisplayName}."],
        "agent" => ["The coding agent. isolation: container (default, Docker sandbox) or host (requires allowedTools).",
            "allowedDomains are the extra hosts the container may reach."],
        "maxAttemptsPerStage" => ["Failed attempts per stage before the ticket is Blocked. Default: 3."],
        "pipeline" =>
        [
            "Stages run in order; the last one must be kind \"done\".",
            "  gate:                human = wait for approval; none (default) = continue automatically;",
            "                       auto = approve when the reviewer judges low risk (needs review.mode: active)",
            "  entryStatuses:       tracker statuses that mean \"start at this stage\" when a ticket is first picked up",
            "  onEnterStatus:       move the card to this status when the stage starts",
            "  onGateStatus:        move the card to this status when the stage is waiting for your approval",
            "  approvedWhenStatus:  moving the card to this status approves the stage",
        ],
        "riskAreas" => ["Areas the risk reviewer must treat as high risk. A change touching one is never auto-approved."],
        "review" => ["Risk reviewer: off (default), shadow (judges gates, approves nothing) or active (also approves",
            "gate: auto stages judged low risk)."],
        "prompts" => ["Prompt overrides per stage kind, as files relative to this file."],
        _ => [],
    };

    private static string Enum<T>(T value) where T : struct, System.Enum => value.ToString().ToLowerInvariant();

    private static string List(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(FlowScalar))}]";

    /// <summary>A scalar, map or list read from an existing file, as YAML (collections in flow style).</summary>
    private static string Value(object? value, bool flow = false) => value switch
    {
        null => "~",
        Dictionary<object, object?> map =>
            $"{{ {string.Join(", ", map.Select(e => $"{Value(e.Key, true)}: {Value(e.Value, true)}"))} }}",
        List<object?> list => $"[{string.Join(", ", list.Select(v => Value(v, true)))}]",
        _ => Scalar(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", flow),
    };

    /// <summary>
    /// <paramref name="value"/> as a YAML scalar: YamlDotNet's emitter picks plain style when it can and quotes
    /// otherwise. <paramref name="flow"/> = inside a flow collection, where ", [ ] { }" need quotes too. Values the
    /// loader would read as null are quoted.
    /// </summary>
    internal static string Scalar(string value, bool flow)
    {
        var style = value is "" or "~" or "null" or "Null" or "NULL" ? ScalarStyle.DoubleQuoted : ScalarStyle.Any;
        var writer = new StringWriter();
        var emitter = new Emitter(writer, Settings);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        if (flow) emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, style, true, true));
        if (flow) emitter.Emit(new SequenceEnd());
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        // Keep the text exactly (no Trim: it would also strip Unicode spaces from the value); drop only the line
        // endings and the "..." end marker a lone document scalar may get.
        var lines = writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && (lines[^1].Length == 0 || lines[^1] == "...")) lines.RemoveAt(lines.Count - 1);
        if (lines.Count != 1 || lines[0].IndexOfAny(['\r', '\u0085', '\u2028', '\u2029']) >= 0)
            throw new ProjectYamlEditException("a value contains a line break and cannot be written on one line");
        return flow ? lines[0][1..^1] : lines[0];
    }

    private static string Scalar(string value) => Scalar(value, flow: false);

    /// <summary>
    /// The value after "key: " up to and including the line end: a one-line scalar, or, when the value holds "\n"
    /// line breaks, a literal block scalar whose lines are indented by <paramref name="indent"/> (two more spaces than
    /// the key). The chomping indicator keeps the trailing line breaks exactly; an indentation indicator is added when
    /// the value starts with a space or a line break, where YAML could not detect the indentation itself.
    /// </summary>
    private static string Multiline(string value, string indent)
    {
        if (!value.Contains('\n')) return $"{Scalar(value)}\n";
        if (value.IndexOfAny(['\r', '\u0085', '\u2028', '\u2029']) >= 0 || value.Any(c => c != '\n' && char.IsControl(c)))
            throw new ProjectYamlEditException("a value contains a line break or control character that cannot be written");
        string chomp, body;
        if (!value.EndsWith('\n')) (chomp, body) = ("-", value);
        else if (value.EndsWith("\n\n", StringComparison.Ordinal)) (chomp, body) = ("+", value[..^1]);
        else (chomp, body) = ("", value[..^1]);
        var text = new StringBuilder("|");
        if (value[0] is ' ' or '\n') text.Append('2');
        text.Append(chomp).Append('\n');
        foreach (var line in body.Split('\n'))
        {
            if (line.Length > 0) text.Append(indent).Append(line);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string FlowScalar(string value) => Scalar(value, flow: true);
}
