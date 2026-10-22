# Symphonist Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A local, live-updating pipeline board in the browser (layout A: columns are stages, cards are tickets, cards waiting on the developer highlighted) with a ticket detail page that approves, sends back, and unblocks — opened with `symphonist dashboard`.

**Architecture:** The dashboard is Blazor Server (interactive server render mode over SignalR) hosted in the existing `Symphonist.Host` process. Components never call the HTTP API; they read through a `BoardQuery` service over `ProjectRegistry` / `ITicketStore` / `Orchestrator` and act through the same `Orchestrator` commands the API uses. A decorating `NotifyingTicketStore` plus a throttled `BoardNotifier` push "something changed" to open circuits. The browser signs in with a single-use code that the CLI obtains from the token-protected API, exchanged for an HttpOnly SameSite=Strict cookie; a middleware rejects cross-origin requests to the dashboard.

**Tech Stack:** .NET 10, ASP.NET Core Razor components (Blazor Server, interactive server render mode), cookie authentication + ASP.NET Core Data Protection, plain CSS (no CSS framework, no CDN), bUnit (component tests), xUnit, FakeTimeProvider, WebApplicationFactory.

**Spec:** `docs/superpowers/specs/2026-10-21-symphonist-design.md` (sections 4 "Dashboard", 9 "User interfaces", 10 "Technical choices"). Builds on plan 1, `docs/superpowers/plans/2026-10-21-symphonist-walking-skeleton.md`.

## Scope of this plan

This is plan 2. In scope: the board, filter by project, source badge, waiting-on-you highlight, running time and cost on cards, blocked count, card detail (run history, agent summaries, events, links) with approve / send back / unblock, per-project pause/resume and problem banners, host-mode marker, "poll now", live updates, sign-in, and the `symphonist dashboard` command.

Out of scope (spec items that need data later plans create):

- Risk badge, gate reasoning, shadow-mode agreement numbers → plan 3.
- Project setup screen (connections, state mapping, risk areas, templates, guardrails) → plan 4.
- Digest view, touch stats, "Paused: usage limit" status, notifications → plan 7.
- Source badges other than Jira → plan 8 (the badge reads `tracker.type`, so new trackers show up without dashboard changes).
- Tray icon "open dashboard" → plan 9 (it will call the same login-code endpoint).

