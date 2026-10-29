using Symphonist.Core.Integrations;

namespace Symphonist.Core.Config;

public enum StageKind { Triage, Plan, Implement, Build, Uat, Release, Deploy, Done }
public enum GatePolicy { None, Human, AutoWhenLowRisk }
public enum ReviewMode { Off, Shadow, Active }
public enum Isolation { Container, Host }

public sealed record StageDefinition(string Id, StageKind Kind, GatePolicy Gate,
    IReadOnlyList<string> EntryStatuses, string? OnEnterStatus, string? OnGateStatus, string? ApprovedWhenStatus)
{
    /// <summary>This stage's own agent (the project agent with its override applied), or null for the project default.</summary>
    public AgentSettings? Agent { get; init; }
}

/// <summary>The settings every tracker provider shares; each provider subclasses it with its own fields.</summary>
public abstract record TrackerConfig(string Type, string TokenSecret);

/// <summary>The settings every repository provider shares; each provider subclasses it with its own fields.</summary>
/// <param name="LocalPath">Absolute path of the local clone.</param>
public abstract record RepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch,
    string TokenSecret)
{
    /// <summary>"owner/name", as the host names the repository (compared ignoring case).</summary>
    public abstract string FullName { get; }
}

/// <summary>The settings every build provider shares; each provider subclasses it with its own fields.</summary>
public abstract record BuildConfig(string Type, int TimeoutMinutes);

public sealed record AgentSettings(string Type, Isolation Isolation, IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> AllowedTools, int TimeoutMinutes, string? Model, string? Image)
{
    /// <summary>Extra packages baked into the agent image (container isolation only).</summary>
    public AgentPackages Packages { get; init; } = AgentPackages.None;

    /// <summary>The project's Codex sign-in; read only by Codex runs.</summary>
    public CodexSettings Codex { get; init; } = CodexSettings.Default;
}

/// <summary>
/// Packages to install on top of the agent image, by installer. Installed when the image is built, where the network
/// is open; the agent itself still runs behind the firewall. Each item is one installer argument, such as
/// <c>ripgrep</c>, <c>graphifyy==0.8.33</c> or <c>@scope/tool@1.2.0</c>.
/// </summary>
public sealed record AgentPackages(IReadOnlyList<string> Apt, IReadOnlyList<string> Pip, IReadOnlyList<string> Npm)
{
    public static readonly AgentPackages None = new([], [], []);

    public bool IsEmpty => Apt.Count == 0 && Pip.Count == 0 && Npm.Count == 0;
}

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
    private readonly EvidenceConfig? _evidence;

    public IReadOnlyList<RiskArea> RiskAreas { get; init; } = [];

    /// <summary>Defaults to <see cref="ReviewMode.Off"/> with the project's own <see cref="Agent"/>.</summary>
    public ReviewConfig Review
    {
        get => _review ?? new ReviewConfig(ReviewMode.Off, Agent, null);
        init => _review = value;
    }

    /// <summary>Defaults to <see cref="EvidenceMode.HighRisk"/> with the project's own <see cref="Agent"/> and a 30 minute timeout.</summary>
    public EvidenceConfig Evidence
    {
        get => _evidence ?? new EvidenceConfig(EvidenceMode.HighRisk, EvidenceRun.None, [],
            Agent with { TimeoutMinutes = EvidenceLimits.DefaultTimeoutMinutes }, null);
        init => _evidence = value;
    }

    public PrCommentsConfig PrComments { get; init; } = PrCommentsConfig.Default;

    /// <summary>
    /// What a tracker or pull request comment starts with to ask Symphonist to rerun a stage: "@" and a name, default
    /// "@claude". Matched without regard to case.
    /// </summary>
    public string CommentMarker { get; init; } = TrackerComments.DefaultMarker;

    /// <summary>Only these stage kinds have agent-readable output for a reviewer to judge.</summary>
    public static bool IsReviewable(StageKind kind) =>
        kind is StageKind.Triage or StageKind.Plan or StageKind.Implement or StageKind.Release;

    public bool Reviews(StageDefinition stage) =>
        Review.Mode != ReviewMode.Off && GateOf(stage) != GatePolicy.None && IsReviewable(stage.Kind);

    /// <summary>Evidence applies to this stage's gate rounds: mode not off, implement kind, a gate.</summary>
    public bool WantsEvidence(StageDefinition stage) =>
        Evidence.Mode != EvidenceMode.Off && GateOf(stage) != GatePolicy.None && stage.Kind == StageKind.Implement;

    /// <summary>
    /// The gate a stage's rounds actually have: its configured gate, except that a release always needs one more
    /// approval after Release now, so a release stage with no gate waits on the developer. Every read that decides
    /// behaviour goes through this; only config editing reads the raw <see cref="StageDefinition.Gate"/>.
    /// </summary>
    public GatePolicy GateOf(StageDefinition stage) => EffectiveGate(stage.Kind, stage.Gate);

    /// <inheritdoc cref="GateOf"/>
    public static GatePolicy EffectiveGate(StageKind kind, GatePolicy gate) =>
        kind == StageKind.Release && gate == GatePolicy.None ? GatePolicy.Human : gate;

    /// <summary>The first release-kind stage, where tickets wait to ship in a release batch; null when there is none.</summary>
    public StageDefinition? ReleaseStage => Pipeline.FirstOrDefault(s => s.Kind == StageKind.Release);

    /// <summary>The agent a stage's runs use: its override, else the project <see cref="Agent"/> (also for an unknown id).</summary>
    public AgentSettings AgentFor(string stageId) =>
        Pipeline.FirstOrDefault(s => s.Id == stageId)?.Agent ?? Agent;

    /// <summary>Every provider some job may run: the project agent, stage overrides, reviewer and evidence agent, whatever their modes.</summary>
    public IReadOnlySet<string> AgentTypesInUse
    {
        get
        {
            var types = new HashSet<string> { Agent.Type, Review.Agent.Type, Evidence.Agent.Type };
            foreach (var stage in Pipeline)
                if (stage.Agent is not null) types.Add(stage.Agent.Type);
            return types;
        }
    }

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
