using System.Text.RegularExpressions;
using Symphonist.Core.Integrations.Providers;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Symphonist.Core.Config;

public sealed class ProjectConfigException(string message) : Exception(message);

public static partial class ProjectConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Loads one project file; the tracker, repo and build sections are parsed by <paramref name="providers"/>.</summary>
    public static ProjectConfig Load(string yamlPath, ProviderRegistry providers)
    {
        var fullPath = Path.GetFullPath(yamlPath);
        return Parse(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!, fullPath, providers);
    }

    /// <summary>
    /// Same validation as <see cref="Load"/> for YAML text: relative paths resolve against <paramref name="configDirectory"/>
    /// and errors are prefixed "{sourceName}: ".
    /// </summary>
    public static ProjectConfig Parse(string yaml, string configDirectory, string sourceName, ProviderRegistry providers)
    {
        try
        {
            var dto = Deserializer.Deserialize<Dto?>(yaml) ?? throw new ProjectConfigException("file is empty");
            return Map(dto, configDirectory, providers);
        }
        catch (YamlException ex)
        {
            throw new ProjectConfigException($"{sourceName}: {ex.Message}");
        }
        catch (ProjectConfigException ex)
        {
            throw new ProjectConfigException($"{sourceName}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z")]
    private static partial Regex ProjectId();

    [GeneratedRegex(@"\A@[A-Za-z0-9][A-Za-z0-9_-]{0,38}\z")]
    private static partial Regex CommentMarkerPattern();

    /// <summary>"@" and a name of letters, digits, '-' and '_' starting with a letter or digit (at most 39 characters in all).</summary>
    public static bool IsValidCommentMarker(string marker) => CommentMarkerPattern().IsMatch(marker);

    /// <summary>The error for an invalid <c>commentMarker</c>, naming the field and the rule.</summary>
    public static string CommentMarkerRule(string marker) =>
        $"invalid commentMarker '{marker}': it must start with '@' followed by a letter or digit, then letters, digits, '-' or '_' (at most 39 characters in all)";

    /// <summary>The id keys every stored ticket and names work and log folders, so only plain names are accepted.</summary>
    public static bool IsValidProjectId(string id) => ProjectId().IsMatch(id) && !IsReservedDeviceName(id);

    /// <summary>
    /// Windows device names (CON, PRN, AUX, NUL, COM0-9, LPT0-9, CONIN$, CONOUT$; any case): the id names the project's
    /// file and folders, which Windows cannot create under these names.
    /// </summary>
    public static bool IsReservedDeviceName(string id) =>
        id.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
        || (id.Length == 4 && (id.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(id[3]));

    public static IReadOnlyList<ProjectConfig> LoadAll(string projectsDirectory, ProviderRegistry providers) =>
        ProjectFiles(projectsDirectory).Select(f => Load(f, providers)).ToList();

    /// <summary>The project files (<c>*.yaml</c>, <c>*.yml</c>) in <paramref name="projectsDirectory"/>, in ordinal order.</summary>
    public static IReadOnlyList<string> ProjectFiles(string projectsDirectory)
    {
        if (!Directory.Exists(projectsDirectory)) return [];
        return Directory.EnumerateFiles(projectsDirectory)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static ProjectConfig Map(Dto d, string dir, ProviderRegistry providers)
    {
        var id = Required(d.Id, "id");
        if (IsReservedDeviceName(id))
            throw new ProjectConfigException($"invalid id '{id}': a reserved device name on Windows; pick another id");
        if (!IsValidProjectId(id))
            throw new ProjectConfigException(
                $"invalid id '{id}': use letters, digits, '-' and '_', starting with a letter or digit (at most 64)");
        var trackerValues = d.Tracker ?? throw Missing("tracker");
        var repoValues = d.Repo ?? throw Missing("repo");
        var buildValues = d.Build ?? throw Missing("build");
        var agent = d.Agent ?? throw Missing("agent");

        var trackerSection = new TrackerSection(trackerValues, dir);
        var tracker = providers.Tracker(trackerSection.Type).Parse(trackerSection);
        var repoSection = new RepoSection(repoValues, dir);
        var repo = providers.Repo(repoSection.Type).Parse(repoSection);
        var buildSection = new BuildSection(buildValues, dir);
        var buildProvider = providers.Build(buildSection.Type);
        var build = buildProvider.Parse(buildSection);
        if (buildProvider.RepoType != repo.Type)
            throw new ProjectConfigException($"build.type '{build.Type}' needs repo.type '{buildProvider.RepoType}'");

        var agentType = SupportedAgentType("agent.type", Required(agent.Type, "agent.type"));

        var isolation = ParseEnum<Isolation>(agent.Isolation ?? "container", "isolation");
        IReadOnlyList<string> allowedTools = Items(agent.AllowedTools, "agent.allowedTools");
        if (isolation == Isolation.Host && allowedTools.Count == 0)
            throw new ProjectConfigException("host isolation requires agent.allowedTools");

        var agentSettings = new AgentSettings(agentType, isolation, Items(agent.AllowedDomains, "agent.allowedDomains"),
            allowedTools, agent.TimeoutMinutes ?? 60, agent.Model, agent.Image)
        {
            Packages = MapPackages(agent.Packages),
            Codex = MapCodex(agent.Codex),
        };
        var pipeline = MapPipeline(d.Pipeline, agentSettings);

        var commentMarker = d.CommentMarker ?? Integrations.TrackerComments.DefaultMarker;
        if (!IsValidCommentMarker(commentMarker)) throw new ProjectConfigException(CommentMarkerRule(commentMarker));

        var prompts = new Dictionary<StageKind, string>();
        foreach (var (kind, path) in d.Prompts ?? [])
            prompts[ParseEnum<StageKind>(kind, "prompt stage kind")] = Path.GetFullPath(Required(path, $"prompts.{kind}"), dir);

        var config = new ProjectConfig(
            id,
            d.Name ?? id,
            tracker,
            repo,
            build,
            agentSettings,
            d.MaxAttemptsPerStage ?? 3,
            pipeline,
            prompts,
            dir)
        {
            RiskAreas = MapRiskAreas(d.RiskAreas),
            Review = MapReview(d.Review, agentSettings, dir),
            Evidence = MapEvidence(d.Evidence, agentSettings, dir),
            PrComments = new PrCommentsConfig(d.PrComments?.FromOthers ?? false),
            CommentMarker = commentMarker,
        };

        var typesInUse = config.AgentTypesInUse;
        CheckIsolation(isolation, typesInUse);
        if (agent.Codex is not null && !typesInUse.Contains(AgentTypes.Codex))
            throw new ProjectConfigException(AgentRules.StaleCodexSettings);
        return config;
    }

    /// <summary>Every provider some job uses must be allowed under the project's isolation (codex: containers only).</summary>
    private static void CheckIsolation(Isolation isolation, IEnumerable<string> typesInUse)
    {
        if (typesInUse.Any(t => !AgentRules.AllowedIn(t, isolation)))
            throw new ProjectConfigException(AgentRules.CodexNeedsContainer);
    }

    private static CodexSettings MapCodex(CodexDto? c)
    {
        if (c is null) return CodexSettings.Default;
        var auth = (c.Auth ?? "api-key").ToLowerInvariant() switch
        {
            "api-key" => CodexAuth.ApiKey,
            "chatgpt" => CodexAuth.ChatGpt,
            _ => throw new ProjectConfigException($"unknown agent.codex.auth '{c.Auth}'"),
        };
        var secret = string.IsNullOrWhiteSpace(c.ApiKeySecret) ? null : c.ApiKeySecret;
        if (secret is not null && auth != CodexAuth.ApiKey)
            throw new ProjectConfigException(AgentRules.ApiKeySecretNeedsApiKey);
        return new CodexSettings(auth, secret ?? CodexSettings.DefaultApiKeySecret);
    }

    /// <summary>
    /// The project agent with an override applied: type, model and timeout may differ, the rest is the project's. A
    /// changed type without a model gets no model (provider default), since model names don't cross providers.
    /// </summary>
    private static AgentSettings Override(AgentSettings project, string field, string? type, string? model,
        int? timeoutMinutes, int defaultTimeoutMinutes)
    {
        var effectiveType = type is null ? project.Type : SupportedAgentType(field, type);
        return project with
        {
            Type = effectiveType,
            Model = model ?? (effectiveType == project.Type ? project.Model : null),
            TimeoutMinutes = timeoutMinutes ?? defaultTimeoutMinutes,
        };
    }

    private static string SupportedAgentType(string field, string type) =>
        AgentRules.IsSupported(type) ? type : throw new ProjectConfigException(AgentRules.UnsupportedType(field, type));

    // Every item ends up as one argument of an install command in a generated Dockerfile, so only plain package
    // names with an optional version are accepted: no spaces, quotes, shell characters or leading '-'.
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9+.-]*(=[A-Za-z0-9.+:~-]+)?\z")]
    private static partial Regex AptPackage();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*(\[[A-Za-z0-9._,-]+\])?((==|>=|<=|~=|!=|>|<)[A-Za-z0-9.*+!-]+(,(==|>=|<=|~=|!=|>|<)[A-Za-z0-9.*+!-]+)*)?\z")]
    private static partial Regex PipPackage();

    [GeneratedRegex(@"\A(@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*(@[A-Za-z0-9._~^+-]+)?\z")]
    private static partial Regex NpmPackage();

    private static AgentPackages MapPackages(PackagesDto? p)
    {
        if (p is null) return AgentPackages.None;
        return new AgentPackages(Packages(p.Apt, "apt", AptPackage()), Packages(p.Pip, "pip", PipPackage()),
            Packages(p.Npm, "npm", NpmPackage()));
    }

    private static List<string> Packages(List<string>? items, string installer, Regex valid)
    {
        var list = Items(items, $"agent.packages.{installer}");
        foreach (var item in list)
            if (!valid.IsMatch(item))
                throw new ProjectConfigException($"invalid {installer} package '{item}' in agent.packages.{installer}");
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<RiskArea> MapRiskAreas(List<RiskAreaDto?>? areas)
    {
        var result = new List<RiskArea>();
        var seen = new HashSet<string>();
        for (var i = 0; areas is not null && i < areas.Count; i++)
        {
            var a = areas[i] ?? throw new ProjectConfigException($"riskAreas[{i}] is empty");
            var name = Required(a.Name, "riskAreas[].name");
            if (!seen.Add(name)) throw new ProjectConfigException($"duplicate risk area '{name}'");
            result.Add(new RiskArea(name, Required(a.Description, "riskAreas[].description"),
                Items(a.Paths, $"riskAreas '{name}' paths")));
        }
        return result;
    }

    private static ReviewConfig MapReview(ReviewDto? r, AgentSettings agent, string dir)
    {
        var mode = ParseEnum<ReviewMode>(r?.Mode ?? "off", "review.mode");
        var ra = r?.Agent;
        var tools = ra?.AllowedTools is null ? ReviewConfig.ReadOnlyTools : Items(ra.AllowedTools, "review.agent.allowedTools");
        if (tools.Count == 0) tools = ReviewConfig.ReadOnlyTools;
        var settings = Override(agent, "review.agent.type", ra?.Type, ra?.Model, ra?.TimeoutMinutes, 20)
            with { AllowedTools = tools };
        var prompt = r?.Prompt is null ? null : Path.GetFullPath(Required(r.Prompt, "review.prompt"), dir);
        return new ReviewConfig(mode, settings, prompt);
    }

    private static EvidenceConfig MapEvidence(EvidenceDto? e, AgentSettings agent, string dir)
    {
        var mode = (e?.Mode ?? "high-risk").ToLowerInvariant() switch
        {
            "off" => EvidenceMode.Off,
            "high-risk" => EvidenceMode.HighRisk,
            "always" => EvidenceMode.Always,
            _ => throw new ProjectConfigException($"unknown evidence.mode '{e!.Mode}'"),
        };

        var run = e?.Run is null ? EvidenceRun.None : MapEvidenceRun(e.Run);

        var features = new List<EvidenceFeature>();
        var dtos = e?.Features ?? [];
        if (dtos.Count > EvidenceLimits.MaxFeatures)
            throw new ProjectConfigException($"evidence.features has {dtos.Count} items (at most {EvidenceLimits.MaxFeatures})");
        for (var i = 0; i < dtos.Count; i++)
        {
            var f = dtos[i] ?? throw new ProjectConfigException($"evidence.features[{i}] is empty");
            features.Add(new EvidenceFeature(
                SingleLine(Required(f.Name, "evidence.features[].name"), "evidence.features[].name", EvidenceLimits.MaxFeatureNameChars),
                Limited(Required(f.Steps, "evidence.features[].steps"), "evidence.features[].steps", EvidenceLimits.MaxFeatureStepsChars)));
        }

        var ea = e?.Agent;
        var settings = Override(agent, "evidence.agent.type", ea?.Type, ea?.Model, ea?.TimeoutMinutes,
            EvidenceLimits.DefaultTimeoutMinutes);
        var prompt = e?.Prompt is null ? null : Path.GetFullPath(Required(e.Prompt, "evidence.prompt"), dir);
        return new EvidenceConfig(mode, run, features, settings, prompt);
    }

    private static EvidenceRun MapEvidenceRun(EvidenceRunDto r)
    {
        var setup = Items(r.Setup, "evidence.run.setup");
        if (setup.Count > EvidenceLimits.MaxSetupCommands)
            throw new ProjectConfigException(
                $"evidence.run.setup has {setup.Count} commands (at most {EvidenceLimits.MaxSetupCommands})");
        foreach (var command in setup)
            SingleLine(command, "evidence.run.setup", EvidenceLimits.MaxRunFieldChars);
        return new EvidenceRun(setup,
            OptionalLine(r.Start, "evidence.run.start"), OptionalLine(r.Url, "evidence.run.url"),
            OptionalLine(r.Ready, "evidence.run.ready"),
            string.IsNullOrWhiteSpace(r.Notes) ? null : Limited(r.Notes, "evidence.run.notes", EvidenceLimits.MaxNotesChars));
    }

    private static string? OptionalLine(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : SingleLine(value, field, EvidenceLimits.MaxRunFieldChars);

    private static string SingleLine(string value, string field, int max)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new ProjectConfigException($"{field} must be a single line");
        return Limited(value, field, max);
    }

    private static string Limited(string value, string field, int max) =>
        value.Length > max ? throw new ProjectConfigException($"{field} is {value.Length} characters (at most {max})") : value;

    private static List<StageDefinition> MapPipeline(List<StageDto?>? stages, AgentSettings agent)
    {
        if (stages is null || stages.Count == 0)
            throw new ProjectConfigException("pipeline must contain at least one stage");

        var result = new List<StageDefinition>();
        var seen = new HashSet<string>();
        for (var i = 0; i < stages.Count; i++)
        {
            // A YAML null item ("pipeline: [~]" or a bare "-") deserializes as null.
            var s = stages[i] ?? throw new ProjectConfigException($"pipeline[{i}] is empty");
            var stageId = Required(s.Id, "pipeline[].id");
            if (!seen.Add(stageId)) throw new ProjectConfigException($"duplicate stage id '{stageId}'");
            var kind = ParseEnum<StageKind>(Required(s.Kind, $"pipeline stage '{stageId}' kind"), "stage kind");
            result.Add(new StageDefinition(
                stageId,
                kind,
                ParseGate(s.Gate, stageId, kind),
                Items(s.EntryStatuses, $"pipeline stage '{stageId}' entryStatuses"),
                s.OnEnterStatus, s.OnGateStatus, s.ApprovedWhenStatus)
            {
                Agent = s.Agent is null ? null : Override(agent, $"pipeline stage '{stageId}' agent.type",
                    s.Agent.Type, s.Agent.Model, s.Agent.TimeoutMinutes, agent.TimeoutMinutes),
            });
        }

        if (result[^1].Kind != StageKind.Done)
            throw new ProjectConfigException("last stage must be kind 'done'");
        return result;
    }

    private static GatePolicy ParseGate(string? value, string stageId, StageKind kind)
    {
        var gate = string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            ? GatePolicy.AutoWhenLowRisk
            : ParseEnum<GatePolicy>(value ?? "none", "gate");
        if (gate == GatePolicy.AutoWhenLowRisk && !ProjectConfig.IsReviewable(kind))
            throw new ProjectConfigException(
                $"stage '{stageId}': gate 'auto' is only supported for triage, plan, implement and release");
        return gate;
    }

    /// <summary>The list, or empty when absent; a null/blank item (YAML <c>~</c>) is rejected.</summary>
    private static List<string> Items(List<string>? items, string field) =>
        items is null ? []
        : items.Any(string.IsNullOrWhiteSpace) ? throw new ProjectConfigException($"{field} contains an empty item")
        : items;

    private static T ParseEnum<T>(string value, string what) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ProjectConfigException($"unknown {what} '{value}'");

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw Missing(field) : value;

    private static ProjectConfigException Missing(string field) => new($"missing required '{field}'");

    private sealed class Dto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        // Provider sections stay raw: their provider binds them (see ProviderSection).
        public Dictionary<string, object?>? Tracker { get; set; }
        public Dictionary<string, object?>? Repo { get; set; }
        public Dictionary<string, object?>? Build { get; set; }
        public AgentDto? Agent { get; set; }
        public int? MaxAttemptsPerStage { get; set; }
        public string? CommentMarker { get; set; }
        public List<StageDto?>? Pipeline { get; set; }
        public Dictionary<string, string?>? Prompts { get; set; }
        public List<RiskAreaDto?>? RiskAreas { get; set; }
        public ReviewDto? Review { get; set; }
        public EvidenceDto? Evidence { get; set; }
        public PrCommentsDto? PrComments { get; set; }
    }

    private sealed class EvidenceDto
    {
        public string? Mode { get; set; }
        public string? Prompt { get; set; }
        public EvidenceRunDto? Run { get; set; }
        public List<EvidenceFeatureDto?>? Features { get; set; }
        public EvidenceAgentDto? Agent { get; set; }
    }

    private sealed class EvidenceRunDto
    {
        public List<string>? Setup { get; set; }
        public string? Start { get; set; }
        public string? Url { get; set; }
        public string? Ready { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class EvidenceFeatureDto
    {
        public string? Name { get; set; }
        public string? Steps { get; set; }
    }

    /// <summary>Only these keys may differ from the project agent.</summary>
    private sealed class EvidenceAgentDto
    {
        public string? Type { get; set; }
        public int? TimeoutMinutes { get; set; }
        public string? Model { get; set; }
    }

    private sealed class PrCommentsDto
    {
        public bool? FromOthers { get; set; }
    }

    private sealed class RiskAreaDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Paths { get; set; }
    }

    private sealed class ReviewDto
    {
        public string? Mode { get; set; }
        public string? Prompt { get; set; }
        public AgentDto? Agent { get; set; }
    }

    private sealed class AgentDto
    {
        public string? Type { get; set; }
        public string? Isolation { get; set; }
        public List<string>? AllowedDomains { get; set; }
        public List<string>? AllowedTools { get; set; }
        public int? TimeoutMinutes { get; set; }
        public string? Model { get; set; }
        public string? Image { get; set; }
        public PackagesDto? Packages { get; set; }
        public CodexDto? Codex { get; set; }
    }

    private sealed class CodexDto
    {
        public string? Auth { get; set; }
        public string? ApiKeySecret { get; set; }
    }

    /// <summary>A pipeline stage's override: only these keys may differ from the project agent.</summary>
    private sealed class StageAgentDto
    {
        public string? Type { get; set; }
        public string? Model { get; set; }
        public int? TimeoutMinutes { get; set; }
    }

    private sealed class PackagesDto
    {
        public List<string>? Apt { get; set; }
        public List<string>? Pip { get; set; }
        public List<string>? Npm { get; set; }
    }

    private sealed class StageDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Gate { get; set; }
        public List<string>? EntryStatuses { get; set; }
        public string? OnEnterStatus { get; set; }
        public string? OnGateStatus { get; set; }
        public string? ApprovedWhenStatus { get; set; }
        public StageAgentDto? Agent { get; set; }
    }
}
