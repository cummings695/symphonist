using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Orchestration;

namespace Symphonist.Host.Api;

public sealed record ApiError(string Error);

public sealed record TicketDto(string Key, string Project, string Title, string Stage, StageStatus Status,
    string? BlockedReason, int Attempts, DateTimeOffset? WaitingSince, Uri? PrUrl, RiskLevel? Risk = null);

public sealed record TicketEventDto(DateTimeOffset At, string Stage, string Message);

/// <param name="Decision">The developer's (or auto-approval's) decision on the round: "Approved" or "SentBack"; null while undecided.</param>
public sealed record GateReviewDto(string Stage, DateTimeOffset At, ReviewMode Mode, RiskLevel Risk, string Summary,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Checked, IReadOnlyList<string> AreasTouched, string? Decision, string? Error);

public sealed record TicketDetailDto(string Key, string Project, string Title, string Stage, StageStatus Status,
    string? BlockedReason, int Attempts, DateTimeOffset? WaitingSince, Uri? PrUrl, IReadOnlyList<TicketEventDto> Events,
    IReadOnlyList<GateReviewDto> Reviews);

public sealed record SendBackRequest(string? Feedback, string? ToStage);

/// <param name="Problem">Why the project's last tick could not run (e.g. a missing secret); null when it ran.</param>
public sealed record ProjectDto(string Id, string Name, bool Paused, Isolation Isolation, string? Problem,
    ReviewMode Review, IReadOnlyList<StageAgreement> Agreement);

/// <param name="Url">Single-use sign-in link for the dashboard (see <see cref="DashboardLogin"/>).</param>
public sealed record DashboardLinkDto(Uri Url);

/// <summary>The local HTTP API under <c>/api</c> (token-protected by <see cref="ApiTokenMiddleware"/>).</summary>
public static class TicketsApi
{
    public static IEndpointRouteBuilder MapTicketsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/tickets", async (string? project, ProjectRegistry projects, ITicketStore store, CancellationToken ct) =>
        {
            if (project is not null && Find(projects, project) is null) return ProjectNotFound(project);
            var tickets = await store.ListAsync(project, ct);
            return Results.Ok(tickets
                .OrderBy(t => t.ProjectId, StringComparer.Ordinal).ThenBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => ToDto(t, Find(projects, t.ProjectId))));
        });

        api.MapGet("/tickets/{project}/{key}", async (string project, string key, ProjectRegistry projects,
            ITicketStore store, CancellationToken ct) =>
        {
            if (await store.FindAsync(project, key, ct) is not { } t) return TicketNotFound(project, key);
            var events = await store.EventsAsync(t.Id, ct);
            var reviews = await store.GateReviewsAsync(t.Id, ct);
            var d = ToDto(t, Find(projects, project));
            return Results.Ok(new TicketDetailDto(d.Key, d.Project, d.Title, d.Stage, d.Status, d.BlockedReason,
                d.Attempts, d.WaitingSince, d.PrUrl,
                events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new TicketEventDto(e.At, e.StageId, e.Message)).ToList(),
                reviews.OrderByDescending(r => r.At).Select(ToDto).ToList()));
        });

        api.MapPost("/tickets/{project}/{key}/approve", (string project, string key, Orchestrator o, CancellationToken ct) =>
            CommandAsync(() => o.ApproveAsync(project, key, ct)));

        api.MapPost("/tickets/{project}/{key}/send-back", (string project, string key, SendBackRequest? body,
            Orchestrator o, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(body?.Feedback)
                ? Task.FromResult<IResult>(Results.BadRequest(new ApiError("feedback is required")))
                : CommandAsync(() => o.SendBackAsync(project, key, body.Feedback, body.ToStage, ct)));

        api.MapPost("/tickets/{project}/{key}/unblock", (string project, string key, Orchestrator o, CancellationToken ct) =>
            CommandAsync(() => o.UnblockAsync(project, key, ct)));

        api.MapDelete("/tickets/{project}/{key}", async (string project, string key, Orchestrator o, CancellationToken ct) =>
        {
            try
            {
                return await o.ForgetAsync(project, key, ct) switch
                {
                    ForgetResult.Forgotten => Results.NoContent(),
                    ForgetResult.Running => Results.Conflict(new ApiError(
                        $"ticket {key} has an agent run in progress; try again when it finishes")),
                    _ => TicketNotFound(project, key),
                };
            }
            catch (KeyNotFoundException) // unknown project
            {
                return ProjectNotFound(project);
            }
        });

        api.MapPost("/poll", (Orchestrator o) =>
        {
            o.RequestTick();
            return Results.Accepted();
        });

        api.MapGet("/projects", async (ProjectRegistry projects, Orchestrator o, ITicketStore store, CancellationToken ct) =>
        {
            var agreements = (await store.ReviewAgreementAsync(null, ct)).ToLookup(a => a.ProjectId);
            return projects.Projects.OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new ProjectDto(p.Id, p.Name, projects.IsPaused(p.Id), p.Agent.Isolation, o.ProblemOf(p.Id),
                    p.Review.Mode, agreements[p.Id].ToList()));
        });

        api.MapPost("/projects/{id}/pause", (string id, ProjectRegistry projects) => SetPaused(projects, id, true));
        api.MapPost("/projects/{id}/resume", (string id, ProjectRegistry projects) => SetPaused(projects, id, false));

        api.MapPost("/dashboard/login-code", (DashboardLogin login, DashboardAddress address) =>
            new DashboardLinkDto(address.LoginUrl(login.IssueCode())));

        return app;
    }

    private static async Task<IResult> CommandAsync(Func<Task<TransitionOutcome>> command)
    {
        try
        {
            return Results.Ok(await command());
        }
        catch (KeyNotFoundException e) // unknown project, ticket, or send-back stage
        {
            return Results.NotFound(new ApiError(e.Message));
        }
    }

    private static IResult SetPaused(ProjectRegistry projects, string id, bool paused)
    {
        if (Find(projects, id) is null) return ProjectNotFound(id);
        projects.SetPaused(id, paused);
        return Results.NoContent();
    }

    private static TicketDto ToDto(Ticket t, ProjectConfig? p) =>
        new(t.Key, t.ProjectId, t.Title, t.StageId, t.Status, t.BlockedReason, t.Attempts, t.WaitingSince,
            TicketLinks.PullRequest(p, t.PullRequestId), t.Risk);

    private static GateReviewDto ToDto(GateReview r) =>
        new(r.StageId, r.At, r.Mode, r.Verdict.Risk, r.Verdict.Summary, r.Verdict.Reasons, r.Verdict.Checked,
            r.Verdict.AreasTouched, r.Decision?.ToString(), r.Verdict.Error);

    private static ProjectConfig? Find(ProjectRegistry projects, string id) => projects.Projects.FirstOrDefault(p => p.Id == id);

    private static IResult ProjectNotFound(string id) => Results.NotFound(new ApiError($"Project '{id}' not found"));

    private static IResult TicketNotFound(string project, string key) =>
        Results.NotFound(new ApiError($"Ticket '{key}' not found in project '{project}'"));
}
