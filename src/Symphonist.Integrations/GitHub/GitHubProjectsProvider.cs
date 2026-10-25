using System.Globalization;
using Microsoft.Extensions.Logging;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.GitHub;

/// <param name="Project">The Project (v2) number, from the board URL.</param>
/// <param name="StatusField">Name of the single-select field used as the status; default "Status".</param>
/// <param name="Statuses">Columns to pick up; null = the pipeline's statuses without the done one.</param>
/// <param name="Repositories">"owner/repo" names to pick up; null = the default from the project's repo.</param>
public sealed record GitHubProjectsTrackerConfig(string Type, string TokenSecret, string Owner, int Project,
    string Label, string StatusField, IReadOnlyList<string>? Statuses, IReadOnlyList<string>? Repositories)
    : TrackerConfig(Type, TokenSecret);

/// <summary>
/// GitHub Projects (v2): <c>tracker.type: github-projects</c> with <c>owner</c>, <c>project</c> and <c>label</c>;
/// optional <c>assignee</c> (only <c>me</c>), <c>statusField</c>, <c>statuses</c> and <c>repositories</c>.
/// </summary>
public sealed class GitHubProjectsProvider : ITrackerProvider
{
    public string Type => "github-projects";

    public string DisplayName => "GitHub";

    public string HttpClientName => "github";

    public TrackerConfig Parse(TrackerSection section)
    {
        var dto = section.Bind<Dto>();
        var owner = section.Required(dto.Owner, "owner");
        var projectText = section.Required(dto.Project, "project");
        if (!int.TryParse(projectText, NumberStyles.None, CultureInfo.InvariantCulture, out var project) || project <= 0)
            throw new ProjectConfigException("invalid 'tracker.project'");
        var label = section.Required(dto.Label, "label");
        if (!string.IsNullOrWhiteSpace(dto.Assignee) && !string.Equals(dto.Assignee, "me", StringComparison.OrdinalIgnoreCase))
            throw new ProjectConfigException($"unsupported tracker.assignee '{dto.Assignee}'");
        var statusField = string.IsNullOrWhiteSpace(dto.StatusField) ? "Status" : dto.StatusField;
        return new GitHubProjectsTrackerConfig(section.Type, section.TokenSecret, owner, project, label, statusField,
            List(dto.Statuses), List(dto.Repositories));
    }

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new("owner", "Owner", ProviderFieldKind.Text, Required: true, Help: "Organization or user that owns the Project"),
        new("project", "Project number", ProviderFieldKind.Number, Required: true, Help: "Project (v2) number, from the board URL"),
        new("label", "Label", ProviderFieldKind.Text, Required: true, Help: "Open issues assigned to you with this label are picked up"),
        new("statusField", "Status field", ProviderFieldKind.Text, Default: "Status", Help: "Single-select board field used as the status"),
        new("statuses", "Statuses", ProviderFieldKind.List, Help: "Columns to pick up; empty = the pipeline statuses without the done one"),
        new("repositories", "Repositories", ProviderFieldKind.List, Help: "owner/repo names to pick up; empty = this project repo"),
        new("tokenSecret", "Token secret name", ProviderFieldKind.Secret, Required: true, Help: "Name of the secret holding the GitHub token"),
    ];

    public IReadOnlyDictionary<string, object?> ToValues(TrackerConfig config)
    {
        var c = (GitHubProjectsTrackerConfig)config;
        var values = new Dictionary<string, object?>
        {
            ["owner"] = c.Owner,
            ["project"] = c.Project.ToString(CultureInfo.InvariantCulture),
            ["label"] = c.Label,
            ["statusField"] = c.StatusField,
        };
        if (c.Statuses is { Count: > 0 }) values["statuses"] = c.Statuses;
        if (c.Repositories is { Count: > 0 }) values["repositories"] = c.Repositories;
        values["tokenSecret"] = c.TokenSecret;
        return values;
    }

    public ITracker Create(ProjectConfig project, HttpClient http, string token, ILoggerFactory loggers) =>
        new GitHubProjectsTracker(http, Config(project), token, PickupStatuses(project), RequiredStatuses(project),
            Repositories(project), loggers.CreateLogger<GitHubProjectsTracker>());

    public Task<TrackerDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string token, CancellationToken ct) =>
        new GitHubProjectsTracker(http, Config(draft), token, PickupStatuses(draft), RequiredStatuses(draft),
            Repositories(draft)).DiscoverAsync(ct);

    /// <summary>Key = <c>{repository name}-{issue number}</c>; the owner is that of the matching repositories entry.</summary>
    public Uri? IssueUrl(ProjectConfig project, string key)
    {
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(key.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
            return null;
        var name = key[..dash];
        var (owner, repo) = Repositories(project)?
            .Select(r => r.Split('/'))
            .Where(parts => parts.Length == 2 && string.Equals(parts[1], name, StringComparison.OrdinalIgnoreCase))
            .Select(parts => (parts[0], parts[1]))
            .FirstOrDefault() is { Item1: not null } match ? match : (Config(project).Owner, name);
        return new Uri($"https://github.com/{owner}/{repo}/issues/{number.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>The configured <c>statuses</c>, else every pipeline-named status except the done stage's.</summary>
    public static IReadOnlyList<string> PickupStatuses(ProjectConfig p)
    {
        if (Config(p).Statuses is { } statuses) return statuses;
        var done = p.Pipeline.Where(s => s.Kind == StageKind.Done).Select(s => s.OnEnterStatus).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredStatuses(p).Where(s => !done.Contains(s)).ToList();
    }

    /// <summary>
    /// Every status the pipeline names (<c>entryStatuses</c>, <c>onEnterStatus</c>, <c>onGateStatus</c>,
    /// <c>approvedWhenStatus</c> of every stage, done included), distinct ignoring case, in pipeline order.
    /// </summary>
    public static IReadOnlyList<string> RequiredStatuses(ProjectConfig p) =>
        p.Pipeline
            .SelectMany(s => s.EntryStatuses.Append(s.OnEnterStatus).Append(s.OnGateStatus).Append(s.ApprovedWhenStatus))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The configured <c>repositories</c>, else the project's GitHub repo, else null (no filter).</summary>
    public static IReadOnlyList<string>? Repositories(ProjectConfig p) =>
        Config(p).Repositories ?? (p.Repo is GitHubRepoConfig repo ? [repo.FullName] : null);

    private static IReadOnlyList<string>? List(List<string>? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static GitHubProjectsTrackerConfig Config(ProjectConfig project) =>
        project.Tracker as GitHubProjectsTrackerConfig
        ?? throw new InvalidOperationException($"project '{project.Id}' tracker is not a GitHub Projects tracker");

    private sealed class Dto
    {
        public string? Owner { get; set; }
        public string? Project { get; set; }
        public string? Label { get; set; }
        public string? Assignee { get; set; }
        public string? StatusField { get; set; }
        public List<string>? Statuses { get; set; }
        public List<string>? Repositories { get; set; }
    }
}
