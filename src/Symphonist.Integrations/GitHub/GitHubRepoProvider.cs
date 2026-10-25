using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.GitHub;

public sealed record GitHubRepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch,
    string TokenSecret, string Owner, string Repo, string MergeStrategy)
    : RepoConfig(Type, LocalPath, MainBranch, ProductionBranch, TokenSecret)
{
    /// <summary>"owner/repo", as GitHub names the repository (compared ignoring case).</summary>
    public override string FullName => $"{Owner}/{Repo}";
}

/// <summary>GitHub: <c>repo.type: github</c> with <c>owner</c>, <c>repo</c> and optional <c>mergeStrategy</c>.</summary>
public sealed class GitHubRepoProvider : IRepoProvider
{
    private static readonly string[] MergeStrategies = ["merge", "squash", "rebase"];

    public string Type => "github";

    public string HttpClientName => "github";

    public RepoConfig Parse(RepoSection section)
    {
        var dto = section.Bind<Dto>();
        var owner = section.Required(dto.Owner, "owner");
        var repo = section.Required(dto.Repo, "repo");
        var strategy = string.IsNullOrWhiteSpace(dto.MergeStrategy) ? "merge" : dto.MergeStrategy;
        if (!MergeStrategies.Contains(strategy))
            throw new ProjectConfigException($"unknown repo.mergeStrategy '{strategy}'");
        return new GitHubRepoConfig(section.Type, section.LocalPath, section.MainBranch, section.ProductionBranch,
            section.TokenSecret, owner, repo, strategy);
    }

    public ISourceControl Create(ProjectConfig project, HttpClient http, string token) =>
        new GitHubSourceControl(http, Config(project), token);

    public Uri PullRequestUrl(ProjectConfig project, long id) => GitHubSourceControl.PullRequestUrl(Config(project), id);

    internal static GitHubRepoConfig Config(ProjectConfig project) =>
        project.Repo as GitHubRepoConfig
        ?? throw new InvalidOperationException($"project '{project.Id}' repo is not a GitHub repository");

    private sealed class Dto
    {
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? MergeStrategy { get; set; }
    }
}
