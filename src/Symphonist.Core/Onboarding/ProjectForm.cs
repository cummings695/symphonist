using System.Globalization;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Core.Onboarding;

/// <summary>One tracker/repo/build section: Values holds scalar fields (incl. Number/Path/Secret/Choice as text), Lists the List fields.</summary>
public sealed class ProviderSectionForm
{
    public string Type { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
    public Dictionary<string, List<string>> Lists { get; set; } = [];

    public ProviderSectionForm Clone() => new()
    {
        Type = Type,
        Values = new Dictionary<string, string>(Values),
        Lists = Lists.ToDictionary(e => e.Key, e => e.Value.ToList()),
    };
}

public sealed class AgentForm
{
    public Isolation Isolation { get; set; } = Isolation.Container;
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> AllowedTools { get; set; } = [];
    public int TimeoutMinutes { get; set; } = 60;
    public string? Model { get; set; }
    public string? Image { get; set; }
    public List<string> PackagesApt { get; set; } = [];
    public List<string> PackagesPip { get; set; } = [];
    public List<string> PackagesNpm { get; set; } = [];

    public AgentForm Clone() => new()
    {
        Isolation = Isolation, AllowedDomains = [.. AllowedDomains], AllowedTools = [.. AllowedTools],
        TimeoutMinutes = TimeoutMinutes, Model = Model, Image = Image,
        PackagesApt = [.. PackagesApt], PackagesPip = [.. PackagesPip], PackagesNpm = [.. PackagesNpm],
    };
}

public sealed class StageForm
{
    public string Id { get; set; } = "";
    public StageKind Kind { get; set; }
    public GatePolicy Gate { get; set; }
    public List<string> EntryStatuses { get; set; } = [];
    public string? OnEnterStatus { get; set; }
    public string? OnGateStatus { get; set; }
    public string? ApprovedWhenStatus { get; set; }

    public StageForm Clone() => new()
    {
        Id = Id, Kind = Kind, Gate = Gate, EntryStatuses = [.. EntryStatuses],
        OnEnterStatus = OnEnterStatus, OnGateStatus = OnGateStatus, ApprovedWhenStatus = ApprovedWhenStatus,
    };
}

public sealed class RiskAreaForm
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Paths { get; set; } = [];

    public RiskAreaForm Clone() => new() { Name = Name, Description = Description, Paths = [.. Paths] };
}

/// <summary>The <c>review</c> section; empty values mean "inherit from agent" or "use the default".</summary>
public sealed class ReviewForm
{
    public ReviewMode Mode { get; set; } = ReviewMode.Off;
    public string? PromptPath { get; set; }
    public string? Model { get; set; }
    public int TimeoutMinutes { get; set; } = 20;
    public List<string> AllowedTools { get; set; } = [];

    public ReviewForm Clone() => new()
    {
        Mode = Mode, PromptPath = PromptPath, Model = Model, TimeoutMinutes = TimeoutMinutes, AllowedTools = [.. AllowedTools],
    };
}

public sealed class EvidenceFeatureForm
{
    public string Name { get; set; } = "";
    public string Steps { get; set; } = "";

    public EvidenceFeatureForm Clone() => new() { Name = Name, Steps = Steps };
}

/// <summary>The <c>evidence</c> section; empty values mean "not set" or "use the default".</summary>
public sealed class EvidenceForm
{
    public EvidenceMode Mode { get; set; } = EvidenceMode.HighRisk;
    public List<string> Setup { get; set; } = [];
    public string? Start { get; set; }
    public string? Url { get; set; }
    public string? Ready { get; set; }
    public string? Notes { get; set; }
    public List<EvidenceFeatureForm> Features { get; set; } = [];
    public int TimeoutMinutes { get; set; } = EvidenceLimits.DefaultTimeoutMinutes;
    public string? Model { get; set; }
    public string? PromptPath { get; set; }

