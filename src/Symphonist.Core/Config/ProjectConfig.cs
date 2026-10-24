namespace Symphonist.Core.Config;

public enum StageKind { Triage, Plan, Implement, Build, Uat, Release, Deploy, Done }
public enum GatePolicy { None, Human, AutoWhenLowRisk }
public enum ReviewMode { Off, Shadow, Active }
public enum Isolation { Container, Host }

public sealed record StageDefinition(string Id, StageKind Kind, GatePolicy Gate,
    IReadOnlyList<string> EntryStatuses, string? OnEnterStatus, string? OnGateStatus, string? ApprovedWhenStatus);

public sealed record TrackerConfig(string Type, Uri Site, string Email, string TokenSecret, string Jql);

public sealed record RepoConfig(string Type, string Workspace, string Slug, string LocalPath,
    string MainBranch, string ProductionBranch, string Email, string TokenSecret, string MergeStrategy);

public sealed record BuildConfig(string Type, int TimeoutMinutes);

public sealed record AgentSettings(string Type, Isolation Isolation, IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> AllowedTools, int TimeoutMinutes, string? Model, string? Image);

public sealed record RiskArea(string Name, string Description, IReadOnlyList<string> Paths);

/// <param name="PromptPath">Absolute path of a review prompt override, or null for the embedded default.</param>
public sealed record ReviewConfig(ReviewMode Mode, AgentSettings Agent, string? PromptPath)
{
    /// <summary>
    /// The reviewer's host-mode tools, and the only ones a read-only run accepts. No Bash: even git diff/log/show can
    /// write files (<c>--output</c>), and the reviewer reads untrusted PR content.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyTools = ["Read", "Grep", "Glob"];
}

public sealed record ProjectConfig(string Id, string Name, TrackerConfig Tracker, RepoConfig Repo, BuildConfig Build,
    AgentSettings Agent, int MaxAttemptsPerStage, IReadOnlyList<StageDefinition> Pipeline,
    IReadOnlyDictionary<StageKind, string> PromptOverrides, string ConfigDirectory)
{
    private readonly ReviewConfig? _review;

    public IReadOnlyList<RiskArea> RiskAreas { get; init; } = [];

    /// <summary>Defaults to <see cref="ReviewMode.Off"/> with the project's own <see cref="Agent"/>.</summary>
    public ReviewConfig Review
    {
        get => _review ?? new ReviewConfig(ReviewMode.Off, Agent, null);
        init => _review = value;
    }

    /// <summary>Only these stage kinds have agent-readable output for a reviewer to judge.</summary>
    public static bool IsReviewable(StageKind kind) =>
        kind is StageKind.Triage or StageKind.Plan or StageKind.Implement or StageKind.Release;

    public bool Reviews(StageDefinition stage) =>
        Review.Mode != ReviewMode.Off && stage.Gate != GatePolicy.None && IsReviewable(stage.Kind);

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
