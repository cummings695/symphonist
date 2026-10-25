using System.Net;
using System.Text.Json;
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

    public string DisplayName => "Bitbucket";

    public string HttpClientName => "bitbucket";

    public IReadOnlyList<string> FullNameKeys { get; } = ["workspace", "slug"];

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new("workspace", "Workspace", ProviderFieldKind.Text, Required: true, Help: "Bitbucket workspace id"),
        new("slug", "Repository slug", ProviderFieldKind.Text, Required: true),
        new("localPath", "Local clone path", ProviderFieldKind.Path, Required: true, Help: "Existing local clone; use an absolute path"),
        new("mainBranch", "Main branch", ProviderFieldKind.Text, Default: "main", Help: "Feature PRs target it"),
        new("productionBranch", "Production branch", ProviderFieldKind.Text, Default: "production", Help: "Release PRs target it"),
        new("email", "Account email", ProviderFieldKind.Text, Required: true, Help: "Bitbucket account email"),
        new("tokenSecret", "Token secret name", ProviderFieldKind.Secret, Required: true, Help: "Name of the secret holding the Bitbucket API token"),
        new("mergeStrategy", "Merge strategy", ProviderFieldKind.Text, Default: "merge_commit"),
    ];

    public IReadOnlyDictionary<string, object?> ToValues(RepoConfig config)
    {
        var c = (BitbucketRepoConfig)config;
        return new Dictionary<string, object?>
        {
            ["workspace"] = c.Workspace,
            ["slug"] = c.Slug,
            ["localPath"] = c.LocalPath,
            ["mainBranch"] = c.MainBranch,
            ["productionBranch"] = c.ProductionBranch,
            ["email"] = c.Email,
            ["tokenSecret"] = c.TokenSecret,
            ["mergeStrategy"] = c.MergeStrategy,
        };
    }

    public RepoConfig Parse(RepoSection section)
    {
        var dto = section.Bind<Dto>();
        return new BitbucketRepoConfig(section.Type, section.LocalPath, section.MainBranch, section.ProductionBranch,
            section.TokenSecret, section.Required(dto.Workspace, "workspace"), section.Required(dto.Slug, "slug"),
            section.Required(dto.Email, "email"), dto.MergeStrategy ?? "merge_commit");
    }

    public ISourceControl Create(ProjectConfig project, HttpClient http, string token) =>
        new BitbucketSourceControl(http, Config(project), token);

    public async Task<RepositoryDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string token,
        CancellationToken ct)
    {
        var config = Config(draft);
        var api = new BitbucketApi(http, config, token);
        string fullName, mainBranch;
        using (var doc = await api.SendAsync(HttpMethod.Get, api.RepoPath, null, ct))
        {
            var root = doc.RootElement;
            fullName = Text(root, "full_name") is { Length: > 0 } name ? name : config.FullName;
            mainBranch = root.TryGetProperty("mainbranch", out var main) && main.ValueKind == JsonValueKind.Object
                ? Text(main, "name") : "";
        }

        var branchPath = $"{api.RepoPath}/refs/branches/{Uri.EscapeDataString(config.ProductionBranch)}";
        var (status, text) = await api.SendRawAsync(HttpMethod.Get, branchPath, null, ct);
        var exists = status switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            _ => throw BitbucketApi.Failure(HttpMethod.Get, branchPath, status, text),
        };
        return new RepositoryDiscovery(fullName, mainBranch, exists);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

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