    public EvidenceForm Clone() => new()
    {
        Mode = Mode, Setup = [.. Setup], Start = Start, Url = Url, Ready = Ready, Notes = Notes,
        Features = Features.Select(f => f.Clone()).ToList(), TimeoutMinutes = TimeoutMinutes, Model = Model,
        PromptPath = PromptPath,
    };

    /// <summary>Fills only blank run fields and adds features whose name is new (ignoring case), up to the loader's limit.</summary>
    public void Merge(EvidenceRun run, IReadOnlyList<EvidenceFeature> features)
    {
        if (Setup.Count == 0) Setup = [.. run.Setup];
        if (string.IsNullOrWhiteSpace(Start)) Start = run.Start;
        if (string.IsNullOrWhiteSpace(Url)) Url = run.Url;
        if (string.IsNullOrWhiteSpace(Ready)) Ready = run.Ready;
        if (string.IsNullOrWhiteSpace(Notes)) Notes = run.Notes;
        foreach (var feature in features)
        {
            if (Features.Count >= EvidenceLimits.MaxFeatures) break;
            if (Features.Any(f => string.Equals(f.Name, feature.Name, StringComparison.OrdinalIgnoreCase))) continue;
            Features.Add(new EvidenceFeatureForm { Name = feature.Name, Steps = feature.Steps });
        }
    }
}

/// <summary>Mutable mirror of every field <see cref="ProjectConfigLoader"/> reads.</summary>
public sealed class ProjectForm
{
    /// <summary>The label a new project picks tickets up by, as in the samples.</summary>
    public const string DefaultLabel = "Claude";

    // Keys every provider of a slot shares (ProviderSection subclasses); they survive a provider type change.
    private static readonly string[] TrackerShared = ["tokenSecret"];
    private static readonly string[] RepoShared = ["tokenSecret", "localPath", "mainBranch", "productionBranch"];
    private static readonly string[] BuildShared = ["timeoutMinutes"];

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ProviderSectionForm Tracker { get; set; } = new();
    public ProviderSectionForm Repo { get; set; } = new();
    public ProviderSectionForm Build { get; set; } = new();
    public AgentForm Agent { get; set; } = new();
    public int MaxAttemptsPerStage { get; set; } = 3;

    /// <summary>The comment marker: "@" and a name. Default "@claude".</summary>
    public string CommentMarker { get; set; } = TrackerComments.DefaultMarker;
    public List<StageForm> Pipeline { get; set; } = [];
    public List<RiskAreaForm> RiskAreas { get; set; } = [];
    public ReviewForm Review { get; set; } = new();
    public EvidenceForm Evidence { get; set; } = new();
    public bool PrCommentsFromOthers { get; set; }

    /// <summary>Paths as written (relative to the config dir when under it).</summary>
    public Dictionary<StageKind, string> Prompts { get; set; } = [];

    /// <summary>
    /// Sample defaults: the sample's seven stages and gates with no statuses, provider fields at their defaults,
    /// default secret names, the first build provider for the repo, container isolation and review off.
    /// </summary>
    public static ProjectForm NewProject(string id, ProviderRegistry providers, string trackerType = "jira",
        string repoType = "bitbucket")
    {
        var tracker = providers.Tracker(trackerType);
        var repo = providers.Repo(repoType);
        var build = providers.BuildsFor(repoType).FirstOrDefault()
            ?? throw new ArgumentException($"no build provider reads repo.type '{repoType}'", nameof(repoType));
        var form = new ProjectForm
        {
            Id = id,
            Name = id,
            Tracker = new ProviderSectionForm { Type = tracker.Type },
            Repo = new ProviderSectionForm { Type = repo.Type },
            Build = new ProviderSectionForm { Type = build.Type },
            Agent = new AgentForm { AllowedDomains = ["api.nuget.org", "registry.npmjs.org"] },
            Pipeline =
            [
                new() { Id = "triage", Kind = StageKind.Triage, Gate = GatePolicy.Human },
                new() { Id = "plan", Kind = StageKind.Plan, Gate = GatePolicy.Human },
                new() { Id = "implement", Kind = StageKind.Implement, Gate = GatePolicy.Human },
                new() { Id = "build", Kind = StageKind.Build },
                new() { Id = "release", Kind = StageKind.Release, Gate = GatePolicy.Human },
                new() { Id = "deploy", Kind = StageKind.Deploy },
                new() { Id = "done", Kind = StageKind.Done },
            ],
        };
        form.Tracker.Values["tokenSecret"] = SecretNames.Default(tracker.HttpClientName, id);
        form.Repo.Values["tokenSecret"] = SecretNames.Default(repo.HttpClientName, id);
        FillDefaults(form.Tracker, tracker.Fields);
        FillDefaults(form.Repo, repo.Fields);
        FillDefaults(form.Build, build.Fields);
        return form;
    }

