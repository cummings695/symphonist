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
        IReadOnlyList<string> allowedTools = agent.AllowedTools ?? [];
        if (isolation == Isolation.Host && allowedTools.Count == 0)
            throw new ProjectConfigException("host isolation requires agent.allowedTools");

        var pipeline = MapPipeline(d.Pipeline);

        var prompts = new Dictionary<StageKind, string>();
        foreach (var (kind, path) in d.Prompts ?? [])
            prompts[ParseEnum<StageKind>(kind, "prompt stage kind")] = Path.GetFullPath(path, dir);

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
            new AgentSettings(agent.Type!, isolation, agent.AllowedDomains ?? [], allowedTools,
                agent.TimeoutMinutes ?? 60, agent.Model, agent.Image),
            d.MaxAttemptsPerStage ?? 3,
            pipeline,
            prompts,
            dir);
    }

    private static List<StageDefinition> MapPipeline(List<StageDto>? stages)
    {
        if (stages is null || stages.Count == 0)
            throw new ProjectConfigException("pipeline must contain at least one stage");

        var result = new List<StageDefinition>();
        var seen = new HashSet<string>();
        foreach (var s in stages)
        {
            var stageId = Required(s.Id, "pipeline[].id");
            if (!seen.Add(stageId)) throw new ProjectConfigException($"duplicate stage id '{stageId}'");
            result.Add(new StageDefinition(
                stageId,
                ParseEnum<StageKind>(Required(s.Kind, $"pipeline stage '{stageId}' kind"), "stage kind"),
                ParseEnum<GatePolicy>(s.Gate ?? "none", "gate"),
                s.EntryStatuses ?? [],
                s.OnEnterStatus, s.OnGateStatus, s.ApprovedWhenStatus));
        }

        if (result[^1].Kind != StageKind.Done)
            throw new ProjectConfigException("last stage must be kind 'done'");
        return result;
    }

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
        public List<StageDto>? Pipeline { get; set; }
        public Dictionary<string, string>? Prompts { get; set; }
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
