using System.Net;
using System.Net.Sockets;
using Symphonist.Cli;
using Symphonist.Core;
using Symphonist.Core.Integrations;
using Symphonist.Tests.Http;

namespace Symphonist.Cli.Tests;

public sealed class CliTests
{
    private const string Token = "tok-123";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Remove(string name) => Values.Remove(name);
    }

    private sealed class BrokenSecrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) => throw new InvalidOperationException("no credential store on this machine");
        public void Remove(string name) { }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("unexpected failure");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed record Result(int Exit, string Out, string Err);

    private static async Task<Result> RunAsync(HttpMessageHandler handler, string stdin, params string[] args)
        => await RunAsync(handler, new FakeSecrets(), stdin, args);

    private static async Task<Result> RunAsync(HttpMessageHandler handler, FakeSecrets secrets, string stdin, params string[] args)
    {
        using var client = new SymphonistClient(handler, Token, 5199);
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await Cli.RunAsync(args, client, o, e, secrets, new StringReader(stdin), clock: new FixedClock());
        return new Result(exit, o.ToString(), e.ToString());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Ticket(string key, string status, string? waitingSince = null, string? blocked = null) =>
        $$"""{"key":"{{key}}","project":"p","title":"t","stage":"review","status":"{{status}}","blockedReason":{{(blocked is null ? "null" : $"\"{blocked}\"")}},"attempts":1,"waitingSince":{{(waitingSince is null ? "null" : $"\"{waitingSince}\"")}},"prUrl":null}""";

    [Fact]
    public async Task Status_lists_waiting_tickets_first()
    {
        var body = "[" + string.Join(",",
            Ticket("A-1", "Pending"),
            Ticket("B-2", "Blocked", blocked: "no longer matches"),
            Ticket("C-3", "AwaitingApproval", "2026-10-21T09:00:00+00:00"),
            Ticket("A-0", "AwaitingApproval", "2026-10-21T11:48:00+00:00")) + "]";
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, body);

        var r = await RunAsync(handler, "", "status");

        Assert.Equal(0, r.Exit);
        var lines = r.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("KEY", lines[0]);
        Assert.Contains("WAITING", lines[0]);
        Assert.Equal(["A-0", "C-3", "B-2", "A-1"], lines.Skip(1).Select(l => l.Split(' ')[0]));
        Assert.Contains("12m", lines[1]);
        Assert.Contains("3h", lines[2]);
        Assert.Contains("no longer matches", lines[3]);
    }

    [Fact]
    public async Task Status_passes_project_filter()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, "[]");

        await RunAsync(handler, "", "status", "--project", "my proj");

        Assert.Equal("/api/tickets?project=my%20proj", handler.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Approve_sends_token_header_and_prints_outcome()
    {
        var seen = new List<string?>();
        var handler = new HeaderCapturingHandler(seen, "{\"changed\":true,\"sourceStatusToSet\":null,\"message\":\"Approved review\"}");

        var r = await RunAsync(handler, "", "approve", "p", "K-1");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Approved review", r.Out);
        Assert.Equal([Token], seen);
        Assert.Equal("/api/tickets/p/K-1/approve", handler.Path);
    }

    private sealed class HeaderCapturingHandler(List<string?> seen, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            seen.Add(request.Headers.GetValues(ApiToken.Header).Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Send_back_requires_message()
    {
        var handler = new StubHttpHandler();

        var r = await RunAsync(handler, "", "send-back", "p", "K-1");

        Assert.NotEqual(0, r.Exit);
        Assert.Contains("Option '-m' is required", r.Err);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Send_back_posts_feedback_and_stage()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Post, "/api/tickets/p/K-1/send-back", HttpStatusCode.OK,
            "{\"changed\":true,\"message\":\"Sent back to plan\"}");

        var r = await RunAsync(handler, "", "send-back", "p", "K-1", "-m", "needs tests", "--to", "plan");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Sent back to plan", r.Out);
        Assert.Contains("\"feedback\":\"needs tests\"", handler.Requests[0].Body);
        Assert.Contains("\"toStage\":\"plan\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Api_error_exits_1_with_server_message()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Post, "/api/tickets", HttpStatusCode.NotFound,
            "{\"error\":\"Ticket 'K-9' not found in project 'p'\"}");

        var r = await RunAsync(handler, "", "approve", "p", "K-9");

        Assert.Equal(1, r.Exit);
        Assert.Contains("Ticket 'K-9' not found in project 'p'", r.Err);
    }

    [Fact]
    public async Task Problem_details_detail_is_shown()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Post, "/api/poll", HttpStatusCode.InternalServerError,
            "{\"title\":\"An error occurred\",\"detail\":\"database locked\"}");

        var r = await RunAsync(handler, "", "poll");

        Assert.Equal(1, r.Exit);
        Assert.Contains("database locked", r.Err);
    }

    [Fact]
    public async Task Host_down_exits_2_with_message()
    {
        var r = await RunAsync(new ThrowingHandler(), "", "poll");

        Assert.Equal(2, r.Exit);
        Assert.Contains("Symphonist host is not running on 127.0.0.1:5199", r.Err);
    }

    [Fact]
    public async Task Missing_token_exits_2_without_calling_host()
    {
        var handler = new StubHttpHandler();
        using var client = new SymphonistClient(handler, null, 6000);
        var (o, e) = (new StringWriter(), new StringWriter());

        var exit = await Cli.RunAsync(["poll"], client, o, e, new FakeSecrets(), new StringReader(""));

        Assert.Equal(2, exit);
        Assert.Contains("127.0.0.1:6000", e.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Projects_lists_and_pauses()
    {
        var handler = new StubHttpHandler()
            .On(HttpMethod.Get, "/api/projects", HttpStatusCode.OK,
                "[{\"id\":\"web\",\"name\":\"Web\",\"paused\":true,\"isolation\":\"Container\"}]")
            .On(HttpMethod.Post, "/api/projects/web/pause", HttpStatusCode.NoContent, "");

        var list = await RunAsync(handler, "", "projects");
        var pause = await RunAsync(handler, "", "projects", "pause", "web");

        Assert.Contains("paused", list.Out);
        Assert.Equal(0, pause.Exit);
        Assert.Contains("Paused web", pause.Out);
    }

    [Fact]
    public async Task Projects_shows_problem_column()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/projects", HttpStatusCode.OK,
            "[{\"id\":\"api\",\"name\":\"Api\",\"paused\":false,\"isolation\":\"Container\",\"problem\":null}," +
            "{\"id\":\"web\",\"name\":\"Web\",\"paused\":false,\"isolation\":\"Container\",\"problem\":\"secret 'jira-acme' not set\"}]");

        var r = await RunAsync(handler, "", "projects");

        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.EndsWith("PROBLEM", lines[0]);
        Assert.EndsWith("Container", lines[1]);
        Assert.EndsWith("secret 'jira-acme' not set", lines[2]);
    }

    [Fact]
    public async Task Show_prints_ticket_and_events()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets/p/K-1", HttpStatusCode.OK,
            "{\"key\":\"K-1\",\"project\":\"p\",\"title\":\"Fix it\",\"stage\":\"review\",\"status\":\"AwaitingApproval\"," +
            "\"blockedReason\":null,\"attempts\":2,\"waitingSince\":null,\"prUrl\":\"https://x/pr/1\"," +
            "\"events\":[{\"at\":\"2026-10-21T10:00:00+00:00\",\"stage\":\"plan\",\"message\":\"plan done\"}]}");

        var r = await RunAsync(handler, "", "show", "p", "K-1");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Fix it", r.Out);
        Assert.Contains("https://x/pr/1", r.Out);
        Assert.Contains("[plan] plan done", r.Out);
    }

    [Fact]
    public async Task Secret_set_reads_stdin_and_never_prints_value()
    {
        var secrets = new FakeSecrets();

        var r = await RunAsync(new StubHttpHandler(), secrets, "s3cret\r\n", "secret", "set", "jira-token");

        Assert.Equal(0, r.Exit);
        Assert.Equal("s3cret", secrets.Values["jira-token"]);
        Assert.DoesNotContain("s3cret", r.Out + r.Err);
    }

    [Fact]
    public async Task Secret_set_with_empty_value_fails()
    {
        var secrets = new FakeSecrets();

        var r = await RunAsync(new StubHttpHandler(), secrets, "\n", "secret", "set", "jira-token");

        Assert.Equal(1, r.Exit);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task Secret_set_failure_prints_a_message_and_exits_1()
    {
        using var client = new SymphonistClient(new StubHttpHandler(), Token, 5199);
        var (o, e) = (new StringWriter(), new StringWriter());

        var exit = await Cli.RunAsync(["secret", "set", "jira-token"], client, o, e, new BrokenSecrets(),
            new StringReader("s3cret\n"), clock: new FixedClock());

        Assert.Equal(1, exit);
        Assert.Equal("Could not save secret 'jira-token': no credential store on this machine", e.ToString().Trim());
        Assert.DoesNotContain("s3cret", o.ToString() + e);
    }

    [Fact]
    public async Task Unexpected_error_prints_a_message_and_exits_1()
    {
        var r = await RunAsync(new FailingHandler(), "", "status");

        Assert.Equal(1, r.Exit);
        Assert.Equal("Error: unexpected failure", r.Err.Trim());
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(719, "11h")]
    [InlineData(60 * 24 * 2 + 5, "2d")]
    public void Age_is_humanized(int minutes, string expected) =>
        Assert.Equal(expected, StatusTable.Age(Now - TimeSpan.FromMinutes(minutes), Now));
}