    /// <summary>Provider sections via <c>ToValues</c>; paths under the config directory become relative ('/'-separated).</summary>
    public static ProjectForm From(ProjectConfig config, ProviderRegistry providers)
    {
        var dir = config.ConfigDirectory;
        var review = config.Review;
        return new ProjectForm
        {
            Id = config.Id,
            Name = config.Name,
            Tracker = Section(config.Tracker.Type, providers.Tracker(config.Tracker.Type).ToValues(config.Tracker),
                providers.Tracker(config.Tracker.Type).Fields, dir),
            Repo = Section(config.Repo.Type, providers.Repo(config.Repo.Type).ToValues(config.Repo),
                providers.Repo(config.Repo.Type).Fields, dir),
            Build = Section(config.Build.Type, providers.Build(config.Build.Type).ToValues(config.Build),
                providers.Build(config.Build.Type).Fields, dir),
            Agent = new AgentForm
            {
                Isolation = config.Agent.Isolation,
                AllowedDomains = [.. config.Agent.AllowedDomains],
                AllowedTools = [.. config.Agent.AllowedTools],
                TimeoutMinutes = config.Agent.TimeoutMinutes,
                Model = Blank(config.Agent.Model),
                Image = Blank(config.Agent.Image),
                PackagesApt = [.. config.Agent.Packages.Apt],
                PackagesPip = [.. config.Agent.Packages.Pip],
                PackagesNpm = [.. config.Agent.Packages.Npm],
            },
            MaxAttemptsPerStage = config.MaxAttemptsPerStage,
            CommentMarker = config.CommentMarker,
            Pipeline = config.Pipeline.Select(s => new StageForm
            {
                Id = s.Id, Kind = s.Kind, Gate = s.Gate, EntryStatuses = [.. s.EntryStatuses],
                OnEnterStatus = s.OnEnterStatus, OnGateStatus = s.OnGateStatus, ApprovedWhenStatus = s.ApprovedWhenStatus,
            }).ToList(),
            RiskAreas = config.RiskAreas.Select(a => new RiskAreaForm
            {
                Name = a.Name, Description = a.Description, Paths = [.. a.Paths],
            }).ToList(),
            Review = new ReviewForm
            {
                Mode = review.Mode,
                PromptPath = review.PromptPath is null ? null : Relative(review.PromptPath, dir),
                // The loader fills inherited and default values in; keep only what the file overrides.
                Model = review.Agent.Model == config.Agent.Model ? null : Blank(review.Agent.Model),
                TimeoutMinutes = review.Agent.TimeoutMinutes,
                AllowedTools = review.Agent.AllowedTools.SequenceEqual(ReviewConfig.ReadOnlyTools)
                    ? [] : [.. review.Agent.AllowedTools],
            },
            Evidence = new EvidenceForm
            {
                Mode = config.Evidence.Mode,
                Setup = [.. config.Evidence.Run.Setup],
                Start = config.Evidence.Run.Start,
                Url = config.Evidence.Run.Url,
                Ready = config.Evidence.Run.Ready,
                Notes = config.Evidence.Run.Notes,
                Features = config.Evidence.Features.Select(f => new EvidenceFeatureForm { Name = f.Name, Steps = f.Steps }).ToList(),
                TimeoutMinutes = config.Evidence.Agent.TimeoutMinutes,
                // The loader fills the inherited model in; keep only what the file overrides.
                Model = config.Evidence.Agent.Model == config.Agent.Model ? null : Blank(config.Evidence.Agent.Model),
                PromptPath = config.Evidence.PromptPath is null ? null : Relative(config.Evidence.PromptPath, dir),
            },
            PrCommentsFromOthers = config.PrComments.FromOthers,
            Prompts = config.PromptOverrides.ToDictionary(e => e.Key, e => Relative(e.Value, dir)),
        };
    }