Plan-level decisions (builder's calls, consistent with the spec):

- **Sign-in by single-use code.** `POST /api/dashboard/login-code` (protected by the existing `X-Symphonist-Token`) returns a URL `http://127.0.0.1:{port}/login?code=…`; the code lives 2 minutes and works once. `/login` exchanges it for a persistent cookie. The API token itself never appears in a URL or browser history.
- **Data Protection keys persist to `$SYMPHONIST_HOME/keys`** (DPAPI-protected on Windows, directory `0700` on Unix) so a host restart doesn't sign the developer out.
- **All-projects board groups columns by stage kind** (Triage, Plan, Implement, Build, Uat, Release, Deploy, Done — only kinds some visible project uses). A single-project board shows that project's own stage ids in pipeline order.
- **Done column shows the 20 most recently finished tickets**, with a "+N older" count.
- **Send back from the dashboard offers the current stage (default) and earlier stages.** `PipelineEngine.SendBack` with no target reruns the current stage; the dashboard makes that explicit.
- **Approve is available on the board card itself** (one click for the common case); send back and unblock live on the detail page, where the reason/feedback is visible.
- **`/api/poll` moves to the orchestrator's coalesced trigger** (`Orchestrator.RequestTick`), shared with the dashboard's "Poll now" — this also closes the plan-1 deferred item "uncoalesced `Task.Run` per request".

## Global Constraints

- .NET 10 (`net10.0`) for every project.
- Windows, macOS, and Linux supported in v1. No Windows-only APIs in the core; platform-specific pieces sit behind small abstractions. Paths, shells, line endings, and process launching must work on all three. CI builds and tests on all three.
- Launch every process with `ProcessStartInfo.ArgumentList` (never a shell string); build paths with `Path.Combine`.
- Local API bound to localhost only. The dashboard is served by the same Kestrel listener (`127.0.0.1:{SYMPHONIST_PORT|5199}`); no new listener.
- Dashboard: Blazor Server with live updates over SignalR (keeps the stack all C#).
- Approvals work both by moving the card in the source system and through Symphonist (CLI/API/dashboard); the dashboard calls the same `Orchestrator.ApproveAsync` / `SendBackAsync` / `UnblockAsync`.
- Every dashboard page and the `/_blazor` hub require the sign-in cookie; only `/login`, `/signed-out`, and static assets are anonymous. `/api/*` keeps requiring `X-Symphonist-Token` (cookies are never accepted there).
- No external network requests from the page: all CSS/JS/fonts are served by the host (works offline; client data never leaks to a CDN).
- Ticket titles, tracker comments, feedback, agent summaries, errors, and event messages are rendered as text only — never through `MarkupString`.
- Status is never conveyed by color alone: every highlighted card also carries a text label ("Waiting on you", "Blocked", "Running").
- Data directory: `$SYMPHONIST_HOME` if set, else `~/.symphonist`.

## Review Focus

1. **Dashboard open while the host restarts.** Expect: Blazor's reconnect UI shows "Symphonist host is not responding — reconnecting…", the page reloads once the host is back, and the developer is still signed in (keys persisted). → test `Cookie_survives_host_restart` in Task 3.
2. **Another website in the same browser tries to drive the dashboard** (cross-origin `/_blazor/negotiate`, WebSocket, or form post). Expect: `403`, nothing executed, regardless of cookies. → test `Cross_origin_hub_request_is_rejected` in Task 3.
3. **Burst of changes** (an agent run writes many events in a second) **or a store failure during refresh.** Expect: at most one board refresh per 250 ms window; a refresh that throws shows an error banner and keeps the last board instead of killing the circuit. → tests `Burst_raises_changed_once` (Task 2) and `Refresh_failure_shows_banner_and_keeps_board` (Task 5).
4. **Stale board: the developer clicks Approve on a ticket that already moved** (approved from Jira or the CLI a moment ago, or approve clicked twice). Expect: the outcome message ("implement is not awaiting approval") shown in the status line, no exception, board refreshed. → test `Approve_on_stale_card_shows_outcome_message` in Task 5.
5. **Ticket whose project or stage no longer exists in config** (project file deleted or a stage renamed). Expect: the board still renders; the card appears in an "Unknown stage" column with its stored stage id, links that need config are omitted, and its detail page opens read-only. → tests `Ticket_with_unknown_stage_goes_to_unknown_column` and `Ticket_of_removed_project_is_still_shown` (Task 4), `Unknown_project_detail_is_read_only` (Task 6).

---

## File Structure

```
src/Symphonist.Core/Storage/ITicketStore.cs            (modify) AgentRunRecord.Summary, AgentRunsAsync, AgentCostsAsync
src/Symphonist.Integrations/Storage/
  SymphonistDbContext.cs                               (modify) AgentRunEntity.Summary
  SqliteTicketStore.cs                                 (modify) new queries
  Migrations/<stamp>_AgentRunSummary.cs                (create via dotnet-ef)
src/Symphonist.Host/
  Program.cs                                           (modify) store decorator, notifier wiring, AddDashboard/MapDashboard
  TicketLinks.cs                                       (create) issue / PR URLs shared by API and dashboard
  Api/TicketsApi.cs                                    (modify) use TicketLinks, RequestTick, login-code endpoint
  Orchestration/Orchestrator.cs                        (modify) ProblemsChanged event, RequestTick()
  Orchestration/ProjectRegistry.cs                     (modify) PausedChanged event
  Orchestration/StageExecutor.cs                       (modify) record agent summary
  Dashboard/
    BoardNotifier.cs                                   throttled change signal
    NotifyingTicketStore.cs                            ITicketStore decorator
    DashboardLogin.cs                                  single-use login codes
    DashboardOriginMiddleware.cs                       same-origin enforcement
    DashboardSetup.cs                                  AddDashboard / MapDashboard (auth, data protection, endpoints)
    BoardQuery.cs                                      view models for board and ticket detail
    BoardModels.cs                                     records returned by BoardQuery
  Components/
    App.razor, Routes.razor, _Imports.razor
    Layout/MainLayout.razor (+ .razor.css), Layout/ReconnectModal.razor
    Pages/Board.razor (+ .razor.css)
    Pages/TicketDetail.razor (+ .razor.css)
    Shared/TicketCard.razor (+ .razor.css), Shared/ProjectBar.razor, Shared/StatusLine.razor
  wwwroot/app.css                                      tokens, light/dark, base styles
src/Symphonist.Cli/
  Cli.cs                                               (modify) `dashboard` command
  SymphonistClient.cs                                  (modify) CreateDashboardLinkAsync
  BrowserLauncher.cs                                   (create) open a URL on Windows/macOS/Linux
tests/Symphonist.Host.Tests/
  Dashboard/BoardNotifierTests.cs, NotifyingTicketStoreTests.cs, DashboardLoginTests.cs,
  Dashboard/DashboardAuthTests.cs, BoardQueryTests.cs, BoardComponentTests.cs, TicketDetailComponentTests.cs
  Fakes/FakeTicketStore.cs                             (modify) new store members
tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs (modify)
tests/Symphonist.Cli.Tests/CliTests.cs                 (modify)
README.md                                              (modify) "Dashboard" section
```

---

### Task 1: Agent-run summaries and run queries in the store

**Files:**
- Modify: `src/Symphonist.Core/Storage/ITicketStore.cs`, `src/Symphonist.Integrations/Storage/{SymphonistDbContext,SqliteTicketStore}.cs`, `src/Symphonist.Host/Orchestration/StageExecutor.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeTicketStore.cs`
- Create: migration `AgentRunSummary` (`dotnet tool run dotnet-ef migrations add AgentRunSummary --project src/Symphonist.Integrations`)
- Test: `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`, `tests/Symphonist.Host.Tests/Orchestration/StageExecutorTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record AgentRunRecord(Guid TicketId, string StageId, DateTimeOffset StartedAt, TimeSpan Duration,
    bool Succeeded, decimal? CostUsd, string? Error, string RawLogPath, string? Summary = null);

// ITicketStore additions
Task<IReadOnlyList<AgentRunRecord>> AgentRunsAsync(Guid ticketId, CancellationToken ct);      // oldest first (StartedAt, then Id)
Task<IReadOnlyDictionary<Guid, decimal>> AgentCostsAsync(CancellationToken ct);              // sum of non-null CostUsd per ticket; tickets with no cost absent
```

`AgentRunEntity.Summary` is `string?`, nullable column. Sum decimals client-side (SQLite stores `decimal` as TEXT). `StageExecutor.RunAgentAsync` passes `result.Output?.Summary`.

- [ ] **Step 1: Write failing tests**

```csharp
// SqliteTicketStoreTests
[Fact] public async Task Agent_runs_round_trip_with_summary_oldest_first()
{ /* add run B (Now+1m, Summary "second") then A (Now, Summary "first"); AgentRunsAsync -> ["first","second"], all fields equal */ }
[Fact] public async Task Agent_costs_sum_per_ticket_and_skip_null()
{ /* t1: 0.12m + 0.30m + null; t2: null only -> { t1: 0.42m }, t2 absent */ }
[Fact] public async Task Migrates_database_created_before_summary_column()
{ /* apply migrations up to "PendingSourceStatus" via db.GetService<IMigrator>().MigrateAsync("…PendingSourceStatus"), insert a run row,
     then use SqliteTicketStore -> AgentRunsAsync returns it with Summary null */ }
// StageExecutorTests
[Fact] public async Task Records_agent_summary() { /* FakeAgentRunner returns Summary "Wrote the plan" -> store.AgentRuns[0].Summary == "Wrote the plan" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter SqliteTicketStoreTests` and `dotnet test tests/Symphonist.Host.Tests --filter StageExecutorTests` — Expected: FAIL (members don't exist).
- [ ] **Step 3: Implement** the record change, store methods, entity column, migration, `FakeTicketStore` (list-backed, same ordering/summing), and the `StageExecutor` change.
- [ ] **Step 4: Run** both suites — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(storage): record agent summaries and query runs and costs"`

---

### Task 2: Change notifications

**Files:**
- Create: `src/Symphonist.Host/Dashboard/{BoardNotifier,NotifyingTicketStore}.cs`
- Modify: `src/Symphonist.Host/Orchestration/{Orchestrator,ProjectRegistry}.cs`, `src/Symphonist.Host/Api/TicketsApi.cs`, `src/Symphonist.Host/Program.cs`
- Test: `tests/Symphonist.Host.Tests/Dashboard/{BoardNotifierTests,NotifyingTicketStoreTests}.cs`, `tests/Symphonist.Host.Tests/Orchestration/{OrchestratorTests,ProjectRegistryTests}.cs`, `tests/Symphonist.Host.Tests/Api/TicketsApiTests.cs`

**Interfaces:**
- Consumes: `ITicketStore` (Task 1).
- Produces:

```csharp
/// Raises Changed at most once per Window, on the trailing edge of a burst of Notify calls.
public sealed class BoardNotifier(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);
    public void Notify();
    public event Action? Changed;   // each subscriber invoked separately; one that throws is ignored and the rest still run
}

/// Forwards to inner; calls notifier.Notify() after each successful write
/// (SaveAsync, AddEventAsync, AddAgentRunAsync, and ResetInterruptedRunsAsync when it returns > 0). Reads pass through.
public sealed class NotifyingTicketStore(ITicketStore inner, BoardNotifier notifier) : ITicketStore

// Orchestrator additions
public event Action? ProblemsChanged;   // raised when a project's problem is set to a different value or cleared
public void RequestTick();              // TickTrigger.Request()

// ProjectRegistry addition
public event Action? PausedChanged;     // raised by SetPaused only when the paused set actually changed
```

Program wiring: register `BoardNotifier` singleton; `SqliteTicketStore` registered as itself and `ITicketStore` as `new NotifyingTicketStore(sqlite, notifier)`; after `Build()`, subscribe `Orchestrator.ProblemsChanged` and `ProjectRegistry.PausedChanged` to `notifier.Notify`. `/api/poll` calls `o.RequestTick()` and returns `202`.

- [ ] **Step 1: Write failing tests**

```csharp
// BoardNotifierTests (FakeTimeProvider)
[Fact] public void Burst_raises_changed_once() { /* 10x Notify; Advance(249ms) -> 0 raised; Advance(1ms) -> 1 raised */ }
[Fact] public void Notify_after_window_raises_again() { /* Notify, Advance(250ms), Notify, Advance(250ms) -> 2 raised */ }
[Fact] public void Throwing_subscriber_does_not_stop_others() { /* sub1 throws, sub2 counts -> sub2 == 1 */ }
// NotifyingTicketStoreTests
[Fact] public async Task Writes_notify_reads_do_not() { /* SaveAsync, AddEventAsync, AddAgentRunAsync -> 3 notifies (count via Changed after Advance per call); FindAsync/ListAsync -> none */ }
[Fact] public async Task Failed_write_does_not_notify() { /* inner throws on SaveAsync -> exception propagates, no notify */ }
// OrchestratorTests
[Fact] public async Task Problems_changed_raised_on_set_and_clear_only() { /* missing secret tick -> 1; same problem again -> still 1; secret fixed tick -> 2 */ }
// ProjectRegistryTests
[Fact] public void Paused_changed_raised_only_on_change() { /* SetPaused(x,true) twice -> 1 */ }
// TicketsApiTests
[Fact] public async Task Poll_requests_are_coalesced() { /* 5 concurrent POST /api/poll -> all 202; at most 2 tracker searches after WhenIdleAsync (add a search counter to FakeTracker if absent) */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter "BoardNotifierTests|NotifyingTicketStoreTests|OrchestratorTests|ProjectRegistryTests|TicketsApiTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.** `BoardNotifier` uses `time.CreateTimer` (one-shot, armed by the first `Notify` in an idle window) under a `Lock`.
- [ ] **Step 4: Run** — Expected: PASS; full `dotnet test` stays green.
- [ ] **Step 5: Commit** `git commit -m "feat(host): change notifications for the dashboard; coalesce /api/poll"`

---

### Task 3: Dashboard shell, sign-in, and origin protection

**Files:**
- Create: `src/Symphonist.Host/Dashboard/{DashboardLogin,DashboardOriginMiddleware,DashboardSetup}.cs`, `src/Symphonist.Host/Components/{App,Routes,_Imports}.razor`, `Components/Layout/{MainLayout,ReconnectModal}.razor`, `Components/Pages/Board.razor` (placeholder heading "Symphonist" only), `wwwroot/app.css`
- Modify: `src/Symphonist.Host/Program.cs`, `src/Symphonist.Host/Api/TicketsApi.cs`
- Test: `tests/Symphonist.Host.Tests/Dashboard/{DashboardLoginTests,DashboardAuthTests}.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class DashboardLogin(TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    public const int MaxOutstanding = 16;          // issuing more evicts the oldest
    public string IssueCode();                     // 32 random bytes, base64url
    public bool Redeem(string? code);              // true once per unexpired code
}

public static class DashboardSetup
{
    public const string CookieName = "symphonist.dashboard";
    public static IServiceCollection AddDashboard(this IServiceCollection services, int port);
    public static WebApplication MapDashboard(this WebApplication app);
}

public sealed record DashboardLinkDto(Uri Url);   // POST /api/dashboard/login-code -> 200 DashboardLinkDto
```

Endpoints and behavior:

| Path | Auth | Behavior |
|---|---|---|
| `POST /api/dashboard/login-code` | API token | `{ "url": "http://127.0.0.1:{port}/login?code=…" }` |
| `GET /login?code=` | none | valid code → sign in (cookie: persistent, 30-day sliding, HttpOnly, SameSite=Strict, Path=/) and `302 /`; else `401` text/html page "This sign-in link has expired or was already used. Run `symphonist dashboard` again." |
| `GET /signed-out` | none | cookie challenge target; text/html "Open the dashboard with `symphonist dashboard`." |
| everything from `MapRazorComponents<App>()` incl. `/_blazor` | cookie | `.RequireAuthorization()`; unauthenticated page → `302 /signed-out` |

`DashboardOriginMiddleware`: allowed origins are exactly `http://127.0.0.1:{port}` and `http://localhost:{port}`. A request outside `/api` that has an `Origin` header not in that set → `403`. A request to `/_blazor` (any method) without an allowed `Origin` → `403`. Order in `MapDashboard`: origin middleware → `UseAuthentication` → `UseAuthorization` → `UseAntiforgery` → `MapStaticAssets` → endpoints. Data Protection: `PersistKeysToFileSystem(SymphonistPaths.Resolve("keys"))`, `SetApplicationName("Symphonist")`, `ProtectKeysWithDpapi()` on Windows; create the keys directory with `UserRead|UserWrite|UserExecute` on Unix. `App.razor` uses global `@rendermode="InteractiveServer"` on `<Routes>` and `<HeadOutlet>`. `ReconnectModal` copy: "Symphonist host is not responding — reconnecting…" and, after rejection, "The host restarted. Reload to continue." with a Reload button.

- [ ] **Step 1: Write failing tests**

```csharp
// DashboardLoginTests (FakeTimeProvider)
[Fact] public void Code_redeems_once() { var c = login.IssueCode(); Assert.True(login.Redeem(c)); Assert.False(login.Redeem(c)); }
[Fact] public void Code_expires_after_two_minutes() { /* Advance(2m + 1s) -> Redeem false */ }
[Fact] public void Unknown_or_empty_code_fails() { Assert.False(login.Redeem(null)); Assert.False(login.Redeem("nope")); }
[Fact] public void Oldest_code_evicted_beyond_max() { /* issue 17 -> first fails, 17th succeeds */ }
// DashboardAuthTests (WebApplicationFactory, same fakes as TicketsApiTests; helper SignInAsync(client) posts login-code with token then GETs the url)
[Fact] public async Task Board_requires_sign_in() { /* GET / without cookie (AllowAutoRedirect=false) -> 302 Location /signed-out... */ }
[Fact] public async Task Login_code_requires_api_token() { /* POST /api/dashboard/login-code without header -> 401 */ }
[Fact] public async Task Sign_in_sets_strict_http_only_cookie_and_redirects() { /* Set-Cookie contains "symphonist.dashboard", "httponly", "samesite=strict"; 302 Location "/" */ }
[Fact] public async Task Signed_in_board_renders() { /* GET / -> 200, body contains "Symphonist" */ }
[Fact] public async Task Reused_code_is_rejected() { /* second GET /login?code=same -> 401, body contains "symphonist dashboard" */ }
[Fact] public async Task Cross_origin_hub_request_is_rejected() { /* signed in; POST /_blazor/negotiate?negotiateVersion=1 with Origin https://evil.example -> 403 */ }
[Fact] public async Task Hub_request_without_origin_is_rejected() { /* signed in; POST /_blazor/negotiate without Origin -> 403 */ }
[Fact] public async Task Same_origin_hub_request_is_allowed() { /* signed in; Origin http://127.0.0.1:5199 -> 200 */ }
[Fact] public async Task Cookie_does_not_authorize_api() { /* signed in, no token header: GET /api/tickets -> 401 */ }
[Fact] public async Task Cookie_survives_host_restart() { /* sign in with factory A, copy cookie; dispose A; new factory B on same SYMPHONIST_HOME -> GET / with cookie -> 200 */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter "DashboardLoginTests|DashboardAuthTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.** Keep `Program.cs` to `builder.Services.AddDashboard(port)` and `app.MapDashboard()` (after `ApiTokenMiddleware`).
- [ ] **Step 4: Run** — Expected: PASS; full `dotnet test` green (existing `TicketsApiTests` unaffected).
- [ ] **Step 5: Commit** `git commit -m "feat(dashboard): Blazor shell with single-use sign-in and same-origin enforcement"`

---

### Task 4: Board query and shared ticket links

**Files:**
- Create: `src/Symphonist.Host/TicketLinks.cs`, `src/Symphonist.Host/Dashboard/{BoardQuery,BoardModels}.cs`
- Modify: `src/Symphonist.Host/Api/TicketsApi.cs` (use `TicketLinks.PullRequest`)
- Test: `tests/Symphonist.Host.Tests/Dashboard/BoardQueryTests.cs`

**Interfaces:**
- Consumes: `ITicketStore.AgentCostsAsync` (Task 1), `Orchestrator.ProblemOf`, `ProjectRegistry.Projects/IsPaused`.
- Produces:

```csharp
public static class TicketLinks
{
    public static Uri? Issue(ProjectConfig? p, string key);          // jira: new Uri(Site, "browse/" + Uri.EscapeDataString(key)); other types null
    public static Uri? PullRequest(ProjectConfig? p, long? id);      // existing Bitbucket logic moved from TicketsApi
}

public sealed record BoardProject(string Id, string Name, bool Paused, Isolation Isolation, string? Problem, string Source);
public sealed record BoardCard(string Project, string Key, string Title, string StageId, StageStatus Status,
    bool WaitingOnYou, string? BlockedReason, int Attempts, int? MaxAttempts, DateTimeOffset? Since,
    decimal? CostUsd, Uri? IssueUrl, Uri? PrUrl, string Source);
public sealed record BoardColumn(string Id, string Title, IReadOnlyList<BoardCard> Cards, int HiddenCount);
public sealed record BoardCounts(int WaitingOnYou, int Running, int Blocked);
public sealed record BoardModel(IReadOnlyList<BoardProject> Projects, string? ProjectFilter,
    IReadOnlyList<BoardColumn> Columns, BoardCounts Counts);

public sealed class BoardQuery(ProjectRegistry projects, ITicketStore store, Orchestrator orchestrator)
{
    public const string UnknownColumnId = "?unknown";
    public const int DoneLimit = 20;
    public Task<BoardModel> LoadBoardAsync(string? projectFilter, CancellationToken ct);
}
```

Rules (the tests pin each):

- `ProjectFilter` is the filter if it names a configured project, else `null` (board shows all).
- Columns: filtered → that project's stages in pipeline order, `Id` = stage id, `Title` = stage id. All → one column per `StageKind` used by any configured project, in enum order, `Id`/`Title` = kind name (`"Implement"`). A ticket whose project or stage is not in config goes to a final column `UnknownColumnId` titled "Unknown stage", created only when needed.
- `Source` = tracker `type` with first letter upper-cased (`"Jira"`); `"Unknown"` for a removed project. `MaxAttempts` = `MaxAttemptsPerStage`, null for a removed project.
- `WaitingOnYou` = `Status == AwaitingApproval`. `Since`: Pending/Running → `StageStartedAt`; AwaitingApproval → `WaitingSince`; Approved/Blocked/Done → `UpdatedAt`.
- Card order in a column: AwaitingApproval, Blocked, Running, Approved, Pending, Done; then `Since` ascending (nulls last); then `Key` ordinal.
- Done column (kind `Done`): 20 most recent by `UpdatedAt` desc; `HiddenCount` = the rest; other columns `HiddenCount` = 0.
- Counts cover the visible (filtered) tickets: waiting, running, blocked.
- `CostUsd` from `AgentCostsAsync`; null when absent.

- [ ] **Step 1: Write failing tests** (TestHome with `orders` and `billing` projects as in `TicketsApiTests`, `FakeTicketStore`, real `Orchestrator` built like `OrchestratorTests.Create`):

```csharp
[Fact] public async Task All_projects_board_groups_by_stage_kind() { /* titles == ["Triage","Plan","Implement","Done"] */ }
[Fact] public async Task Project_filter_uses_its_pipeline_order() { /* filter "orders" -> ids ["triage","plan","implement","done"], ProjectFilter "orders" */ }
[Fact] public async Task Unknown_filter_shows_all() { /* filter "nope" -> ProjectFilter null, both projects' tickets present */ }
[Fact] public async Task Waiting_cards_sort_first_then_oldest() { /* in implement: Pending(10:00), AwaitingApproval(11:00), Blocked, AwaitingApproval(09:00) -> keys order W09, W11, Blocked, Pending */ }
[Fact] public async Task Counts_cover_visible_tickets() { /* 2 waiting, 1 running, 1 blocked across projects; filter billing -> its counts only */ }
[Fact] public async Task Done_column_keeps_twenty_most_recent() { /* 23 done -> 20 cards, newest first, HiddenCount 3 */ }
[Fact] public async Task Card_has_links_cost_and_source() { /* PullRequestId 7 -> PrUrl ".../pull-requests/7"; IssueUrl "https://acme.atlassian.net/browse/ORD-1"; cost 0.42; Source "Jira" */ }
[Fact] public async Task Ticket_with_unknown_stage_goes_to_unknown_column() { /* StageId "review" -> last column "?unknown" "Unknown stage" */ }
[Fact] public async Task Ticket_of_removed_project_is_still_shown() { /* ProjectId "gone" -> unknown column, Source "Unknown", IssueUrl null, MaxAttempts null */ }
[Fact] public async Task Project_problem_and_pause_are_reported() { /* missing secret tick -> Problem contains "secret"; SetPaused -> Paused true; billing Isolation Host */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter BoardQueryTests` — Expected: FAIL.
- [ ] **Step 3: Implement.** `BoardQuery` never calls `ProjectConfig.Stage` on stored stage ids without a guard (it throws `KeyNotFoundException`); look stages up by `FirstOrDefault`.
- [ ] **Step 4: Run** `BoardQueryTests` and `TicketsApiTests` — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(dashboard): board view model and shared ticket links"`

---

### Task 5: Live pipeline board

**Files:**
- Modify: `src/Symphonist.Host/Components/Pages/Board.razor` (+ `.razor.css`), `wwwroot/app.css`, `tests/Symphonist.Host.Tests/Symphonist.Host.Tests.csproj` (add `bunit`, latest stable, exact version)
- Create: `Components/Shared/{TicketCard,ProjectBar,StatusLine}.razor` (+ `.razor.css`)
- Test: `tests/Symphonist.Host.Tests/Dashboard/BoardComponentTests.cs`

**Interfaces:**
- Consumes: `BoardQuery.LoadBoardAsync` (Task 4), `BoardNotifier.Changed` (Task 2), `Orchestrator.ApproveAsync/RequestTick`, `ProjectRegistry.SetPaused`, `TimeProvider`.
- Produces: route `/` with query `?project={id}`; ticket links to `/tickets/{project}/{key}` (Task 6 owns that page).

Board contents, top to bottom:

1. Header: "Symphonist", counts as text — "{n} waiting on you · {n} running · {n} blocked" — a project filter `<select>` ("All projects" + each project name) that navigates to `/?project=id` (or `/`), and a "Poll now" button (`RequestTick`, status line "Poll requested").
2. `ProjectBar`, one row per visible project: name, source badge, "host mode" badge when `Isolation.Host`, "Paused" badge + Resume button or Pause button, and the problem text as a warning banner when `Problem` is set.
3. `StatusLine`: last command outcome or error message; `role="status"`.
4. Columns side by side, horizontally scrollable on narrow screens; column header shows title and card count (`HiddenCount` shown as "+N older" at the bottom of Done).
5. `TicketCard`: key (link to detail page), title, project name (all-projects board only), source badge, status label text, attempts "attempt {n}/{max}" when `Attempts > 0`, time text — Running "running {elapsed}", AwaitingApproval "waiting {elapsed}", Blocked "blocked {elapsed}" (elapsed as `4m`, `2h 5m`, `3d`), cost "$0.42" when present, blocked reason (first 140 chars), PR link "PR" when `PrUrl`, and an "Approve" button when `WaitingOnYou`. Waiting cards get the highlight style plus the "Waiting on you" label; blocked cards a danger style plus "Blocked".

Behavior:

- Subscribes to `BoardNotifier.Changed` in `OnInitialized`, reloads via `InvokeAsync`, unsubscribes in `Dispose`. A `PeriodicTimer` built from `TimeProvider` (30 s) re-renders elapsed times only.
- A reload that throws keeps the previous model and shows "Could not refresh the board: {message}" in the status line; the next successful reload clears it.
- Approve: disables that card's button while in flight, shows `TransitionOutcome.Message` in the status line; a `KeyNotFoundException` message is shown the same way.
- Styling: CSS custom properties in `app.css` with light and dark values (`prefers-color-scheme`), system font stack, visible focus rings, running indicator pulses only when `prefers-reduced-motion: no-preference`. Use the `frontend-design` skill's guidance for the visual pass; the tests below are the acceptance criteria.

- [ ] **Step 1: Write failing bUnit tests** (register real `BoardQuery`, `BoardNotifier` with `FakeTimeProvider`, `Orchestrator` from fakes, `ProjectRegistry` over TestHome):

```csharp
[Fact] public void Renders_columns_cards_and_counts() { /* "2 waiting on you · 1 running · 1 blocked"; column headers; card ORD-1 title */ }
[Fact] public void Waiting_card_is_labelled_and_has_approve() { /* card has "Waiting on you" text and an Approve button; Pending card has neither */ }
[Fact] public void Approve_calls_orchestrator_and_shows_outcome() { /* click -> store ticket Approved; status line shows outcome message */ }
[Fact] public void Approve_on_stale_card_shows_outcome_message() { /* ticket set to Approved in store before click -> status line "implement is not awaiting approval" */ }
[Fact] public void Board_refreshes_when_notified() { /* add ticket to store, notifier.Notify(), Advance(250ms) -> new card rendered (WaitForAssertion) */ }
[Fact] public void Refresh_failure_shows_banner_and_keeps_board() { /* FakeTicketStore.FailNextList = true; notify -> status line "Could not refresh the board"; ORD-1 still rendered */ }
[Fact] public void Filter_shows_one_project_pipeline() { /* render with ?project=billing -> column ids billing's; no orders cards */ }
[Fact] public void Paused_project_offers_resume() { /* SetPaused(orders) -> "Paused" badge + Resume; click -> IsPaused false */ }
[Fact] public void Host_mode_project_is_marked() { /* billing row contains "host mode" */ }
[Fact] public void Ticket_text_is_not_rendered_as_markup() { /* Title "<b>x</b>" -> markup contains "&lt;b&gt;" and no <b> element */ }
[Fact] public void No_projects_shows_setup_hint() { /* empty projects dir -> "No projects configured" and the projects directory path */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter BoardComponentTests` — Expected: FAIL.
- [ ] **Step 3: Implement** the components and styles. Add `FailNextList` to `FakeTicketStore` if absent.
- [ ] **Step 4: Run** — Expected: PASS. Also extend `DashboardAuthTests.Signed_in_board_renders` to seed one ticket and assert its key is in the prerendered HTML.
- [ ] **Step 5: Commit** `git commit -m "feat(dashboard): live pipeline board"`

---

### Task 6: Ticket detail page

**Files:**
- Create: `src/Symphonist.Host/Components/Pages/TicketDetail.razor` (+ `.razor.css`)
- Modify: `src/Symphonist.Host/Dashboard/{BoardQuery,BoardModels}.cs`
- Test: `tests/Symphonist.Host.Tests/Dashboard/{BoardQueryTests,TicketDetailComponentTests}.cs`

**Interfaces:**
- Consumes: `ITicketStore.AgentRunsAsync/EventsAsync` (Task 1), `BoardCard` building from Task 4, `Orchestrator.ApproveAsync/SendBackAsync/UnblockAsync`.
- Produces:

```csharp
public sealed record TicketDetailModel(BoardCard Card, string? Branch, string? SpecPath, string? PlanPath,
    Uri? ReleasePrUrl, string? Feedback, IReadOnlyList<AgentRunRecord> Runs, IReadOnlyList<TicketEvent> Events,
    IReadOnlyList<string> SendBackTargets, bool CanApprove, bool CanSendBack, bool CanUnblock);

// BoardQuery addition
public Task<TicketDetailModel?> LoadTicketAsync(string projectId, string key, CancellationToken ct);   // null when not in store
```

Rules: `CanApprove` = AwaitingApproval; `CanSendBack` = AwaitingApproval/Approved/Blocked (mirrors `PipelineEngine.SendBack`); `CanUnblock` = Blocked. `SendBackTargets` = current stage id then earlier stage ids nearest first, excluding `Done`-kind stages. For a removed project or unknown stage all three are false and `SendBackTargets` is empty (read-only). Events newest first; runs newest first.

Page `/tickets/{Project}/{Key}`, live (same notifier subscription and error handling as the board):

- Back link to the board (keeps `?project=` if it came from a filtered board).
- Header: key, title, stage, status label, attempts, blocked reason in full, feedback in full when set ("Last feedback").
- Links: tracker issue, PR, release PR, branch name as text, spec and plan paths as text.
- Actions: Approve; Unblock; Send back — a textarea (required; the button stays disabled while it is blank or whitespace) and a stage `<select>` from `SendBackTargets`, default first. Each shows its outcome in the status line and reloads.
- Agent runs table: stage, started (local time), duration, result ("succeeded"/"failed"), cost, summary or error (plain text, wraps), raw log path as text with a "Copy path" button (`navigator.clipboard` via a small JS module in `wwwroot`).
- Events timeline: time, stage, message.
- Not found: "Ticket {key} was not found in project {project}." with a back link (HTTP 404 on prerender via `NavigationManager.NotFound()`).

- [ ] **Step 1: Write failing tests**

```csharp
// BoardQueryTests
[Fact] public async Task Detail_lists_runs_and_events_newest_first() { }
[Fact] public async Task Send_back_targets_are_current_then_earlier() { /* at implement -> ["implement","plan","triage"] */ }
[Fact] public async Task Detail_actions_follow_status() { /* AwaitingApproval -> approve+sendback; Blocked -> unblock+sendback; Running -> none */ }
[Fact] public async Task Unknown_project_detail_is_read_only() { /* project "gone" -> all Can* false, targets empty, model not null */ }
[Fact] public async Task Missing_ticket_detail_is_null() { }
// TicketDetailComponentTests
[Fact] public void Send_back_requires_feedback() { /* button disabled for "" and "   " */ }
[Fact] public void Send_back_to_selected_stage() { /* feedback "use the v2 endpoint", select "plan" -> store ticket StageId "plan", Feedback set; status line shows outcome */ }
[Fact] public void Unblock_moves_ticket_to_pending() { }
[Fact] public void Run_summary_and_error_render_as_text() { /* Summary "<script>x</script>" escaped */ }
[Fact] public void Missing_ticket_shows_not_found() { }
[Fact] public void Detail_refreshes_when_notified() { /* new event added + notify -> appears */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter "BoardQueryTests|TicketDetailComponentTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(dashboard): ticket detail with approve, send back, unblock"`

---

### Task 7: `symphonist dashboard`, docs, and a real browser check

**Files:**
- Create: `src/Symphonist.Cli/BrowserLauncher.cs`
- Modify: `src/Symphonist.Cli/{Cli,SymphonistClient}.cs`, `README.md`
- Test: `tests/Symphonist.Cli.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `POST /api/dashboard/login-code` → `DashboardLinkDto` (Task 3).
- Produces:

```csharp
// SymphonistClient
public Task<Uri> CreateDashboardLinkAsync(CancellationToken ct);
// BrowserLauncher
public static class BrowserLauncher { public static bool TryOpen(Uri url); }   // Windows: UseShellExecute with FileName=url; macOS: "open"; else "xdg-open"; false on any start failure
// Cli.RunAsync gains a trailing optional parameter: Func<Uri, bool>? openBrowser = null  (default BrowserLauncher.TryOpen)
```

Command: `symphonist dashboard [--no-browser]` — "Open the dashboard in your browser". Gets the link; with `--no-browser`, prints the URL only; otherwise opens it and prints "Opened the dashboard (link valid for 2 minutes)"; if opening fails, prints "Could not open a browser. Open this link within 2 minutes: {url}". Exit 0 in all three cases; host down → existing exit code 2 path.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Dashboard_opens_link_from_host() { /* stub returns {"url":"http://127.0.0.1:5199/login?code=abc"}; openBrowser captures it; request had token header; exit 0 */ }
[Fact] public async Task Dashboard_no_browser_prints_link() { /* stdout == url + newline; openBrowser not called */ }
[Fact] public async Task Dashboard_prints_link_when_browser_fails() { /* openBrowser returns false -> stdout contains "Could not open a browser" and the url */ }
[Fact] public async Task Dashboard_host_down_exits_2() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Cli.Tests --filter Dashboard` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; full `dotnet test` green on the solution; `dotnet build` with 0 warnings.
- [ ] **Step 5: README** — add a "Dashboard" section: `symphonist dashboard`, what the board shows, that the link is single-use for 2 minutes, that the sign-in lasts 30 days per browser, and that `--no-browser` prints the link (for a browser on the same machine only — the host listens on loopback).
- [ ] **Step 6: Real browser check.** With a temp `SYMPHONIST_HOME` holding `samples/projects/orders.example.yaml` (secrets unset, so the project shows its problem banner), run the host, run `symphonist dashboard`, and confirm in a browser: board renders, problem banner shows the missing-secret message, Pause/Resume toggles, "Poll now" shows "Poll requested", light and dark both legible, a narrow (≈400 px) window scrolls columns horizontally without page-level horizontal scroll, and stopping the host shows the reconnect message. Record anything that needed fixing in the task report.
- [ ] **Step 7: Commit** `git commit -m "feat(cli): symphonist dashboard; document the dashboard"`
