using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
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
            s.AddSingleton<IAgentRunner>(new FakeAgentRunner());
            s.AddSingleton<IProcessRunner>(processes);
            s.AddSingleton<ISecretStore>(new NoSecrets());
            foreach (var name in new[] { IntegrationFactory.JiraClient, IntegrationFactory.BitbucketClient })
                s.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler(this));
            // Drive the orchestrator only through the API (no background ticks).
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
}
