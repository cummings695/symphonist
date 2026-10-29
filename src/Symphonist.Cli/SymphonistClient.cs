using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Symphonist.Core;

namespace Symphonist.Cli;

public sealed record TicketDto(string Key, string Project, string Title, string Stage, string Status,
    string? BlockedReason, int Attempts, DateTimeOffset? WaitingSince, Uri? PrUrl, string? Risk = null);

public sealed record TicketEventDto(DateTimeOffset At, string Stage, string Message);

public sealed record TicketDetailDto(string Key, string Project, string Title, string Stage, string Status,
    string? BlockedReason, int Attempts, DateTimeOffset? WaitingSince, Uri? PrUrl, IReadOnlyList<TicketEventDto> Events,
    IReadOnlyList<GateReviewDto>? Reviews = null);

/// <param name="Mode">shadow, active or off (as the API names them).</param>
public sealed record GateReviewDto(string Stage, DateTimeOffset At, string Mode, string Risk, string Summary,
    IReadOnlyList<string> Reasons, string? Decision = null);

public sealed record StageAgreementDto(string StageId, int Decided, int Agreed, int FalseLows, int AutoApproved);

public sealed record ProjectDto(string Id, string Name, bool Paused, string Isolation, string? Problem = null,
    string Review = "Off", IReadOnlyList<StageAgreementDto>? Agreement = null);

/// <param name="Errors">Each skipped project file (by file name) and why.</param>
public sealed record ProjectReloadDto(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Errors);

public sealed record OutcomeDto(bool Changed, string Message);

public sealed record DashboardLinkDto(Uri Url);

/// <summary>An agent provider paused by a usage limit.</summary>
/// <param name="NextProbeAt">With no known reset, when one job may next try the provider.</param>
public sealed record ProviderPauseDto(string Key, string Provider, DateTimeOffset Since, DateTimeOffset? ResetsAt,
    DateTimeOffset? NextProbeAt);

/// <summary>A ticket that ships with a release without joining it, or joins it (<paramref name="Finishes"/>).</summary>
public sealed record AlsoShippedDto(string Key, string StageId, string Status, bool Finishes);

/// <summary>What Release now would ship. It is posted back unchanged to confirm it.</summary>
public sealed record ReleasePreviewDto(string ProjectId, IReadOnlyList<string> Ready, IReadOnlyList<AlsoShippedDto> AlsoShipped,
    int OutsideCommits, string? MainHead, string? Unavailable, string? ProductionBranch = null);

public sealed record ReleaseStartDto(bool Started, string Message, string? ReleaseKey);

/// <summary>The host is unreachable (not running, or no API token exists yet).</summary>
public sealed class HostNotRunningException(int port)
    : Exception($"Symphonist host is not running on 127.0.0.1:{port}");

/// <summary>The host answered with an error; <see cref="Exception.Message"/> is the server's message.</summary>
public sealed class ApiException(string message) : Exception(message);

/// <summary>Typed client for the host's local HTTP API.</summary>
public sealed class SymphonistClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient http;
    private readonly string? token;
    private readonly int port;

    /// <param name="token">The API token, or null when none exists (every call then reports the host as not running).</param>
    public SymphonistClient(HttpMessageHandler handler, string? token, int port)
    {
        this.token = token;
        this.port = port;
        http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public Task<List<TicketDto>> ListTicketsAsync(string? project, CancellationToken ct) =>
        SendAsync<List<TicketDto>>(HttpMethod.Get,
            project is null ? "/api/tickets" : $"/api/tickets?project={Uri.EscapeDataString(project)}", null, ct);

    public Task<TicketDetailDto> GetTicketAsync(string project, string key, CancellationToken ct) =>
        SendAsync<TicketDetailDto>(HttpMethod.Get, TicketPath(project, key), null, ct);

    public Task<OutcomeDto> ApproveAsync(string project, string key, CancellationToken ct) =>
        SendAsync<OutcomeDto>(HttpMethod.Post, TicketPath(project, key) + "/approve", null, ct);

    public Task<OutcomeDto> SendBackAsync(string project, string key, string feedback, string? toStage, CancellationToken ct) =>
        SendAsync<OutcomeDto>(HttpMethod.Post, TicketPath(project, key) + "/send-back",
            JsonContent.Create(new { feedback, toStage }, options: Json), ct);

    public Task<OutcomeDto> UnblockAsync(string project, string key, CancellationToken ct) =>
        SendAsync<OutcomeDto>(HttpMethod.Post, TicketPath(project, key) + "/unblock", null, ct);

    public Task ForgetAsync(string project, string key, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Delete, TicketPath(project, key), null, ct);

    public Task PollAsync(CancellationToken ct) => SendAsync<object>(HttpMethod.Post, "/api/poll", null, ct);

    public Task<List<ProjectDto>> ListProjectsAsync(CancellationToken ct) =>
        SendAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", null, ct);

    public Task SetPausedAsync(string id, bool paused, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(id)}/{(paused ? "pause" : "resume")}", null, ct);

    /// <summary>Asks the host to reload the project files (after a hand edit).</summary>
    public Task<ProjectReloadDto> ReloadProjectsAsync(CancellationToken ct) =>
        SendAsync<ProjectReloadDto>(HttpMethod.Post, "/api/projects/reload", null, ct);

    public Task<List<ProviderPauseDto>> ListLimitsAsync(CancellationToken ct) =>
        SendAsync<List<ProviderPauseDto>>(HttpMethod.Get, "/api/limits", null, ct);

    /// <summary>Lets jobs on the provider paused under <paramref name="key"/> start again now.</summary>
    public Task ResumeLimitAsync(string key, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "/api/limits/resume", JsonContent.Create(new { key }, options: Json), ct);

    /// <summary>What Release now would ship for the project.</summary>
    public Task<ReleasePreviewDto> PreviewReleaseAsync(string project, CancellationToken ct) =>
        SendAsync<ReleasePreviewDto>(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(project)}/release", null, ct);

    /// <summary>Starts the release <paramref name="confirmed"/> describes; the host refuses (409) when it changed since.</summary>
    public Task<ReleaseStartDto> ReleaseNowAsync(string project, ReleasePreviewDto confirmed, CancellationToken ct) =>
        SendAsync<ReleaseStartDto>(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(project)}/release",
            JsonContent.Create(confirmed, options: Json), ct);

    /// <summary>A single-use sign-in link for the dashboard, valid for two minutes.</summary>
    public async Task<Uri> CreateDashboardLinkAsync(CancellationToken ct) =>
        (await SendAsync<DashboardLinkDto>(HttpMethod.Post, "/api/dashboard/login-code", null, ct)).Url;

    public void Dispose() => http.Dispose();

    private static string TicketPath(string project, string key) =>
        $"/api/tickets/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(key)}";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        if (token is null) throw new HostNotRunningException(port);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(ApiToken.Header, token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException || e.HttpRequestError == HttpRequestError.ConnectionError)
        {
            throw new HostNotRunningException(port);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new ApiException(await ErrorMessageAsync(response, ct));
            if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0) return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    /// <summary>The server's message: <c>error</c> (our API), <c>detail</c>/<c>title</c> (problem details), else the body text.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "error", "detail", "title" })
                    if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(p.GetString()))
                        return p.GetString()!;
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the raw text.
        }

        return string.IsNullOrWhiteSpace(body) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : body.Trim();
    }
}