    public void ChangeTrackerType(string type, ProviderRegistry providers)
    {
        var next = providers.Tracker(type);
        var previous = providers.AllTrackers.FirstOrDefault(p => p.Type == Tracker.Type);
        Tracker = ChangeType(Tracker, type, next.Fields, TrackerShared, previous?.HttpClientName, next.HttpClientName);
    }

    /// <summary>Also re-picks the build provider when the current one does not read the new repo type.</summary>
    public void ChangeRepoType(string type, ProviderRegistry providers)
    {
        var next = providers.Repo(type);
        var previous = providers.AllRepos.FirstOrDefault(p => p.Type == Repo.Type);
        Repo = ChangeType(Repo, type, next.Fields, RepoShared, previous?.HttpClientName, next.HttpClientName);
        var builds = providers.BuildsFor(type);
        if (builds.Count > 0 && builds.All(b => b.Type != Build.Type)) ChangeBuildType(builds[0].Type, providers);
    }

    /// <exception cref="ArgumentException">The build provider does not read the current repo type.</exception>
    public void ChangeBuildType(string type, ProviderRegistry providers)
    {
        var next = providers.BuildsFor(Repo.Type).FirstOrDefault(p => p.Type == type)
            ?? throw new ArgumentException($"build.type '{type}' does not work with repo.type '{Repo.Type}'", nameof(type));
        Build = ChangeType(Build, type, next.Fields, BuildShared, null, null);
    }

    public ProjectForm Clone() => new()
    {
        Id = Id,
        Name = Name,
        Tracker = Tracker.Clone(),
        Repo = Repo.Clone(),
        Build = Build.Clone(),
        Agent = Agent.Clone(),
        MaxAttemptsPerStage = MaxAttemptsPerStage,
        CommentMarker = CommentMarker,
        Pipeline = Pipeline.Select(s => s.Clone()).ToList(),
        RiskAreas = RiskAreas.Select(a => a.Clone()).ToList(),
        Review = Review.Clone(),
        Evidence = Evidence.Clone(),
        PrCommentsFromOthers = PrCommentsFromOthers,
        Prompts = new Dictionary<StageKind, string>(Prompts),
    };

