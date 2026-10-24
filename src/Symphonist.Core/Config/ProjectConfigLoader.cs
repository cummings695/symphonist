using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Symphonist.Core.Config;

public sealed class ProjectConfigException(string message) : Exception(message);

public static class ProjectConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ProjectConfig Load(string yamlPath)
    {
        var fullPath = Path.GetFullPath(yamlPath);
        Dto dto;
        try
        {
            dto = Deserializer.Deserialize<Dto?>(File.ReadAllText(fullPath))
                  ?? throw new ProjectConfigException($"{fullPath}: file is empty");
        }
        catch (YamlException ex)
        {
            throw new ProjectConfigException($"{fullPath}: {ex.Message}");
        }

        try
        {
            return Map(dto, Path.GetDirectoryName(fullPath)!);
        }
        catch (ProjectConfigException ex)
        {
            throw new ProjectConfigException($"{fullPath}: {ex.Message}");
        }
    }

    public static IReadOnlyList<ProjectConfig> LoadAll(string projectsDirectory) =>
        ProjectFiles(projectsDirectory).Select(Load).ToList();

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

    private static ProjectConfig Map(Dto d, string dir)
    {
        var id = Required(d.Id, "id");
        var tracker = d.Tracker ?? throw Missing("tracker");
        var repo = d.Repo ?? throw Missing("repo");
        var build = d.Build ?? throw Missing("build");
        var agent = d.Agent ?? throw Missing("agent");

        Supported("tracker.type", Required(tracker.Type, "tracker.type"), "jira");
        Supported("repo.type", Required(repo.Type, "repo.type"), "bitbucket");
        Supported("agent.type", Required(agent.Type, "agent.type"), "claude-code");

        var isolation = ParseEnum<Isolation>(agent.Isolation ?? "container", "isolation");
        IReadOnlyList<string> allowedTools = Items(agent.AllowedTools, "agent.allowedTools");
        if (isolation == Isolation.Host && allowedTools.Count == 0)
            throw new ProjectConfigException("host isolation requires agent.allowedTools");

        var pipeline = MapPipeline(d.Pipeline);

        var prompts = new Dictionary<StageKind, string>();
        foreach (var (kind, path) in d.Prompts ?? [])
            prompts[ParseEnum<StageKind>(kind, "prompt stage kind")] = Path.GetFullPath(Required(path, $"prompts.{kind}"), dir);

        return new ProjectConfig(
            id,
            d.Name ?? id,
            new TrackerConfig(tracker.Type!, new Uri(Required(tracker.Site, "tracker.site")),
                Required(tracker.Email, "tracker.email"), Required(tracker.TokenSecret, "tracker.tokenSecret"),
                Required(tracker.Jql, "tracker.jql")),
            new RepoConfig(repo.Type!, Required(repo.Workspace, "repo.workspace"), Required(repo.Slug, "repo.slug"),
                Path.GetFullPath(Required(repo.LocalPath, "repo.localPath"), dir),
                repo.MainBranch ?? "main", repo.ProductionBranch ?? "production",
                Required(repo.Email, "repo.email"), Required(repo.TokenSecret, "repo.tokenSecret"),
                repo.MergeStrategy ?? "merge_commit"),
            new BuildConfig(Required(build.Type, "build.type"), build.TimeoutMinutes ?? 45),
            new AgentSettings(agent.Type!, isolation, Items(agent.AllowedDomains, "agent.allowedDomains"), allowedTools,
                agent.TimeoutMinutes ?? 60, agent.Model, agent.Image),
            d.MaxAttemptsPerStage ?? 3,
            pipeline,
            prompts,
            dir)
        {
            RiskAreas = MapRiskAreas(d.RiskAreas),
            Review = MapReview(d.Review, agent, isolation, dir),
        };
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

    private static ReviewConfig MapReview(ReviewDto? r, AgentDto agent, Isolation isolation, string dir)
    {
        var mode = ParseEnum<ReviewMode>(r?.Mode ?? "off", "review.mode");
        var ra = r?.Agent;
        Supported("review.agent.type", ra?.Type ?? agent.Type!, "claude-code");
        var tools = ra?.AllowedTools is null ? ReviewConfig.ReadOnlyTools : Items(ra.AllowedTools, "review.agent.allowedTools");
        if (tools.Count == 0) tools = ReviewConfig.ReadOnlyTools;
        var settings = new AgentSettings(ra?.Type ?? agent.Type!, isolation, Items(agent.AllowedDomains, "agent.allowedDomains"),
            tools, ra?.TimeoutMinutes ?? 20, ra?.Model ?? agent.Model, agent.Image);
        var prompt = r?.Prompt is null ? null : Path.GetFullPath(Required(r.Prompt, "review.prompt"), dir);
        return new ReviewConfig(mode, settings, prompt);
    }

    private static List<StageDefinition> MapPipeline(List<StageDto?>? stages)
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
                s.OnEnterStatus, s.OnGateStatus, s.ApprovedWhenStatus));
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

    private static void Supported(string field, string value, string expected)
    {
        if (value != expected) throw new ProjectConfigException($"unsupported {field} '{value}'");
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw Missing(field) : value;

    private static ProjectConfigException Missing(string field) => new($"missing required '{field}'");

    private sealed class Dto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public TrackerDto? Tracker { get; set; }
        public RepoDto? Repo { get; set; }
        public BuildDto? Build { get; set; }
        public AgentDto? Agent { get; set; }
        public int? MaxAttemptsPerStage { get; set; }
        public List<StageDto?>? Pipeline { get; set; }
        public Dictionary<string, string?>? Prompts { get; set; }
        public List<RiskAreaDto?>? RiskAreas { get; set; }
        public ReviewDto? Review { get; set; }
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

    private sealed class TrackerDto
    {
        public string? Type { get; set; }
        public string? Site { get; set; }
        public string? Email { get; set; }
        public string? TokenSecret { get; set; }
        public string? Jql { get; set; }
    }

    private sealed class RepoDto
    {
        public string? Type { get; set; }
        public string? Workspace { get; set; }
        public string? Slug { get; set; }
        public string? LocalPath { get; set; }
        public string? MainBranch { get; set; }
        public string? ProductionBranch { get; set; }
        public string? Email { get; set; }
        public string? TokenSecret { get; set; }
        public string? MergeStrategy { get; set; }
    }

    private sealed class BuildDto
    {
        public string? Type { get; set; }
        public int? TimeoutMinutes { get; set; }
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
    }
}
