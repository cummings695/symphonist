using Symphonist.Core.Config;
using Symphonist.Core.Onboarding;
using Symphonist.Integrations;

namespace Symphonist.Core.Tests.Onboarding;

public class ProjectFormTests
{
    private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

    [Fact]
    public void From_makes_paths_relative_to_config_dir()
    {
        var form = ProjectYamlWriterTests.Filled();
        form.Repo.Values["localPath"] = "clones/orders";
        form.Prompts[StageKind.Plan] = "prompts/plan.md";
        form.Review.PromptPath = "prompts/review.md";
        var outside = Path.Combine(Path.GetTempPath(), "acme-orders");
        var config = ProjectConfigLoader.Parse(ProjectYamlWriter.NewFile(form, BuiltInProviders.Registry), ConfigDir,
            "test.yaml", BuiltInProviders.Registry);
        Assert.True(Path.IsPathFullyQualified(config.PromptOverrides[StageKind.Plan]));

        var back = ProjectForm.From(config, BuiltInProviders.Registry);

        Assert.Equal("prompts/plan.md", back.Prompts[StageKind.Plan]);
        Assert.Equal("prompts/review.md", back.Review.PromptPath);
        Assert.Equal("clones/orders", back.Repo.Values["localPath"]);

        form.Repo.Values["localPath"] = outside;
        config = ProjectConfigLoader.Parse(ProjectYamlWriter.NewFile(form, BuiltInProviders.Registry), ConfigDir,
            "test.yaml", BuiltInProviders.Registry);
        Assert.Equal(Path.GetFullPath(outside), ProjectForm.From(config, BuiltInProviders.Registry).Repo.Values["localPath"]);
    }

    [Fact]
    public void From_maps_every_section()
    {
        var config = ProjectConfigLoader.Load(Path.Combine(ConfigDir, "orders.example.yaml"), BuiltInProviders.Registry);

        var form = ProjectForm.From(config, BuiltInProviders.Registry);

        Assert.Equal("orders", form.Id);
        Assert.Equal("Orders (Acme)", form.Name);
        Assert.Equal("jira", form.Tracker.Type);
        Assert.Equal("ACME", form.Tracker.Values["projectKey"]);
        Assert.Equal("acme", form.Repo.Values["workspace"]);
        Assert.Equal("bitbucket-pipelines", form.Build.Type);
        Assert.Equal(["api.nuget.org", "registry.npmjs.org"], form.Agent.AllowedDomains);
        Assert.Equal(7, form.Pipeline.Count);
        Assert.Equal(GatePolicy.Human, form.Pipeline[0].Gate);
        Assert.Equal(["Backlog"], form.Pipeline[0].EntryStatuses);
        Assert.Equal("pricing", Assert.Single(form.RiskAreas).Name);
        Assert.Equal(ReviewMode.Off, form.Review.Mode);
        Assert.Empty(form.Review.AllowedTools);
        Assert.Null(form.Review.Model);
    }

    [Fact]
    public void Changing_tracker_type_keeps_shared_values_and_resets_provider_fields()
    {
        var form = ProjectForm.NewProject("orders", BuiltInProviders.Registry);
        form.Tracker.Values["site"] = "https://acme.atlassian.net";
        form.Tracker.Values["jql"] = "project = ACME";
        Assert.Equal("jira-orders", form.Tracker.Values["tokenSecret"]);

        form.ChangeTrackerType("github-projects", BuiltInProviders.Registry);

        Assert.Equal("github-projects", form.Tracker.Type);
        Assert.False(form.Tracker.Values.ContainsKey("site"));
        Assert.False(form.Tracker.Values.ContainsKey("jql"));
        Assert.Equal("Claude", form.Tracker.Values["label"]);
        Assert.Equal("Status", form.Tracker.Values["statusField"]);
        Assert.Equal("github-orders", form.Tracker.Values["tokenSecret"]);

        form.Tracker.Values["tokenSecret"] = "my-token";
        form.ChangeTrackerType("jira", BuiltInProviders.Registry);
        Assert.Equal("my-token", form.Tracker.Values["tokenSecret"]);
        Assert.False(form.Tracker.Values.ContainsKey("label"));
    }

