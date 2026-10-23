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
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class DashboardAuthTests : IDisposable
{
    private const string Origin = "http://127.0.0.1:5199";
    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) => throw new NotSupportedException();
        public void Remove(string name) => throw new NotSupportedException();
    }

    private readonly TestHome home = new();
    private readonly FakeTicketStore store = new();
    private readonly List<IDisposable> disposables = [];
    private readonly WebApplicationFactory<Program> factory;

    public DashboardAuthTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        factory = CreateFactory();
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(disposables)) d.Dispose();
        home.Dispose();
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var integrations = new FakeIntegrationFactory(
            new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ITicketStore>(store);
            s.AddSingleton<IIntegrationFactory>(integrations);
            s.AddSingleton<IGitWorkspace>(new FakeGitWorkspace());
            s.AddSingleton<IAgentRunner>(new FakeAgentRunner());
            s.AddSingleton<IProcessRunner>(new FakeProcessRunner());
            s.AddSingleton<ISecretStore>(new NoSecrets());
            s.AddSingleton(new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false));
            s.Remove(s.Single(d => d.ImplementationType == typeof(OrchestratorService)));
        }));
        disposables.Add(f);
        return f;
    }

    private HttpClient Client(WebApplicationFactory<Program>? f = null, bool handleCookies = true)
    {
        var c = (f ?? factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin), AllowAutoRedirect = false, HandleCookies = handleCookies,
        });
        disposables.Add(c);
        return c;
    }

    private static string Token => File.ReadAllText(SymphonistPaths.Resolve("api-token")).Trim();

    private static async Task<Uri> LoginUrlAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/login-code");
        request.Headers.Add(ApiToken.Header, Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Uri(body.GetProperty("url").GetString()!);
    }

    /// <summary>Signs <paramref name="client"/> in the way the CLI does: a login code from the API, then the link.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var response = await client.GetAsync(await LoginUrlAsync(client));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response;
    }

    private static HttpRequestMessage NegotiateRequest(string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Negotiate);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Login_code_url_points_at_the_loopback_listener()
    {
        var url = await LoginUrlAsync(Client());
        Assert.Matches("^http://127\\.0\\.0\\.1:5199/login\\?code=[A-Za-z0-9_-]{43}$", url.ToString());
    }

    [Fact]
    public async Task Board_requires_sign_in()
    {
        var response = await Client().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/signed-out", response.Headers.Location!.OriginalString.Replace(Origin, ""));
    }

    [Fact]
    public async Task Missing_ticket_page_is_404_with_a_not_found_page()
    {
        var client = Client();
        await SignInAsync(client);

        var response = await client.GetAsync("/tickets/orders/NOPE-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_page_explains_how_to_sign_in()
    {
        var response = await Client().GetAsync("/signed-out");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("symphonist dashboard", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_code_requires_api_token() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsync("/api/dashboard/login-code", null)).StatusCode);

    [Fact]
    public async Task Sign_in_sets_strict_http_only_cookie_and_redirects()
    {
        var response = await SignInAsync(Client());
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(DashboardSetup.CookieName + "="))
            .ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/;", cookie + ";");
        Assert.Contains("expires=", cookie); // persistent
    }

    [Fact]
    public async Task Signed_in_board_renders()
    {
        var at = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        await store.SaveAsync(new Ticket
        {
            Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-42", Title = "Seeded", StageId = "plan",
            Status = StageStatus.AwaitingApproval, CreatedAt = at, UpdatedAt = at, WaitingSince = at,
        }, default);
        var client = Client();
        await SignInAsync(client);
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Symphonist</h1>", html);
        Assert.Contains("ORD-42", html);
    }

    [Fact]
    public async Task Reused_code_is_rejected()
    {
        var client = Client();
        var url = await LoginUrlAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(url)).StatusCode);

        var again = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("text/html", again.Content.Headers.ContentType?.MediaType);
        Assert.Contains("symphonist dashboard", await again.Content.ReadAsStringAsync());
        Assert.DoesNotContain(again.Headers.TryGetValues("Set-Cookie", out var c) ? c : [], v => v.StartsWith(DashboardSetup.CookieName + "="));
    }

    [Fact]
    public async Task Cross_origin_hub_request_is_rejected()
    {
        var client = Client();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(NegotiateRequest("https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(NegotiateRequest("http://127.0.0.1:5200"))).StatusCode);
    }

    [Fact]
    public async Task Hub_request_without_origin_is_rejected()
    {
        var client = Client();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(NegotiateRequest(null))).StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5199")]
    [InlineData("http://localhost:5199")]
    public async Task Same_origin_hub_request_is_allowed(string origin)
    {
        var client = Client();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(NegotiateRequest(origin))).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_hub_request_is_rejected()
    {
        var status = (await Client().SendAsync(NegotiateRequest(Origin))).StatusCode;
        Assert.Contains(status, new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized });
    }

    [Fact]
    public async Task Hub_initializers_load_without_origin_only_when_signed_in()
    {
        // blazor.web.js fetches this same-origin, and browsers send no Origin on same-origin GETs.
        Assert.NotEqual(HttpStatusCode.OK, (await Client().GetAsync("/_blazor/initializers")).StatusCode);

        var client = Client();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_blazor/initializers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/_blazor/initializers", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/_blazor?id=abc")).StatusCode); // long polling
    }

    [Fact]
    public async Task Cross_origin_page_request_is_rejected()
    {
        var client = Client();
        await SignInAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    private static void AssertNotFramable(HttpResponseMessage response)
    {
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Dashboard_responses_forbid_framing()
    {
        var client = Client();
        AssertNotFramable(await client.GetAsync("/signed-out"));
        AssertNotFramable(await client.GetAsync("/login?code=nope"));
        AssertNotFramable(await client.SendAsync(NegotiateRequest("https://evil.example"))); // rejected by the origin check

        AssertNotFramable(await SignInAsync(client));
        var board = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        AssertNotFramable(board);
        var css = await client.GetAsync("/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        AssertNotFramable(css);
    }

    [Fact]
    public async Task Cookie_does_not_authorize_api()
    {
        var client = Client();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public async Task Cookie_survives_host_restart()
    {
        var signIn = await SignInAsync(Client());
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DashboardSetup.CookieName + "=")).Split(';')[0];
        factory.Dispose();
        Assert.NotEmpty(Directory.GetFiles(SymphonistPaths.Resolve("keys"), "*.xml")); // keys persisted under SYMPHONIST_HOME

        var restarted = Client(CreateFactory(), handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await restarted.SendAsync(request)).StatusCode);
    }
}
