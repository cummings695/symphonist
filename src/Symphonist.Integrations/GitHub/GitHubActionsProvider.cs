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
