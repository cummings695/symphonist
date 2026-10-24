# Backlog Batch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Six small improvements from the backlog and the plan 1/2 deferred lists:
- `@claude` comments rerun a waiting stage.
- Jira comment threads are always read in full.
- Secrets are trimmed and re-read without a restart.
- `symphonist forget` removes a ticket from Symphonist.
- Failed tracker moves are retried.
- Small CLI and dashboard fixes.

**Architecture:** Everything sits on the existing seams.
- `IntegrationFactory` re-reads secrets on every `Create`.
- `JiraTracker` pages a ticket's comments when the search result is truncated.
- `Orchestrator.SyncAsync` gains two per-ticket steps: `@claude` comment handling and tracker-move retry. Each is backed by one new `Ticket` column.
- `forget` is a new store delete plus an orchestrator command, an API endpoint and a CLI command.

**Tech Stack:** .NET 10, EF Core + SQLite (migrations via the local `dotnet-ef` tool), ASP.NET Core minimal APIs, System.CommandLine, Blazor Server, xUnit, bUnit, FakeTimeProvider.

**Spec:** `docs/superpowers/specs/2026-10-21-symphonist-design.md`. Sources:
- `@claude` feature: `docs/superpowers/notes/feature-backlog.md`.
- Deferred items: `docs/superpowers/notes/2026-10-22-walking-skeleton-deferred.md` and `docs/superpowers/notes/2026-10-23-dashboard-deferred.md`.
- The anti-goal in spec §1: "if i have to keep making too many adjustments to the automatic process".

