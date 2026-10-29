using Symphonist.Core.Agents;
using Symphonist.Core.Config;

namespace Symphonist.Core.Tests.Agents;

public class UsageLimitTests
{
    private sealed record TestTracker() : TrackerConfig("test", "t");

    private sealed record TestRepo() : RepoConfig("test", "/src", "main", "production", "t")
    {
        public override string FullName => "o/n";
    }

    private sealed record TestBuild() : BuildConfig("test", 10);

    private static AgentSettings Claude(string? model = null, Isolation isolation = Isolation.Container) =>
        new(AgentTypes.ClaudeCode, isolation, [], [], 10, model, null);

    private static AgentSettings Codex(CodexAuth auth, string secret = "openai-api-key") =>
        new(AgentTypes.Codex, Isolation.Container, [], [], 10, null, null) { Codex = new CodexSettings(auth, secret) };

    [Fact]
    public void Claude_jobs_share_one_key()
    {
        Assert.Equal("claude-code", UsageLimits.KeyFor(Claude()));
        Assert.Equal("claude-code", UsageLimits.KeyFor(Claude("opus", Isolation.Host)));
    }

    [Fact]
    public void Codex_keys_follow_the_sign_in()
    {
        Assert.Equal("codex:chatgpt", UsageLimits.KeyFor(Codex(CodexAuth.ChatGpt)));
        Assert.Equal("codex:api-key:openai-orders", UsageLimits.KeyFor(Codex(CodexAuth.ApiKey, "openai-orders")));
    }

    [Fact]
    public void Provider_names_follow_the_key()
    {
        Assert.Equal("Claude", UsageLimits.ProviderName("claude-code"));
        Assert.Equal("Codex", UsageLimits.ProviderName("codex:chatgpt"));
        Assert.Equal("Codex", UsageLimits.ProviderName("codex:api-key:openai-orders"));
    }

    [Fact]
    public void Keys_in_use_cover_stage_review_and_evidence_agents()
    {
        StageDefinition Stage(string id, AgentSettings? agent) =>
            new(id, StageKind.Implement, GatePolicy.None, [], null, null, null) { Agent = agent };
        var project = new ProjectConfig("orders", "Orders", new TestTracker(), new TestRepo(), new TestBuild(),
            Claude(), 3, [Stage("plan", null), Stage("implement", Codex(CodexAuth.ChatGpt))],
            new Dictionary<StageKind, string>(), "/config")
        {
            Review = new ReviewConfig(ReviewMode.Active, Codex(CodexAuth.ApiKey, "openai-review"), null),
            Evidence = new EvidenceConfig(EvidenceMode.HighRisk, EvidenceRun.None, [], Codex(CodexAuth.ApiKey, "openai-evidence"), null),
        };

        Assert.Equal(
            new HashSet<string> { "claude-code", "codex:chatgpt", "codex:api-key:openai-review", "codex:api-key:openai-evidence" },
            UsageLimits.KeysInUse(project));
    }

    [Fact]
    public void Next_occurrence_is_today_or_tomorrow_in_the_zone()
    {
        var utc = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero), UsageLimit.NextOccurrence(now, utc, 15, 0));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), UsageLimit.NextOccurrence(now, utc, 9, 0));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero), UsageLimit.NextOccurrence(now, utc, 10, 0));
    }
}
