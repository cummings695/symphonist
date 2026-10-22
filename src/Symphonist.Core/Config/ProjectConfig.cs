namespace Symphonist.Core.Config;

public enum StageKind { Triage, Plan, Implement, Build, Uat, Release, Deploy, Done }
public enum GatePolicy { None, Human }
public enum Isolation { Container, Host }

public sealed record StageDefinition(string Id, StageKind Kind, GatePolicy Gate,
    IReadOnlyList<string> EntryStatuses, string? OnEnterStatus, string? OnGateStatus, string? ApprovedWhenStatus);

public sealed record TrackerConfig(string Type, Uri Site, string Email, string TokenSecret, string Jql);

public sealed record RepoConfig(string Type, string Workspace, string Slug, string LocalPath,
    string MainBranch, string ProductionBranch, string Email, string TokenSecret, string MergeStrategy);

public sealed record BuildConfig(string Type, int TimeoutMinutes);

public sealed record AgentSettings(string Type, Isolation Isolation, IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> AllowedTools, int TimeoutMinutes, string? Model, string? Image);

public sealed record ProjectConfig(string Id, string Name, TrackerConfig Tracker, RepoConfig Repo, BuildConfig Build,
    AgentSettings Agent, int MaxAttemptsPerStage, IReadOnlyList<StageDefinition> Pipeline,
    IReadOnlyDictionary<StageKind, string> PromptOverrides, string ConfigDirectory)
{
    public StageDefinition Stage(string id) =>
        Pipeline.FirstOrDefault(s => s.Id == id)
        ?? throw new KeyNotFoundException($"Stage '{id}' not found in project '{Id}'");

    public StageDefinition? NextStage(string id)
    {
        var index = Pipeline.ToList().FindIndex(s => s.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Stage '{id}' not found in project '{Id}'");
        return index + 1 < Pipeline.Count ? Pipeline[index + 1] : null;
    }
}
