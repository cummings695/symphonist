using System.Globalization;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.GitHub;

public sealed record GitHubActionsConfig(string Type, int TimeoutMinutes,
    IReadOnlyList<string> BuildWorkflows, IReadOnlyList<string> DeployWorkflows) : BuildConfig(Type, TimeoutMinutes);

/// <summary>GitHub Actions: <c>build.type: github-actions</c>, read through the GitHub repository.</summary>
public sealed class GitHubActionsProvider : IBuildProvider
{
    public string Type => "github-actions";

    public string RepoType => "github";

    public string DisplayName => "GitHub Actions";

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new("timeoutMinutes", "Timeout (minutes)", ProviderFieldKind.Number, Default: "45", Help: "How long to wait for a build or deployment"),
        new("buildWorkflows", "Build workflows", ProviderFieldKind.List, Help: "Workflow file names that build; empty = all"),
        new("deployWorkflows", "Deploy workflows", ProviderFieldKind.List, Help: "Workflow file names that deploy; empty = none"),
    ];

    public IReadOnlyDictionary<string, object?> ToValues(BuildConfig config)
    {
        var c = (GitHubActionsConfig)config;
        var values = new Dictionary<string, object?>
        {
            ["timeoutMinutes"] = c.TimeoutMinutes.ToString(CultureInfo.InvariantCulture),
        };
        if (c.BuildWorkflows.Count > 0) values["buildWorkflows"] = c.BuildWorkflows;
        if (c.DeployWorkflows.Count > 0) values["deployWorkflows"] = c.DeployWorkflows;
        return values;
    }

    public BuildConfig Parse(BuildSection section)
    {
        var dto = section.Bind<Dto>();
        return new GitHubActionsConfig(section.Type, section.TimeoutMinutes,
            Items(dto.BuildWorkflows, "build.buildWorkflows"), Items(dto.DeployWorkflows, "build.deployWorkflows"));
    }

    public IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken) =>
        new GitHubActionsWatcher(http, GitHubRepoProvider.Config(project),
            project.Build as GitHubActionsConfig
            ?? throw new InvalidOperationException($"project '{project.Id}' build is not GitHub Actions"),
            repoToken);

    private static List<string> Items(List<string>? items, string field) =>
        items is null ? []
        : items.Any(string.IsNullOrWhiteSpace) ? throw new ProjectConfigException($"{field} contains an empty item")
        : items;

    private sealed class Dto
    {
        public List<string>? BuildWorkflows { get; set; }
        public List<string>? DeployWorkflows { get; set; }
    }
}
