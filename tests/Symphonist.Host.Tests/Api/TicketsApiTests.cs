using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Integrations;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Api;

[Collection(SymphonistHomeCollection.Name)]
public sealed class TicketsApiTests : IDisposable
{
    private static readonly DateTimeOffset Waiting = DateTimeOffset.Parse("2026-10-21T09:00:00Z");

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) => throw new NotSupportedException();
        public void Remove(string name) => throw new NotSupportedException();
    }

    /// <summary>Answers every request with 503, counting attempts.</summary>
    private sealed class UnavailableHandler(TicketsApiTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.httpAttempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private int httpAttempts;
    private readonly TestHome home = new();
    private readonly FakeTicketStore store = new();
    private readonly FakeTracker tracker = new();
    private readonly FakeAgentRunner agent = new();
    private readonly FakeProcessRunner processes = new();
    private readonly FakeIntegrationFactory integrations;
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public TicketsApiTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", TestHome.HostAgent));
        integrations = new FakeIntegrationFactory(new ProjectIntegrations(tracker, new FakeSourceControl(), new FakeBuildWatcher()));
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ITicketStore>(store);
            s.AddSingleton<IIntegrationFactory>(integrations);
            s.AddSingleton<IGitWorkspace>(new FakeGitWorkspace());
            s.AddSingleton<IAgentRunner>(agent);
            s.AddSingleton<IProcessRunner>(processes);
            s.AddSingleton<ISecretStore>(new NoSecrets());
            foreach (var name in BuiltInProviders.Registry.HttpClientNames)
                s.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler(this));
            // Drive the orchestrator only through the API (no background ticks, not even after commands).
            s.AddSingleton(new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false));
            var service = s.Single(d => d.ImplementationType == typeof(OrchestratorService));
            s.Remove(service);
        }));
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        home.Dispose();
    }

    private string Token => File.ReadAllText(SymphonistPaths.Resolve("api-token")).Trim();

    private HttpClient Authorized()
    {
        client.DefaultRequestHeaders.Remove("X-Symphonist-Token");
        client.DefaultRequestHeaders.Add("X-Symphonist-Token", Token);
        return client;
    }

    private async Task<Ticket> Seed(string key, string stage, StageStatus status, string project = "orders")
    {
        var t = new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = project, Key = key, Title = $"Title {key}", StageId = stage, Status = status,
            Attempts = 1, WaitingSince = Waiting, PullRequestId = 42, LastSourceStatus = "Planned",
        };
        await store.SaveAsync(t, default);
        return t;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Rejects_missing_token() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);

    [Fact]
    public async Task Rejects_wrong_token()
    {
        client.DefaultRequestHeaders.Add("X-Symphonist-Token", "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/poll", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_need_no_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
    }

    [Fact]
    public async Task Token_is_32_random_bytes_base64url_and_stable()
    {
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Token);
        Assert.Equal(Token, ApiToken.LoadOrCreate(SymphonistPaths.Resolve("api-token")));
    }

    [Fact]
    public async Task Lists_tickets_with_token()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await Seed("BIL-1", "triage", StageStatus.Blocked, "billing");

        var all = await Json(await Authorized().GetAsync("/api/tickets"));
        Assert.Equal(2, all.GetArrayLength());

        var orders = await Json(await Authorized().GetAsync("/api/tickets?project=orders"));
        var t = Assert.Single(orders.EnumerateArray());
        Assert.Equal("ORD-1", t.GetProperty("key").GetString());
        Assert.Equal("orders", t.GetProperty("project").GetString());
        Assert.Equal("Title ORD-1", t.GetProperty("title").GetString());
        Assert.Equal("plan", t.GetProperty("stage").GetString());
        Assert.Equal("AwaitingApproval", t.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("blockedReason").ValueKind);
        Assert.Equal(1, t.GetProperty("attempts").GetInt32());
        Assert.Equal(Waiting, t.GetProperty("waitingSince").GetDateTimeOffset());
        Assert.Equal("https://bitbucket.org/acme/orders/pull-requests/42", t.GetProperty("prUrl").GetString());
    }

    [Fact]
    public async Task Ticket_detail_includes_gate_reviews()
    {
        var t = await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        t.Risk = RiskLevel.High;
        await store.SaveAsync(t, default);
        var at = Waiting.AddMinutes(5);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Waiting, at, ReviewMode.Shadow, GatePolicy.Human,
            new GateVerdict(RiskLevel.High, "touches pricing", ["touches risk area 'pricing'"], ["diff"], ["pricing"])), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "implement", Waiting, GateDecision.Approved,
            false, at.AddMinutes(1)), default);

        var shown = await Json(await Authorized().GetAsync("/api/tickets/orders/ORD-1"));

        var r = Assert.Single(shown.GetProperty("reviews").EnumerateArray());
        Assert.Equal("implement", r.GetProperty("stage").GetString());
        Assert.Equal("Shadow", r.GetProperty("mode").GetString());
        Assert.Equal("High", r.GetProperty("risk").GetString());
        Assert.Equal("touches pricing", r.GetProperty("summary").GetString());
        Assert.Equal(["touches risk area 'pricing'"], r.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["diff"], r.GetProperty("checked").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["pricing"], r.GetProperty("areasTouched").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Approved", r.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("error").ValueKind);

        var list = await Json(await Authorized().GetAsync("/api/tickets?project=orders"));
        Assert.Equal("High", Assert.Single(list.EnumerateArray()).GetProperty("risk").GetString());
    }

    [Fact]
    public async Task Projects_include_review_mode_and_agreement()
    {
        home.WriteProject("orders.yaml", TestHome.ReviewedProjectYaml("orders", "shadow"));
        factory.Services.GetRequiredService<ProjectRegistry>().Reload();
        var t = await Seed("ORD-1", "implement", StageStatus.AwaitingApproval);
        await store.AddGateReviewAsync(new GateReview(t.Id, "orders", "implement", Waiting, Waiting.AddMinutes(1), ReviewMode.Shadow,
            GatePolicy.Human, new GateVerdict(RiskLevel.Low, "ok", [], ["diff"], [])), default);
        await store.AddGateDecisionAsync(new GateDecisionRecord(t.Id, "orders", "implement", Waiting, GateDecision.SentBack,
            false, Waiting.AddMinutes(2)), default);

        var projects = await Json(await Authorized().GetAsync("/api/projects"));

        Assert.Equal("Off", projects[0].GetProperty("review").GetString());
        Assert.Equal(0, projects[0].GetProperty("agreement").GetArrayLength());
        Assert.Equal("Shadow", projects[1].GetProperty("review").GetString());
        var a = Assert.Single(projects[1].GetProperty("agreement").EnumerateArray());
        Assert.Equal("implement", a.GetProperty("stageId").GetString());
        Assert.Equal(1, a.GetProperty("decided").GetInt32());
        Assert.Equal(0, a.GetProperty("agreed").GetInt32());
        Assert.Equal(1, a.GetProperty("falseLows").GetInt32());
    }

    [Fact]
    public async Task Unknown_project_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().GetAsync("/api/tickets?project=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().GetAsync("/api/tickets/nope/ORD-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().PostAsync("/api/tickets/nope/ORD-1/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().PostAsync("/api/projects/nope/pause", null)).StatusCode);
    }

    [Fact]
    public async Task Shows_ticket_with_events()
    {
        var t = await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        await store.AddEventAsync(t.Id, "triage", "picked up at triage", Waiting, default);

        var shown = await Json(await Authorized().GetAsync("/api/tickets/orders/ORD-1"));

        Assert.Equal("ORD-1", shown.GetProperty("key").GetString());
        var e = Assert.Single(shown.GetProperty("events").EnumerateArray());
        Assert.Equal("picked up at triage", e.GetProperty("message").GetString());
        Assert.Equal("triage", e.GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().GetAsync("/api/tickets/orders/ORD-404")).StatusCode);
    }

    [Fact]
    public async Task Approve_unknown_ticket_is_404()
    {
        var response = await Authorized().PostAsync("/api/tickets/orders/ORD-404/approve", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approve_returns_the_outcome()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);

        var outcome = await Json(await Authorized().PostAsync("/api/tickets/orders/ORD-1/approve", null));

        Assert.True(outcome.GetProperty("changed").GetBoolean());
        Assert.Equal("In Progress", outcome.GetProperty("sourceStatusToSet").GetString());
        Assert.Equal("implement", store.Single("ORD-1").StageId);
        Assert.Contains(("ORD-1", "In Progress"), tracker.Transitions);
    }

    [Fact]
    public async Task Approve_with_missing_secret_still_returns_the_outcome()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set");

        var outcome = await Json(await Authorized().PostAsync("/api/tickets/orders/ORD-1/approve", null));

        Assert.True(outcome.GetProperty("changed").GetBoolean());
        Assert.Equal("implement", store.Single("ORD-1").StageId);
        Assert.Contains("tracker sync skipped: secret 'jira-acme' not set", store.EventMessages("ORD-1"));
    }

    [Fact]
    public async Task Send_back_passes_feedback()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);

        var outcome = await Json(await Authorized().PostAsJsonAsync("/api/tickets/orders/ORD-1/send-back",
            new { feedback = "split the export into pages", toStage = "triage" }));

        Assert.True(outcome.GetProperty("changed").GetBoolean());
        var t = store.Single("ORD-1");
        Assert.Equal("split the export into pages", t.Feedback);
        Assert.Equal("triage", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
    }

    [Fact]
    public async Task Send_back_requires_feedback()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        var response = await Authorized().PostAsJsonAsync("/api/tickets/orders/ORD-1/send-back", new { feedback = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(StageStatus.AwaitingApproval, store.Single("ORD-1").Status);
    }

    [Fact]
    public async Task Send_back_to_unknown_stage_is_404()
    {
        await Seed("ORD-1", "plan", StageStatus.AwaitingApproval);
        var response = await Authorized().PostAsJsonAsync("/api/tickets/orders/ORD-1/send-back",
            new { feedback = "x", toStage = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unblock_returns_the_outcome()
    {
        await Seed("ORD-1", "plan", StageStatus.Blocked);

        var outcome = await Json(await Authorized().PostAsync("/api/tickets/orders/ORD-1/unblock", null));

        Assert.Equal("unblocked", outcome.GetProperty("message").GetString());
        Assert.Equal(StageStatus.Pending, store.Single("ORD-1").Status);
    }

    [Fact]
    public async Task Forget_endpoint_returns_204_and_404()
    {
        await Seed("ORD-1", "plan", StageStatus.Blocked);

        Assert.Equal(HttpStatusCode.NoContent, (await Authorized().DeleteAsync("/api/tickets/orders/ORD-1")).StatusCode);
        Assert.Empty(store.Tickets);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().DeleteAsync("/api/tickets/orders/ORD-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized().DeleteAsync("/api/tickets/nope/ORD-1")).StatusCode);
    }

    [Fact]
    public async Task Forget_refused_while_running_is_409()
    {
        tracker.Tickets.Add(new SourceTicket("ORD-1", "T", "", "Backlog", [], new Uri("https://acme.atlassian.net/browse/ORD-1")));
        var release = new TaskCompletionSource<AgentRunResult>();
        agent.OnRunAsync = _ => release.Task;
        var o = factory.Services.GetRequiredService<Orchestrator>();
        await o.TickAsync(default); // starts the triage run, which blocks

        var response = await Authorized().DeleteAsync("/api/tickets/billing/ORD-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ticket ORD-1 has an agent run in progress; try again when it finishes",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Contains(store.Tickets, t => t.ProjectId == "billing" && t.Key == "ORD-1");
        release.SetResult(FakeAgentRunner.Ok(FakeAgentRunner.Output()));
        await o.WhenIdleAsync();
    }

    [Fact]
    public async Task Poll_is_accepted_and_ticks_in_the_background()
    {
        tracker.Tickets.Add(new SourceTicket("ORD-9", "New", "", "Backlog", [], new Uri("https://acme.atlassian.net/browse/ORD-9")));

        var response = await Authorized().PostAsync("/api/poll", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Tickets.All(t => t.Key != "ORD-9") && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Contains(store.Tickets, t => t.Key == "ORD-9");
    }

    [Fact]
    public async Task Poll_requests_are_coalesced()
    {
        // Hold the first tick until every request was answered, so no tick can finish between requests.
        using var allAccepted = new ManualResetEventSlim();
        tracker.OnQuery = () =>
        {
            allAccepted.Wait(TimeSpan.FromSeconds(10));
            return [];
        };
        var http = Authorized();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => http.PostAsync("/api/poll", null)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        allAccepted.Set();

        await factory.Services.GetRequiredService<Orchestrator>().WhenIdleAsync();

        // Two projects, so one tick queries twice: at most two ticks (one running + one pending) means at most 4.
        Assert.InRange(tracker.QueryCount, 1, 4);
    }

    [Fact]
    public async Task Lists_pauses_and_resumes_projects()
    {
        var projects = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal(["billing", "orders"], projects.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        var billing = projects[0];
        Assert.Equal("Project billing", billing.GetProperty("name").GetString());
        Assert.False(billing.GetProperty("paused").GetBoolean());
        Assert.Equal("Host", billing.GetProperty("isolation").GetString());
        Assert.Equal("Container", projects[1].GetProperty("isolation").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Authorized().PostAsync("/api/projects/orders/pause", null)).StatusCode);
        var paused = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.True(paused[1].GetProperty("paused").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await Authorized().PostAsync("/api/projects/orders/resume", null)).StatusCode);
        var resumed = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.False(resumed[1].GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Projects_report_their_integration_problem()
    {
        var before = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal(JsonValueKind.Null, before[1].GetProperty("problem").ValueKind);

        integrations.Error = new InvalidOperationException("secret 'jira-acme' not set; run: symphonist secret set jira-acme");
        await factory.Services.GetRequiredService<Orchestrator>().TickAsync(CancellationToken.None);

        var after = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal("secret 'jira-acme' not set; run: symphonist secret set jira-acme", after[1].GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Startup_with_docker_unavailable_still_serves_the_api()
    {
        // The orders project uses container isolation; the fake runner reports Docker as unavailable.
        Assert.Equal(HttpStatusCode.OK, (await Authorized().GetAsync("/api/projects")).StatusCode);
        Assert.All(processes.Calls, c => Assert.Equal("docker", c.FileName));
        Assert.Equal(["version"], processes.Calls.Select(c => c.Arguments[0]));
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("bitbucket")]
    public async Task Integration_clients_never_retry_writes(string name)
    {
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var response = await http.PostAsync("https://example.test/rest/api/3/issue/ORD-1/comment", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, httpAttempts);
    }

    [UnixFact, System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Token_file_is_user_only_on_unix()
    {
        await Authorized().GetAsync("/api/projects");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SymphonistPaths.Resolve("api-token")));
    }

    [Fact]
    public async Task Reload_endpoint_returns_projects_and_errors()
    {
        var before = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal(2, before.GetArrayLength());
        home.WriteProject("shop.yaml", TestHome.ProjectYaml("shop"));
        home.WriteProject("bad.yaml", "id: bad\n");

        var reload = await Json(await Authorized().PostAsync("/api/projects/reload", null));

        Assert.Equal(["billing", "orders", "shop"],
            reload.GetProperty("projects").EnumerateArray().Select(p => p.GetString()).Order(StringComparer.Ordinal));
        var errors = reload.GetProperty("errors");
        Assert.Contains("missing required", errors.GetProperty("bad.yaml").GetString());
        var after = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal(["billing", "orders", "shop"], after.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Reload_endpoint_requires_api_token()
    {
        home.WriteProject("shop.yaml", TestHome.ProjectYaml("shop"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/projects/reload", null)).StatusCode);
        var projects = await Json(await Authorized().GetAsync("/api/projects"));
        Assert.Equal(2, projects.GetArrayLength()); // not reloaded
    }
}
