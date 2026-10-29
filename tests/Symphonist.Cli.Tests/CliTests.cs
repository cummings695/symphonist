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
    public async Task Send_back_help_describes_default()
    {
        var r = await RunAsync(new FailingHandler(), "", "send-back", "--help");

        Assert.Equal(0, r.Exit);
        Assert.Contains("default: rerun the current stage", r.Out);
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
    public async Task Forget_prints_confirmation()
    {
        var handler = new RequestRecordingHandler("");

        var r = await RunAsync(handler, "", "forget", "orders", "ORD-1");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Forgot orders/ORD-1", r.Out);
        Assert.Contains("picked up again", r.Out);
        Assert.Equal([(HttpMethod.Delete, "/api/tickets/orders/ORD-1", Token)], handler.Requests);
    }

    [Fact]
    public async Task Forget_running_shows_api_error()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Delete, "/api/tickets/orders/ORD-1", HttpStatusCode.Conflict,
            "{\"error\":\"ticket ORD-1 has an agent run in progress; try again when it finishes\"}");

        var r = await RunAsync(handler, "", "forget", "orders", "ORD-1");

        Assert.Equal(1, r.Exit);
        Assert.Contains("has an agent run in progress", r.Err);
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

    private sealed class RequestRecordingHandler(string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues(ApiToken.Header, out var v) ? v.Single() : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private const string LinkBody = """{"url":"http://127.0.0.1:5199/login?code=abc"}""";

    private static async Task<Result> RunDashboardAsync(HttpMessageHandler handler, Func<Uri, bool> openBrowser, params string[] args)
    {
        using var client = new SymphonistClient(handler, Token, 5199);
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await Cli.RunAsync(args, client, o, e, new FakeSecrets(), new StringReader(""), openBrowser: openBrowser);
        return new Result(exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Dashboard_opens_link_from_host()
    {
        var handler = new RequestRecordingHandler(LinkBody);
        var opened = new List<Uri>();

        var r = await RunDashboardAsync(handler, u => { opened.Add(u); return true; }, "dashboard");

        Assert.Equal(0, r.Exit);
        Assert.Equal(new Uri("http://127.0.0.1:5199/login?code=abc"), Assert.Single(opened));
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/api/dashboard/login-code", Token), request);
        Assert.Contains("Opened the dashboard (link valid for 2 minutes)", r.Out);
        Assert.DoesNotContain("code=abc", r.Out);
    }

    [Fact]
    public async Task Dashboard_no_browser_prints_link()
    {
        var called = false;

        var r = await RunDashboardAsync(new RequestRecordingHandler(LinkBody), _ => called = true, "dashboard", "--no-browser");

        Assert.Equal(0, r.Exit);
        Assert.Equal("http://127.0.0.1:5199/login?code=abc" + Environment.NewLine, r.Out);
        Assert.False(called);
    }

    [Fact]
    public async Task Dashboard_prints_link_when_browser_fails()
    {
        var r = await RunDashboardAsync(new RequestRecordingHandler(LinkBody), _ => false, "dashboard");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Could not open a browser. Open this link within 2 minutes: http://127.0.0.1:5199/login?code=abc", r.Out);
    }

    [Fact]
    public void Browser_launcher_opens_only_http_links()
    {
        Assert.False(BrowserLauncher.TryOpen(new Uri("file:///etc/passwd")));
        Assert.False(BrowserLauncher.TryOpen(new Uri("javascript:alert(1)")));
        Assert.False(BrowserLauncher.TryOpen(new Uri("/login?code=abc", UriKind.Relative)));
    }

    [Fact]
    public async Task Dashboard_host_down_exits_2()
    {
        var r = await RunDashboardAsync(new ThrowingHandler(), _ => true, "dashboard");

        Assert.Equal(2, r.Exit);
        Assert.Contains("Symphonist host is not running", r.Err);
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
    public async Task Status_shows_risk_column()
    {
        var withRisk = Ticket("A-1", "AwaitingApproval", "2026-10-21T11:48:00+00:00").Replace("\"prUrl\":null", "\"prUrl\":null,\"risk\":\"High\"");
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, "[" + withRisk + "," + Ticket("B-2", "Pending") + "]");

        var r = await RunAsync(handler, "", "status");

        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Matches(@"STATUS\s+RISK\s+WAITING", lines[0]);
        Assert.Matches(@"AwaitingApproval\s+high\s+12m", lines[1]);
        Assert.DoesNotContain("high", lines[2]);
        Assert.DoesNotContain("low", lines[2]);
    }

    [Fact]
    public async Task Show_prints_latest_review()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets/p/K-1", HttpStatusCode.OK,
            "{\"key\":\"K-1\",\"project\":\"p\",\"title\":\"Fix it\",\"stage\":\"implement\",\"status\":\"AwaitingApproval\"," +
            "\"blockedReason\":null,\"attempts\":1,\"waitingSince\":null,\"prUrl\":null,\"events\":[]," +
            "\"reviews\":[{\"stage\":\"implement\",\"at\":\"2026-10-21T10:00:00+00:00\",\"mode\":\"Shadow\",\"risk\":\"High\"," +
            "\"summary\":\"touches pricing\",\"reasons\":[\"touches risk area 'pricing'\"],\"checked\":[],\"areasTouched\":[],\"decision\":null,\"error\":null}," +
            "{\"stage\":\"plan\",\"at\":\"2026-10-20T10:00:00+00:00\",\"mode\":\"Shadow\",\"risk\":\"Low\",\"summary\":\"older review\",\"reasons\":[]}]}");

        var r = await RunAsync(handler, "", "show", "p", "K-1");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Review:   implement (shadow) high risk: touches pricing", r.Out);
        Assert.Contains("  - touches risk area 'pricing'", r.Out);
        Assert.DoesNotContain("older review", r.Out);
    }

    [Fact]
    public async Task Show_cleans_and_caps_reviewer_text()
    {
        var summary = "rm \\u001b[2J\\nfake line" + new string('s', 600);
        var reasons = string.Join(",", Enumerable.Range(1, 12).Select(i => $"\"reason {i}\\u202E\\r\\nnext\""));
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/tickets/p/K-1", HttpStatusCode.OK,
            "{\"key\":\"K-1\",\"project\":\"p\",\"title\":\"Fix\\u0007 it\",\"stage\":\"implement\",\"status\":\"AwaitingApproval\"," +
            "\"blockedReason\":null,\"attempts\":1,\"waitingSince\":null,\"prUrl\":null," +
            "\"events\":[{\"at\":\"2026-10-21T10:00:00+00:00\",\"stage\":\"implement\",\"message\":\"reviewer: low \\u001b[31m\\nforged\"}]," +
            "\"reviews\":[{\"stage\":\"implement\",\"at\":\"2026-10-21T10:00:00+00:00\",\"mode\":\"Shadow\",\"risk\":\"High\"," +
            $"\"summary\":\"{summary}\",\"reasons\":[{reasons}]}}]}}");

        var r = await RunAsync(handler, "", "show", "p", "K-1");

        Assert.Equal(0, r.Exit);
        Assert.DoesNotContain(r.Out, c => char.IsControl(c) && c is not '\r' and not '\n');
        Assert.DoesNotContain('‮', r.Out);
        var lines = r.Out.Split(Environment.NewLine);
        Assert.Equal("K-1  Fix it", lines[0]);
        var review = Assert.Single(lines, l => l.StartsWith("Review:", StringComparison.Ordinal));
        Assert.EndsWith("high risk: rm [2J fake line" + new string('s', 500 - "rm [2J fake line".Length - 1) + "…", review);
        var items = lines.Where(l => l.StartsWith("  - ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["  - reason 1  next", "  - reason 10  next", "  - (+2 more)"], [items[0], items[9], items[^1]]);
        Assert.Contains(lines, l => l.EndsWith("[implement] reviewer: low [31m forged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projects_prints_agreement()
    {
        var handler = new StubHttpHandler().On(HttpMethod.Get, "/api/projects", HttpStatusCode.OK,
            "[{\"id\":\"api\",\"name\":\"Api\",\"paused\":false,\"isolation\":\"Container\",\"problem\":null,\"review\":\"Shadow\"," +
            "\"agreement\":[{\"projectId\":\"api\",\"stageId\":\"implement\",\"decided\":10,\"agreed\":9,\"falseLows\":1,\"autoApproved\":0,\"rate\":0.9}," +
            "{\"projectId\":\"api\",\"stageId\":\"plan\",\"decided\":0,\"agreed\":0,\"falseLows\":0,\"autoApproved\":0,\"rate\":null}]}," +
            "{\"id\":\"web\",\"name\":\"Web\",\"paused\":false,\"isolation\":\"Container\",\"review\":\"Active\"," +
            "\"agreement\":[{\"projectId\":\"web\",\"stageId\":\"release\",\"decided\":2,\"agreed\":2,\"falseLows\":0,\"autoApproved\":3,\"rate\":1}]}]");

        var r = await RunAsync(handler, "", "projects");

        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Matches(@"STATE\s+REVIEW\s+ISOLATION", lines[0]);
        Assert.Matches(@"active\s+shadow\s+Container", lines[1]);
        Assert.Equal("  implement: agrees 9/10, 1 low-risk sent back", lines[2]);
        Assert.Matches(@"^web\s", lines[3]);
        Assert.Equal("  release: agrees 2/2, 3 auto-approved", lines[4]);
        Assert.Equal(5, lines.Count);
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
    public async Task Secret_set_trims_whitespace()
    {
        var secrets = new FakeSecrets();

        var r = await RunAsync(new StubHttpHandler(), secrets, "  tok-123 \t\r\n", "secret", "set", "jira-token");

        Assert.Equal(0, r.Exit);
        Assert.Equal("tok-123", secrets.Values["jira-token"]);
    }

    [Fact]
    public async Task Secret_set_rejects_whitespace_only()
    {
        var secrets = new FakeSecrets();

        var r = await RunAsync(new StubHttpHandler(), secrets, "   ", "secret", "set", "jira-token");

        Assert.Equal(1, r.Exit);
        Assert.Contains("Secret value must not be empty", r.Err);
        Assert.Empty(secrets.Values);
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

    [Fact]
    public async Task Projects_reload_prints_count_and_errors()
    {
        var ok = await RunAsync(new StubHttpHandler().On(HttpMethod.Post, "/api/projects/reload", HttpStatusCode.OK,
            """{"projects":["orders","web"],"errors":{}}"""), "", "projects", "reload");
        var failed = await RunAsync(new StubHttpHandler().On(HttpMethod.Post, "/api/projects/reload", HttpStatusCode.OK,
            """{"projects":["orders"],"errors":{"bad.yaml":"missing required 'tracker'","a.yaml":"project id 'x' is declared by 2 files"}}"""),
            "", "projects", "reload");

        Assert.Equal((0, "Reloaded 2 project(s)" + Environment.NewLine, ""), (ok.Exit, ok.Out, ok.Err));
        Assert.Equal(1, failed.Exit);
        Assert.Equal("Reloaded 1 project(s)" + Environment.NewLine, failed.Out);
        Assert.Equal("skipped a.yaml: project id 'x' is declared by 2 files" + Environment.NewLine
            + "skipped bad.yaml: missing required 'tracker'" + Environment.NewLine, failed.Err);
    }

    private static string Local(string at) => DateTimeOffset.Parse(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [Fact]
    public async Task Cli_limits_prints_pauses()
    {
        var handler = new StubHttpHandler()
            .On(HttpMethod.Get, "/api/limits", HttpStatusCode.OK,
                """[{"key":"claude-code","provider":"Claude","since":"2026-10-21T11:00:00+00:00","resetsAt":"2026-10-21T13:00:00+00:00","nextProbeAt":null},"""
                + """{"key":"codex:chatgpt","provider":"Codex","since":"2026-10-21T11:30:00+00:00","resetsAt":null,"nextProbeAt":"2026-10-21T12:15:00+00:00"}]""")
            .On(HttpMethod.Post, "/api/limits/resume", HttpStatusCode.NoContent, "");

        var list = await RunAsync(handler, "", "limits");
        var resume = await RunAsync(handler, "", "limits", "resume", "codex:chatgpt");

        Assert.Equal(0, list.Exit);
        var lines = list.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(@"^PROVIDER\s+KEY\s+SINCE\s+RESETS / NEXT TRY$", lines[0]);
        Assert.Matches($@"^Claude\s+claude-code\s+{Local("2026-10-21T11:00:00Z")}\s+resets {Local("2026-10-21T13:00:00Z")}$", lines[1]);
        Assert.Matches($@"^Codex\s+codex:chatgpt\s+{Local("2026-10-21T11:30:00Z")}\s+next try {Local("2026-10-21T12:15:00Z")}$", lines[2]);
        Assert.Equal((0, "Resumed codex:chatgpt" + Environment.NewLine), (resume.Exit, resume.Out));
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/api/limits/resume"
            && r.Body!.Contains("\"key\":\"codex:chatgpt\""));
    }

    [Fact]
    public async Task Cli_limits_says_when_nothing_is_paused()
    {
        var r = await RunAsync(new StubHttpHandler().On(HttpMethod.Get, "/api/limits", HttpStatusCode.OK, "[]"), "", "limits");

        Assert.Equal((0, "No provider is paused by a usage limit" + Environment.NewLine), (r.Exit, r.Out));
    }

    [Fact]
    public async Task Cli_limits_resume_reports_a_provider_not_paused()
    {
        var r = await RunAsync(new StubHttpHandler().On(HttpMethod.Post, "/api/limits/resume", HttpStatusCode.NotFound,
            """{"error":"'claude-code' is not paused"}"""), "", "limits", "resume", "claude-code");

        Assert.Equal((1, "'claude-code' is not paused" + Environment.NewLine), (r.Exit, r.Err));
    }
}