    [Fact]
    public void Changing_repo_type_repicks_build()
    {
        var form = ProjectForm.NewProject("orders", BuiltInProviders.Registry);
        form.Repo.Values["localPath"] = "C:/src/orders";
        form.Repo.Values["workspace"] = "acme";
        form.Build.Values["timeoutMinutes"] = "30";

        form.ChangeRepoType("github", BuiltInProviders.Registry);

        Assert.Equal("github", form.Repo.Type);
        Assert.Equal("C:/src/orders", form.Repo.Values["localPath"]);
        Assert.False(form.Repo.Values.ContainsKey("workspace"));
        Assert.Equal("github-orders", form.Repo.Values["tokenSecret"]);
        Assert.Equal("github-actions", form.Build.Type);
        Assert.Equal("30", form.Build.Values["timeoutMinutes"]);
        Assert.Throws<ArgumentException>(() => form.ChangeBuildType("bitbucket-pipelines", BuiltInProviders.Registry));
        form.ChangeBuildType("github-actions", BuiltInProviders.Registry);
    }

    [Fact]
    public void Default_secret_names_follow_client_names()
    {
        var form = ProjectForm.NewProject("Acme_Orders", BuiltInProviders.Registry, "github-projects", "github");

        Assert.Equal("github-acme-orders", form.Tracker.Values["tokenSecret"]);
        Assert.Equal("github-acme-orders", form.Repo.Values["tokenSecret"]);
        Assert.Equal("github-acme-orders", SecretNames.Default("github", "Acme_Orders"));
        Assert.True(SecretNames.IsValid("github-acme-orders"));
        Assert.False(SecretNames.IsValid("-github"));
        Assert.False(SecretNames.IsValid("Jira Acme"));
        Assert.False(SecretNames.IsValid(new string('a', 65)));
    }

    [Fact]
    public void Clone_is_deep()
    {
        var form = ProjectYamlWriterTests.Filled("github-projects", "github");
        form.Tracker.Lists["statuses"] = ["Backlog"];
        form.RiskAreas.Add(new RiskAreaForm { Name = "a", Description = "d", Paths = ["p"] });
        form.Prompts[StageKind.Plan] = "prompts/plan.md";
        form.Agent.PackagesApt.Add("ripgrep");
        form.Agent.Type = AgentTypes.Codex;
        form.Agent.CodexAuth = CodexAuth.ChatGpt;
        form.Agent.CodexApiKeySecret = "openai-orders";
        form.Pipeline[2].AgentType = AgentTypes.Codex;
        form.Pipeline[2].AgentModel = "m";
        form.Pipeline[2].AgentTimeoutMinutes = 5;
        form.Review.AgentType = AgentTypes.Codex;
        form.Evidence.AgentType = AgentTypes.ClaudeCode;

        var clone = form.Clone();
        Assert.Equal(AgentTypes.Codex, clone.Agent.Type);
        Assert.Equal(CodexAuth.ChatGpt, clone.Agent.CodexAuth);
        Assert.Equal("openai-orders", clone.Agent.CodexApiKeySecret);
        Assert.Equal((AgentTypes.Codex, "m", 5),
            (clone.Pipeline[2].AgentType, clone.Pipeline[2].AgentModel, clone.Pipeline[2].AgentTimeoutMinutes));
        Assert.Equal(AgentTypes.Codex, clone.Review.AgentType);
        Assert.Equal(AgentTypes.ClaudeCode, clone.Evidence.AgentType);
        clone.Tracker.Values["label"] = "Other";
        clone.Tracker.Lists["statuses"].Add("Planned");
        clone.Repo.Values["owner"] = "other";
        clone.Build.Values["timeoutMinutes"] = "1";
        clone.Agent.AllowedDomains.Add("example.com");
        clone.Agent.PackagesApt.Add("jq");
        clone.Pipeline[0].EntryStatuses.Add("X");
        clone.Pipeline[0].Id = "changed";
        clone.RiskAreas[0].Paths.Add("q");
        clone.Review.AllowedTools.Add("Read");
        clone.Prompts[StageKind.Plan] = "other.md";

        Assert.Equal("Claude", form.Tracker.Values["label"]);
        Assert.Equal(["Backlog"], form.Tracker.Lists["statuses"]);
        Assert.Equal("acme", form.Repo.Values["owner"]);
        Assert.Equal("45", form.Build.Values["timeoutMinutes"]);
        Assert.Equal(2, form.Agent.AllowedDomains.Count);
        Assert.Equal(["ripgrep"], form.Agent.PackagesApt);
        Assert.Empty(form.Pipeline[0].EntryStatuses);
        Assert.Equal("triage", form.Pipeline[0].Id);
        Assert.Equal(["p"], form.RiskAreas[0].Paths);
        Assert.Empty(form.Review.AllowedTools);
        Assert.Equal("prompts/plan.md", form.Prompts[StageKind.Plan]);
    }
}
