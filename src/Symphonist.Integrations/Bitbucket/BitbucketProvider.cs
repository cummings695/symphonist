using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.Bitbucket;

public sealed record BitbucketRepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch,
    string TokenSecret, string Workspace, string Slug, string Email, string MergeStrategy)
    : RepoConfig(Type, LocalPath, MainBranch, ProductionBranch, TokenSecret)
{
    /// <summary>"workspace/slug", as Bitbucket names the repository (compared ignoring case).</summary>
    public override string FullName => $"{Workspace}/{Slug}";
}

/// <summary>Bitbucket Cloud: <c>repo.type: bitbucket</c> with <c>workspace</c>, <c>slug</c>, <c>email</c>.</summary>
public sealed class BitbucketProvider : IRepoProvider
{
    public string Type => "bitbucket";

    public string HttpClientName => "bitbucket";

    public RepoConfig Parse(RepoSection section)
    {
        var dto = section.Bind<Dto>();
        return new BitbucketRepoConfig(section.Type, section.LocalPath, section.MainBranch, section.ProductionBranch,
            section.TokenSecret, section.Required(dto.Workspace, "workspace"), section.Required(dto.Slug, "slug"),
            section.Required(dto.Email, "email"), dto.MergeStrategy ?? "merge_commit");
    }

    public ISourceControl Create(ProjectConfig project, HttpClient http, string token) =>
        new BitbucketSourceControl(http, Config(project), token);

    public Uri PullRequestUrl(ProjectConfig project, long id) => BitbucketSourceControl.PullRequestUrl(Config(project), id);

    internal static BitbucketRepoConfig Config(ProjectConfig project) =>
        project.Repo as BitbucketRepoConfig
        ?? throw new InvalidOperationException($"project '{project.Id}' repo is not a Bitbucket repository");

    private sealed class Dto
    {
        public string? Workspace { get; set; }
        public string? Slug { get; set; }
        public string? Email { get; set; }
        public string? MergeStrategy { get; set; }
    }
}
