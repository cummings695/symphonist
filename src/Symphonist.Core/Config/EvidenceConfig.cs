namespace Symphonist.Core.Config;

/// <summary>When the evidence agent runs for a gated implement round. YAML: off | high-risk | always.</summary>
public enum EvidenceMode { Off, HighRisk, Always }

/// <summary>How to start the app so the evidence agent can use it. All fields are optional.</summary>
public sealed record EvidenceRun(IReadOnlyList<string> Setup, string? Start, string? Url, string? Ready, string? Notes)
{
    public static readonly EvidenceRun None = new([], null, null, null, null);

    /// <summary>No setup commands and every field null or blank.</summary>
    public bool IsEmpty => Setup.Count == 0 && string.IsNullOrWhiteSpace(Start) && string.IsNullOrWhiteSpace(Url)
        && string.IsNullOrWhiteSpace(Ready) && string.IsNullOrWhiteSpace(Notes);
}

/// <summary>A feature the developer wants exercised, in their own words.</summary>
public sealed record EvidenceFeature(string Name, string Steps);

/// <param name="PromptPath">Absolute path of an evidence prompt override, or null for the embedded default.</param>
public sealed record EvidenceConfig(EvidenceMode Mode, EvidenceRun Run, IReadOnlyList<EvidenceFeature> Features,
    AgentSettings Agent, string? PromptPath);

/// <param name="FromOthers">Whether PR comments by people other than the token's user can trigger a fix run.</param>
public sealed record PrCommentsConfig(bool FromOthers)
{
    public static readonly PrCommentsConfig Default = new(false);
}

public static class EvidenceLimits
{
    public const int MaxFeatures = 20, MaxFeatureNameChars = 80, MaxFeatureStepsChars = 2000,
        MaxRunFieldChars = 500, MaxNotesChars = 2000, MaxSetupCommands = 10, DefaultTimeoutMinutes = 30;
}