Plan-level decisions (builder's calls):

- **`@claude` marker:** a developer comment (`IsMine`, not Symphonist-signed) whose text, after trimming, starts with `@claude` (case-insensitive) followed by whitespace or end of text. Feedback is the text after the marker, trimmed.
- **When a `@claude` comment acts:**
  - If the ticket is `AwaitingApproval` or `Blocked`, it sends the ticket back to rerun its **current** stage. The feedback is every unhandled `@claude` comment, oldest first, joined with a blank line.
  - If the ticket is `Pending`, `Running` or `Approved`, the comment waits: it is handled once the stage reaches its gate. A comment made while an agent runs therefore reruns the stage right after it finishes.
  - A comment is "unhandled" when it was created after `max(StageStartedAt, ClaudeCommentsHandledAt)`. Entering a new stage resets the baseline, so a comment never leaks into a later stage.
  - `@claude` comments are handled only on ticks where the card's tracker status did not change. A status move takes priority, and its send-back already carries the latest comment as feedback.
- **Retrying a tracker move:**
  - A move that was skipped (integration error) or threw is remembered in `Ticket.TrackerRetryStatus`.
  - Each later tick retries it while the card is still at `LastSourceStatus`, meaning the developer hasn't moved it.
  - A developer move clears the retry, and so does any new move Symphonist makes.
  - "No transition to X" is a workflow fact, not a failure, so it is never retried.
- **`forget`:**
  - Deletes the ticket, its events and its agent runs from the store.
  - Refused while an agent run is in progress (HTTP 409).
  - Jira, Bitbucket, the workspace clone and logs are untouched.
  - If the ticket still matches the tracker query, the next poll picks it up again as new; the CLI says so.
- **Secrets:**
  - `IntegrationFactory` caches per project only while the config object *and* both secret values are unchanged. This is one OS-store read per secret per tick.
  - `secret set` trims all leading and trailing whitespace.

## Global Constraints

- .NET 10 (`net10.0`) for every project.
- Windows, macOS, and Linux supported in v1. No Windows-only APIs in the core; platform-specific pieces sit behind small abstractions. Paths, shells, line endings, and process launching must work on all three. CI builds and tests on all three.
- Acts as the developer: tickets assigned to them, actions under their accounts.
- Symphonist's own tracker comments are signed with `TrackerComments.Footer` ("— posted by Symphonist") and are never treated as developer feedback.
- Secrets: OS credential store on each platform, never in plain config files. Secret values never appear in logs, events, exceptions or CLI output.
- Local API bound to localhost only; `/api/*` requires `X-Symphonist-Token`.
- Ticket text is rendered as text only in the dashboard (no `MarkupString`).
- Every commit message ends with the `Claude-Session:` trailer line given in the dispatch.

## Review Focus

1. **The developer writes several `@claude` comments in a row, or one while the agent is still running.** Expect exactly one rerun that carries all of them in order, and none of them re-triggers on later ticks. → tests `Several_claude_comments_make_one_rerun` and `Claude_comment_during_run_applies_at_the_gate` (Task 3).
2. **A `@claude` comment on a ticket that has since moved to a later stage.** Expect it to be ignored; it never reruns the later stage. → test `Claude_comment_from_earlier_stage_is_ignored` (Task 3).
3. **A Jira ticket with more comments than the search returns.** Expect all comments to be read, including the newest `@claude` reply. → test `Fetches_remaining_comments_when_truncated` (Task 2).
4. **The secret is fixed while the host keeps running.** Expect the next tick to use the new value, and a stuck tracker move to go through. → tests `New_secret_value_is_used_without_restart` (Task 1) and `Failed_move_is_retried_after_secret_is_fixed` (Task 4).
5. **Forgetting a ticket whose agent is running, or one that still matches the query.** Expect 409 with a clear message for the first. For the second, expect the ticket to come back as new on the next poll, and the CLI to warn about it. → tests `Forget_refused_while_running` and `Forgotten_ticket_is_picked_up_again_if_still_matching` (Task 5).

---

## File Structure

```
src/Symphonist.Core/Integrations/TrackerComments.cs      (modify) ClaudeRequest(body)
src/Symphonist.Core/Pipeline/Ticket.cs                   (modify) ClaudeCommentsHandledAt, TrackerRetryStatus
src/Symphonist.Core/Storage/ITicketStore.cs              (modify) DeleteAsync
src/Symphonist.Integrations/IntegrationFactory.cs        (modify) secret-aware cache
src/Symphonist.Integrations/Jira/JiraTracker.cs          (modify) page truncated comments
src/Symphonist.Integrations/Storage/{SymphonistDbContext,SqliteTicketStore}.cs (modify)
src/Symphonist.Integrations/Storage/Migrations/<stamp>_ClaudeComments.cs, <stamp>_TrackerRetry.cs (create)
src/Symphonist.Host/Orchestration/Orchestrator.cs        (modify) claude comments, retry, ForgetAsync
src/Symphonist.Host/Api/TicketsApi.cs                    (modify) DELETE ticket
src/Symphonist.Host/Components/Shared/StatusLine.razor, wwwroot/app.css (modify) small UI fixes
src/Symphonist.Cli/{Cli,SymphonistClient}.cs             (modify) trim, forget, --to help
README.md                                                (modify) @claude, forget
docs/superpowers/notes/feature-backlog.md                (modify) mark @claude done
```

---

### Task 1: Secrets are trimmed and re-read without a restart

**Files:**
- Modify: `src/Symphonist.Integrations/IntegrationFactory.cs`, `src/Symphonist.Cli/Cli.cs` (secret set).
- Test: `tests/Symphonist.Integrations.Tests/` (the IntegrationFactory tests; create `IntegrationFactoryTests.cs` if none exist), `tests/Symphonist.Cli.Tests/CliTests.cs`.

**Interfaces:**
- Produces: `IntegrationFactory.Create(ProjectConfig)`. Same signature; it returns the cached `ProjectIntegrations` only when the config reference **and** both token values equal the cached ones, otherwise it builds new ones. The cache stores the token values in memory only.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void New_secret_value_is_used_without_restart()
{ /* secrets jira-acme="old" -> Create(p) = io1; set "new" -> Create(p) = io2, not same instance; requests from io2's tracker carry Basic auth for "new" (StubHttpHandler) */ }
[Fact] public void Unchanged_secrets_reuse_integrations() { /* Create twice -> same instance */ }
[Fact] public void Removed_secret_is_reported() { /* Remove -> InvalidOperationException "secret 'jira-acme' not set; run: symphonist secret set jira-acme" */ }
// CliTests
[Fact] public async Task Secret_set_trims_whitespace() { /* stdin "  tok-123 \t\r\n" -> stored "tok-123" */ }
[Fact] public async Task Secret_set_rejects_whitespace_only() { /* stdin "   " -> exit 1, "Secret value must not be empty" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter IntegrationFactoryTests` and `dotnet test tests/Symphonist.Cli.Tests --filter Secret_set` — Expected: FAIL.
- [ ] **Step 3: Implement.** In `Cli.cs`, replace `TrimEnd('\r','\n')` with `Trim()`. In the factory, extend the cache tuple with the two token strings and compare them ordinally.
- [ ] **Step 4: Run** the same tests — Expected: PASS.
- [ ] **Step 5: Commit** `fix: trim pasted secrets and pick up changed secrets without a restart`

---

### Task 2: Jira comment threads read in full

**Files:**
- Modify: `src/Symphonist.Integrations/Jira/JiraTracker.cs`.
- Test: `tests/Symphonist.Integrations.Tests/Jira/JiraTrackerTests.cs`.

**Interfaces:**
- Produces: `QueryAsync` returns `SourceTicket.Comments` holding **every** comment of each ticket, ordered by `Created`.

Rules:
- In the search result, `fields.comment` carries `total` and `comments`. When `total` is greater than the number of comments returned, fetch `GET rest/api/3/issue/{key}/comment?startAt={n}&maxResults=100` page by page until `startAt + comments.Length >= total` or a page is empty.
- Parse the comments with the existing comment parsing; don't duplicate it.
- De-duplicate by comment `id`.
- An HTTP failure throws, as `QueryAsync` already promises ("never partial").

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Fetches_remaining_comments_when_truncated() { /* search returns total 3, 1 comment; /comment?startAt=0 returns 3 -> Comments.Count == 3, newest body present, ordered by Created */ }
[Fact] public async Task Does_not_fetch_comments_when_complete() { /* total == returned -> no /comment request */ }
[Fact] public async Task Comment_page_failure_throws() { /* /comment -> 500 -> HttpRequestException */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter JiraTrackerTests` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `fix(jira): read a ticket's whole comment thread when search truncates it`

---

### Task 3: `@claude` comments rerun a waiting stage

**Files:**
- Modify: `src/Symphonist.Core/Integrations/TrackerComments.cs`, `src/Symphonist.Core/Pipeline/Ticket.cs`, `src/Symphonist.Integrations/Storage/SymphonistDbContext.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `README.md`, `docs/superpowers/notes/feature-backlog.md`.
- Create: migration `ClaudeComments` (`dotnet tool run dotnet-ef migrations add ClaudeComments --project src/Symphonist.Integrations`).
- Test: `tests/Symphonist.Core.Tests/` (TrackerComments tests), `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs`, and the SQLite store round trip in `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`.

**Interfaces:**
- Consumes: complete comment lists (Task 2).
- Produces:

```csharp
// TrackerComments
public const string ClaudeMarker = "@claude";
/// The request text of a developer "@claude" comment (marker removed, trimmed); null when the body does not start with the marker.
public static string? ClaudeRequest(string body);
// Ticket
public DateTimeOffset? ClaudeCommentsHandledAt { get; set; }   // Created of the newest @claude comment acted on; stored with the DateTimeOffset converter like the other times
```

Orchestrator behavior (in `SyncAsync`, for a known ticket whose source status is unchanged this tick):
- Collect the unhandled `@claude` comments: `IsMine`, not `TrackerComments.IsSymphonist(body)`, `ClaudeRequest(body)` not null, and `Created > max(StageStartedAt, ClaudeCommentsHandledAt)`.
- If there are none, or the ticket is not `AwaitingApproval`/`Blocked`, do nothing.
- Otherwise:
  - `PipelineEngine.SendBack(p, t, feedback, t.StageId, now)`, where feedback is the requests joined with `"\n\n"`. Empty requests are skipped; if all are empty, the feedback is `"(no details given)"`.
  - Set `ClaudeCommentsHandledAt` to the newest such comment's `Created`.
  - Save, then `ApplyAsync`.
  - Record the event `"@claude comment: rerunning {stage}"`. This is in addition to the outcome event, which `ApplyAsync` already records.

- [ ] **Step 1: Write the failing tests**

```csharp
// Core
[Theory]
[InlineData("@claude please use v2", "please use v2")]
[InlineData("  @Claude\nAnswers: yes", "Answers: yes")]
[InlineData("@claude", "")]
public void ClaudeRequest_strips_marker(string body, string expected) => Assert.Equal(expected, TrackerComments.ClaudeRequest(body));
[Theory] [InlineData("hey @claude")] [InlineData("@claudette")] [InlineData("note to Sam")]
public void ClaudeRequest_ignores_other_comments(string body) => Assert.Null(TrackerComments.ClaudeRequest(body));
// Orchestrator
[Fact] public async Task Claude_comment_at_gate_reruns_stage_with_feedback() { /* triage AwaitingApproval; mine "@claude answers: A, B" after WaitingSince -> Pending at triage, Feedback "answers: A, B", events contain "@claude comment: rerunning triage" */ }
[Fact] public async Task Several_claude_comments_make_one_rerun() { /* two comments -> Feedback "first\n\nsecond"; next tick (same comments) -> no further send-back */ }
[Fact] public async Task Claude_comment_during_run_applies_at_the_gate() { /* comment while Running -> no change; stage completes -> AwaitingApproval; next tick -> rerun with that comment */ }
[Fact] public async Task Claude_comment_from_earlier_stage_is_ignored() { /* comment created before plan's StageStartedAt -> plan waiting stays waiting */ }
[Fact] public async Task Claude_comment_on_blocked_ticket_reruns() { /* Blocked -> Pending, attempts reset, feedback set */ }
[Fact] public async Task Plain_and_symphonist_comments_do_not_trigger() { /* "thanks!" and a signed "@claude …\n\n— posted by Symphonist" -> no change */ }
[Fact] public async Task Status_move_wins_over_claude_comment_in_same_tick() { /* card moved to approvedWhenStatus and a @claude comment -> approved, no rerun */ }
// Store
[Fact] public async Task Claude_handled_time_round_trips() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter ClaudeRequest` and `dotnet test tests/Symphonist.Host.Tests --filter Claude` — Expected: FAIL.
- [ ] **Step 3: Implement**, including the migration.
- [ ] **Step 4: Run** those tests plus `SqliteTicketStoreTests` — Expected: PASS.
- [ ] **Step 5: Docs.**
  - README: add a short "Answering Symphonist from Jira" section. Start a comment with `@claude` while a ticket waits at a gate (or is blocked), and that stage reruns with the comment as feedback. Comments made while it runs apply when it reaches the gate. Other comments are ignored.
  - `feature-backlog.md`: mark the item done (plan 3a).
- [ ] **Step 6: Commit** `feat: @claude comments rerun a waiting stage with the comment as feedback`

---

### Task 4: Failed tracker moves are retried

**Files:**
- Modify: `src/Symphonist.Core/Pipeline/Ticket.cs`, `src/Symphonist.Integrations/Storage/SymphonistDbContext.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`.
- Create: migration `TrackerRetry`.
- Test: `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs`, `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`.

**Interfaces:**
- Produces: `Ticket.TrackerRetryStatus` (`string?`), the target of a tracker move that was skipped or threw.

Rules:
- **In `ApplyAsync`:**
  - When resolving integrations fails, or `TransitionAsync` throws, set `t.TrackerRetryStatus = target` and save.
  - A successful move, or `TransitionAsync` returning false ("no transition"), sets it to null.
  - Any new outcome with a `SourceStatusToSet` replaces it.
- **In `SyncAsync`, for a known ticket whose source status equals `LastSourceStatus`:** if `TrackerRetryStatus` is set, retry the move.
  - Success: set `LastSourceStatus`, clear the retry, and record the event `"tracker moved to '{target}' on retry"`.
  - "No transition": clear the retry and record `"no transition to '{target}' in tracker"`.
  - Failure: keep the retry, and record the failure event only via `AddEventOnceAsync`, so a long outage doesn't add an event per tick.
- **When the developer moved the card (status changed):** clear `TrackerRetryStatus` before `SourceStatusChanged`.
- Leave Done tickets and tickets that left the query alone.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Failed_move_is_retried_after_secret_is_fixed() { /* approve with missing secret -> TrackerRetryStatus "In Progress"; secret fixed; tick -> tracker transitioned to "In Progress", LastSourceStatus updated, retry cleared, event "tracker moved to 'In Progress' on retry" */ }
[Fact] public async Task Thrown_move_is_retried_once_per_tick_without_event_flood() { /* transition throws 3 ticks -> one failure event; 4th succeeds */ }
[Fact] public async Task Developer_move_cancels_retry() { /* retry pending; card moved by developer -> retry cleared, no transition attempted to old target */ }
[Fact] public async Task No_transition_is_not_retried() { /* TransitionAsync false -> retry null, no further attempts */ }
[Fact] public async Task Tracker_retry_status_round_trips() { /* store */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter "retried|retry|Developer_move_cancels"` — Expected: FAIL.
- [ ] **Step 3: Implement**, including the migration.
- [ ] **Step 4: Run** — Expected: PASS. Run the full `OrchestratorTests` too, since the ApplyAsync changes touch every command.
- [ ] **Step 5: Commit** `feat: retry a failed tracker move once the developer hasn't moved the card`

---

### Task 5: `symphonist forget`

**Files:**
- Modify: `src/Symphonist.Core/Storage/ITicketStore.cs`, `src/Symphonist.Integrations/Storage/SqliteTicketStore.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeTicketStore.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `src/Symphonist.Host/Api/TicketsApi.cs`, `src/Symphonist.Cli/{Cli,SymphonistClient}.cs`, `README.md`.
- Test: `SqliteTicketStoreTests`, `OrchestratorTests`, `TicketsApiTests`, `CliTests`.

**Interfaces:**
- Produces:

```csharp
// ITicketStore
Task<bool> DeleteAsync(string projectId, string key, CancellationToken ct);   // ticket + its events + agent runs; false when not found; notifies like other writes (NotifyingTicketStore)
// Orchestrator
public Task<ForgetResult> ForgetAsync(string projectId, string key, CancellationToken ct);  // under the gate
public enum ForgetResult { Forgotten, NotFound, Running }
// API: DELETE /api/tickets/{project}/{key} -> 204 | 404 ApiError | 409 ApiError("ticket {key} has an agent run in progress; try again when it finishes")
// CLI: symphonist forget <project> <key>
//   prints "Forgot {project}/{key}. Jira, Bitbucket and the workspace are unchanged; if it still matches the tracker query it will be picked up again as new."
```

`Running` means the ticket's id is in the orchestrator's in-flight run set. `NotifyingTicketStore` must forward `DeleteAsync` and notify on `true`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Delete_removes_ticket_events_and_runs() { /* SQLite: then FindAsync null, EventsAsync empty, AgentRunsAsync empty, other ticket untouched */ }
[Fact] public async Task Delete_unknown_returns_false() { }
[Fact] public async Task Forget_refused_while_running() { /* agent run blocked in FakeAgentRunner -> ForgetResult.Running; API 409 */ }
[Fact] public async Task Forgotten_ticket_is_picked_up_again_if_still_matching() { /* forget, tick with ticket still in query -> new ticket Id, "picked up at triage" */ }
[Fact] public async Task Forget_endpoint_returns_204_and_404() { }
[Fact] public async Task Forget_prints_confirmation() { /* CLI: DELETE sent with token; stdout contains "Forgot orders/ORD-1" and "picked up again" */ }
[Fact] public async Task Forget_running_shows_api_error() { /* 409 -> exit 1, message */ }
```

- [ ] **Step 2: Run** the named tests — Expected: FAIL.
- [ ] **Step 3: Implement.** README: add `forget` to the CLI command list, with the warning.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `feat: symphonist forget removes a ticket from Symphonist only`

---

### Task 6: Small CLI and dashboard fixes

**Files:**
- Modify: `src/Symphonist.Cli/Cli.cs`, `src/Symphonist.Host/Components/Shared/StatusLine.razor`, `src/Symphonist.Host/wwwroot/app.css`.
- Test: `CliTests`, `tests/Symphonist.Host.Tests/Dashboard/BoardComponentTests.cs`.

Changes:
1. `send-back --to` help text: `"Stage to send back to (default: rerun the current stage)"`. This matches `PipelineEngine.SendBack` with a null target.
2. **StatusLine.** The element stays rendered (screen readers need the live region), but takes no space when empty: add the class `status-line-empty` when `Message` is null or empty, styled as zero height, zero margin and zero padding.
3. **Phone width.** The counts of empty, non-toggleable stages line up with the counts of toggleable ones: reserve the chevron's width.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Send_back_help_describes_default() { /* "send-back --help" output contains "default: rerun the current stage" */ }
[Fact] public void Empty_status_line_takes_no_space() { /* Board rendered with no message -> status element has class "status-line-empty"; after Poll now -> class absent, text "Poll requested" */ }
```

- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**, then the full `dotnet test Symphonist.slnx` — Expected: PASS, 0 warnings.
- [ ] **Step 5: Screenshot check** at 400×860 and 1440×900. Use a temp `SYMPHONIST_HOME`, port 5299 and seeded tickets, and never touch `~/.symphonist`. Confirm there is no blank band under the header and the counts align. Note the result in the report.
- [ ] **Step 6: Commit** `fix: send-back help text, empty status line takes no space, aligned stage counts`