    /// <summary>
    /// Errors the loader cannot see: line breaks or control characters in any value (descriptions and
    /// <see cref="ProviderFieldKind.MultilineText"/> fields may hold "\n" line breaks), invalid secret names, Number
    /// fields that are not integers, duplicate stage ids and blank list items.
    /// </summary>
    public IReadOnlyList<string> Validate(ProviderRegistry providers)
    {
        var errors = new List<string>();
        Line("id", Id, errors);
        Line("name", Name, errors);
        if (!ProjectConfigLoader.IsValidCommentMarker(CommentMarker)) errors.Add(ProjectConfigLoader.CommentMarkerRule(CommentMarker));
        ValidateSection("tracker", Tracker, providers.AllTrackers.FirstOrDefault(p => p.Type == Tracker.Type)?.Fields, errors);
        ValidateSection("repo", Repo, providers.AllRepos.FirstOrDefault(p => p.Type == Repo.Type)?.Fields, errors);
        ValidateSection("build", Build, providers.AllBuilds.FirstOrDefault(p => p.Type == Build.Type)?.Fields, errors);

        Items("agent.allowedDomains", Agent.AllowedDomains, errors);
        Items("agent.allowedTools", Agent.AllowedTools, errors);
        Line("agent.model", Agent.Model, errors);
        Line("agent.image", Agent.Image, errors);
        Items("agent.packages.apt", Agent.PackagesApt, errors);
        Items("agent.packages.pip", Agent.PackagesPip, errors);
        Items("agent.packages.npm", Agent.PackagesNpm, errors);

        var seen = new HashSet<string>();
        foreach (var stage in Pipeline)
        {
            Line("pipeline[].id", stage.Id, errors);
            if (!seen.Add(stage.Id)) errors.Add($"duplicate stage id '{stage.Id}'");
            Items($"pipeline stage '{stage.Id}' entryStatuses", stage.EntryStatuses, errors);
            Line($"pipeline stage '{stage.Id}' onEnterStatus", stage.OnEnterStatus, errors);
            Line($"pipeline stage '{stage.Id}' onGateStatus", stage.OnGateStatus, errors);
            Line($"pipeline stage '{stage.Id}' approvedWhenStatus", stage.ApprovedWhenStatus, errors);
        }

        foreach (var area in RiskAreas)
        {
            Line("riskAreas[].name", area.Name, errors);
            Multiline($"riskAreas '{area.Name}' description", area.Description, errors);
            Items($"riskAreas '{area.Name}' paths", area.Paths, errors);
        }

        Line("review.prompt", Review.PromptPath, errors);
        Line("review.agent.model", Review.Model, errors);
        Items("review.agent.allowedTools", Review.AllowedTools, errors);
        ValidateEvidence(errors);
        foreach (var (kind, path) in Prompts) Line($"prompts.{kind.ToString().ToLowerInvariant()}", path, errors);
        return errors;
    }

    /// <summary>Rules matching the loader's evidence limits, so the screen cannot save what the loader rejects.</summary>
    private void ValidateEvidence(List<string> errors)
    {
        var e = Evidence;
        Items("evidence.run.setup", e.Setup, errors);
        if (e.Setup.Count > EvidenceLimits.MaxSetupCommands)
            errors.Add($"evidence.run.setup has {e.Setup.Count} commands (at most {EvidenceLimits.MaxSetupCommands})");
        foreach (var command in e.Setup) Max("evidence.run.setup", command, EvidenceLimits.MaxRunFieldChars, errors);
        foreach (var (field, value) in new[] { ("start", e.Start), ("url", e.Url), ("ready", e.Ready) })
        {
            Line($"evidence.run.{field}", value, errors);
            Max($"evidence.run.{field}", value, EvidenceLimits.MaxRunFieldChars, errors);
        }
        Multiline("evidence.run.notes", e.Notes, errors);
        Max("evidence.run.notes", e.Notes, EvidenceLimits.MaxNotesChars, errors);
        if (e.Features.Count > EvidenceLimits.MaxFeatures)
            errors.Add($"evidence.features has {e.Features.Count} items (at most {EvidenceLimits.MaxFeatures})");
        foreach (var feature in e.Features)
        {
            if (string.IsNullOrWhiteSpace(feature.Name)) errors.Add("evidence.features[].name is required");
            Line("evidence.features[].name", feature.Name, errors);
            Max("evidence.features[].name", feature.Name, EvidenceLimits.MaxFeatureNameChars, errors);
            if (string.IsNullOrWhiteSpace(feature.Steps)) errors.Add($"evidence feature '{feature.Name}' needs steps");
            Multiline($"evidence feature '{feature.Name}' steps", feature.Steps, errors);
            Max($"evidence feature '{feature.Name}' steps", feature.Steps, EvidenceLimits.MaxFeatureStepsChars, errors);
        }
        Line("evidence.prompt", e.PromptPath, errors);
        Line("evidence.agent.model", e.Model, errors);
    }

    private static void Max(string field, string? value, int max, List<string> errors)
    {
        if (value is not null && value.Length > max) errors.Add($"{field} is {value.Length} characters (at most {max})");
    }

    private static void ValidateSection(string slot, ProviderSectionForm section, IReadOnlyList<ProviderField>? fields,
        List<string> errors)
    {
        Line($"{slot}.type", section.Type, errors);
        foreach (var (key, value) in section.Values)
        {
            var field = fields?.FirstOrDefault(f => f.Key == key);
            if (field?.Kind == ProviderFieldKind.MultilineText) Multiline($"{slot}.{key}", value, errors);
            else Line($"{slot}.{key}", value, errors);
            if (string.IsNullOrWhiteSpace(value) || field is null) continue;
            if (field.Kind == ProviderFieldKind.Secret && !SecretNames.IsValid(value))
                errors.Add($"{slot}.{key}: invalid secret name; use lowercase letters, digits and '-'");
            if (field.Kind == ProviderFieldKind.Number
                && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                errors.Add($"{slot}.{key} must be a whole number");
        }
        foreach (var (key, items) in section.Lists) Items($"{slot}.{key}", items, errors);
    }

    private static void Items(string field, List<string> items, List<string> errors)
    {
        if (items.Any(string.IsNullOrWhiteSpace)) errors.Add($"{field} contains an empty item");
        var bad = items.FirstOrDefault(i => i.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'));
        if (bad is not null) Line(field, bad, errors);
    }

    private static void Line(string field, string? value, List<string> errors)
    {
        if (value is null) return;
        // YAML also reads NEL and the Unicode line/paragraph separators as line breaks.
        if (value.IndexOfAny(['\n', '\r', '\u0085', '\u2028', '\u2029']) >= 0) errors.Add($"{field} must be a single line");
        else if (value.Any(char.IsControl)) errors.Add($"{field} contains a control character");
    }

    /// <summary>Like <see cref="Line"/>, but "\n" line breaks are allowed (written as a literal block scalar).</summary>
    private static void Multiline(string field, string? value, List<string> errors)
    {
        if (value is null) return;
        if (value.IndexOfAny(['\r', '\u0085', '\u2028', '\u2029']) >= 0)
            errors.Add($"{field} contains an unsupported line break (only plain line breaks are allowed)");
        else if (value.Any(c => c != '\n' && char.IsControl(c))) errors.Add($"{field} contains a control character");
    }

    private ProviderSectionForm ChangeType(ProviderSectionForm current, string type,
        IReadOnlyList<ProviderField> fields, string[] shared, string? previousClient, string? nextClient)
    {
        var next = new ProviderSectionForm { Type = type };
        foreach (var key in shared)
            if (current.Values.TryGetValue(key, out var value))
                next.Values[key] = value;
        if (nextClient is not null && previousClient is not null
            && next.Values.TryGetValue("tokenSecret", out var secret) && secret == SecretNames.Default(previousClient, Id))
            next.Values["tokenSecret"] = SecretNames.Default(nextClient, Id);
        FillDefaults(next, fields);
        return next;
    }

    private static void FillDefaults(ProviderSectionForm section, IReadOnlyList<ProviderField> fields)
    {
        foreach (var field in fields.Where(f => f.Kind != ProviderFieldKind.List))
        {
            if (section.Values.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value)) continue;
            var fallback = field.Default ?? (field.Key == "label" ? DefaultLabel : null);
            if (fallback is not null) section.Values[field.Key] = fallback;
        }
    }

    private static ProviderSectionForm Section(string type, IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ProviderField> fields, string dir)
    {
        var section = new ProviderSectionForm { Type = type };
        foreach (var (key, value) in values)
        {
            switch (value)
            {
                case string text:
                    var isPath = fields.Any(f => f.Key == key && f.Kind == ProviderFieldKind.Path);
                    section.Values[key] = isPath ? Relative(text, dir) : text;
                    break;
                case IEnumerable<string> items:
                    section.Lists[key] = items.ToList();
                    break;
            }
        }
        return section;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="dir"/> with '/' separators, or unchanged when outside it.</summary>
    private static string Relative(string path, string dir)
    {
        var relative = Path.GetRelativePath(dir, Path.GetFullPath(path, dir));
        var outside = relative == "." || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative);
        return outside
            ? path
            : relative.Replace('\\', '/');
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
