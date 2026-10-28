# Release Batches, Usage Limits, Touch Stats and Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Releases become project-level batches started with Release now; a provider's usage limit pauses that provider instead of blocking tickets; the dashboard shows how often the developer steps in; Windows desktop notifications report what needs the developer; reopened tickets restart.

**Architecture:** Usage limits are detected inside each runner and surface as a distinct result; a host `ProviderLimits` service (persisted in SQLite) decides which jobs may start. Touches become structured records next to the existing gate decisions, and a pure Core calculator turns them into stats. A release batch is a Symphonist-owned **release ticket** (`TicketKind.Release`) that reuses the existing release-stage gate, reviewer, merge and merge-hold machinery; a host `ReleaseCoordinator` creates it from a frozen release branch, keeps its member tickets in step, and isolates it from the tracker. Notifications go through an `INotifier` abstraction, folded by a `NotificationCenter`; Windows shows them with a PowerShell script launched through `IProcessRunner`.

**Tech Stack:** .NET 10, EF Core + SQLite (migrations via `dotnet tool run dotnet-ef migrations add <Name> --project src/Symphonist.Integrations`), Blazor Server, System.CommandLine, Windows PowerShell 5.1 + WinRT toasts, xUnit, bUnit, FakeTimeProvider.

**Spec:** `docs/superpowers/specs/2026-10-28-releases-limits-notifications-design.md`

## Global Constraints

- .NET 10 (`net10.0`); Windows, macOS, Linux all build and run. Launch processes with `ProcessSpec` (argument list, never a shell string); build paths with `Path.Combine`.
- No Windows-only APIs in Core or in shared Host code: the Windows notifier is a PowerShell script behind `INotifier`; macOS and Linux get a notifier that does nothing and logs once.
- Existing project files keep loading; no new required YAML fields. A `release` stage now means "batch", not a per-ticket PR.
- A release always needs one more approval after Release now: a `release` stage with `gate: none` behaves as `human` (`ProjectConfig.GateOf`).
- Tracker card moves never approve a release batch.
- A usage-limit result never costs an attempt and never blocks a ticket.
- Batches, provider pauses and touch records persist in the SQLite store (migrations), so they survive restarts.
- Notifications carry project name, ticket key and one line of at most 200 characters; never agent output beyond that line, never a secret; a click opens only `http://127.0.0.1:{port}/…`.
- Fold window 30 seconds; probe interval without a reset time 30 minutes; trend 8 weekly buckets, weeks start Monday, local time.
- Known accepted test failure: Core `ProjectYamlWriterTests.Update_rewrites_only_changed_sections` (CRLF checkout).
- Commit messages end with `Claude-Session: https://claude.ai/code/session_01Fa3TSErRpsDCCnGNNYVXhG`.

## Decisions the plan adds (within the spec's "left to the builder")

- **Release batch = release ticket.** One `Ticket` with `Kind = Release`, key `_release-{n}` (no tracker key starts with `_`), at the project's release stage. It gets the gate, reviewer, auto-approval, merge holds, touch stats and dashboard approve/send-back for free.
- **Frozen release branch.** Release now pushes `symphonist/release-{n}` at the `repo.mainBranch` head it previewed, and the PR goes from that branch to `repo.productionBranch`. A ticket merging into main while the PR is open is therefore not shipped by it (spec: it waits for the next release).
- **Rate limits** (HTTP 429, "rate limit") pause the provider with a reset of now + 5 minutes; usage limits without a stated reset probe every 30 minutes (spec).
- **Any non-limited result of a job for a paused key lifts that key's pause** (the probe got through).
- **A touch is counted once:** total touches = send-backs + unblocks + approvals of a round the reviewer called high. The overrules column shows both overrule kinds (approving a high, sending back a low).
- Usage-limit key: `claude-code` for every Claude job (one token, `claude-code-token`); `codex:chatgpt`; `codex:api-key:{apiKeySecret}`.

## Review Focus

1. A successful run whose output merely mentions "usage limit" (e.g. a ticket about rate limiting) must not pause anything: detection reads only failed runs' error channels. Test in Task 1.
2. A ticket merged into main after Release now is not in the release PR and stays ready for the next batch. Test in Task 9.
3. Moving a batch member's card to the release stage's `approvedWhenStatus` neither approves nor merges the batch. Test in Task 9.
4. Ten tickets blocked in one tick produce one notification, not ten. Test in Task 6.
5. A host restart while Claude is paused with a reset two hours away starts no Claude job before the reset. Test in Task 2.

---

### Task 1: Detect usage limits in the runners

**Files:**
- Create: `src/Symphonist.Core/Agents/UsageLimit.cs`, `src/Symphonist.Integrations/ClaudeCode/ClaudeUsageLimit.cs`, `src/Symphonist.Integrations/Codex/CodexUsageLimit.cs`
- Modify: `src/Symphonist.Core/Agents/IAgentRunner.cs` (`AgentRunResult`), `src/Symphonist.Integrations/ClaudeCode/ClaudeCodeRunner.cs`, `src/Symphonist.Integrations/Codex/CodexRunner.cs`
- Test: `tests/Symphonist.Core.Tests/Agents/UsageLimitTests.cs`, `tests/Symphonist.Integrations.Tests/ClaudeCode/ClaudeUsageLimitTests.cs`, `tests/Symphonist.Integrations.Tests/Codex/CodexUsageLimitTests.cs`, existing runner tests

**Interfaces:**
- Produces:
  - `public sealed record UsageLimit(DateTimeOffset? ResetsAt);`
  - `AgentRunResult(..., JsonElement? Structured = null, UsageLimit? Limit = null)` (new last optional parameter).
  - `public static class UsageLimits { string KeyFor(AgentSettings s); string ProviderName(string key); IReadOnlySet<string> KeysInUse(ProjectConfig p); }` — keys per "Decisions" above; `ProviderName`: `claude-code` → "Claude", `codex…` → "Codex"; `KeysInUse` covers `Agent`, every stage override, `Review.Agent`, `Evidence.Agent`.
  - `ClaudeUsageLimit.Detect(string text, DateTimeOffset now, TimeZoneInfo local) -> UsageLimit?` and `CodexUsageLimit.Detect(...)` with the same signature.

- [ ] **Step 1: Collect the real texts.** Read the CLIs in the built agent image (`symphonist/agent:2`; build it if missing) and note every usage/rate-limit phrasing:
  - `docker run --rm --entrypoint sh symphonist/agent:2 -c 'grep -oE ".{0,60}(usage limit|hit your limit|limit reached|rate.limit).{0,80}" "$(npm root -g)/@anthropic-ai/claude-code/cli.js" | sort -u'`
  - `docker run --rm --entrypoint sh symphonist/agent:2 -c 'strings "$(readlink -f "$(command -v codex)")" | grep -iE "usage limit|try again|rate limit|429" | sort -u'` (follow the npm shim to the native binary if the first hit is a script).
  Put each phrasing as a test case in Step 2, with its source in a comment.

- [ ] **Step 2: Write failing tests**

```csharp
// UsageLimitTests
[Fact] public void Claude_jobs_share_one_key() // KeyFor(claude settings, any model/isolation) == "claude-code"
[Fact] public void Codex_keys_follow_the_sign_in() // ChatGpt → "codex:chatgpt"; ApiKey "openai-orders" → "codex:api-key:openai-orders"
[Fact] public void Keys_in_use_cover_stage_review_and_evidence_agents()
// ClaudeUsageLimitTests (now = 2026-10-28T10:00 local, zone Europe/London)
[Fact] public void Epoch_suffix_gives_reset()        // "Claude AI usage limit reached|1793210400" → ResetsAt == FromUnixTimeSeconds(1793210400)
[Fact] public void Resets_time_today_or_tomorrow()   // "...limit reached ∙ resets 3pm" → today 15:00 local; "resets 9am" → tomorrow 09:00
[Fact] public void Resets_with_named_zone()          // "resets 3pm (America/New_York)" → 15:00 in that zone, next occurrence
[Fact] public void Usage_limit_without_time_has_no_reset() // ResetsAt null
[Fact] public void Rate_limit_resets_in_five_minutes()     // "API Error: 429 {\"type\":\"rate_limit_error\"...}" → now + 5 min
[Fact] public void Overloaded_is_not_a_limit()             // "overloaded_error" / "529" → null
[Fact] public void Unrelated_error_is_not_a_limit()        // "agent exited with code 1" → null
// + one [InlineData] per real phrasing from Step 1
// CodexUsageLimitTests
[Fact] public void Try_again_in_duration()   // "You've hit your usage limit. ... Try again in 2 hours 5 minutes." → now + 2h5m
[Fact] public void Try_again_at_time()       // "...try again at 3:04 PM." → next 15:04 local
[Fact] public void Too_many_requests_is_rate_limit() // "exceeded retry limit, last status: 429 Too Many Requests" → now + 5 min
// Runner tests (existing fakes)
[Fact] public void Claude_failed_run_with_limit_text_sets_Limit()     // exit 1, stderr limit text → Succeeded false, Limit not null, Error = the text tail
[Fact] public void Claude_successful_run_mentioning_limit_has_no_Limit() // exit 0, structured output ok, result text contains "usage limit reached" → Limit null
[Fact] public void Codex_failed_run_with_limit_text_sets_Limit()
```

- [ ] **Step 3: Run; expect failures** — `dotnet test tests/Symphonist.Integrations.Tests --filter UsageLimit` and Core `--filter UsageLimitTests`.

- [ ] **Step 4: Implement.** Detection is case-insensitive regex over the given text; patterns for one provider live only in its `*UsageLimit` class. Runners call `Detect` only on their failure paths, over the CLI's error channels (Claude: the `result` text when `is_error`, and stderr; Codex: stderr and stdout of the failed run). Both runners take a last optional ctor parameter `TimeProvider? time = null` (default `TimeProvider.System`) for `now`, and use `TimeZoneInfo.Local`.

- [ ] **Step 5: Run Core and Integrations tests; all green.**

- [ ] **Step 6: Commit** — `feat(agents): recognise provider usage and rate limits`

---

### Task 2: Provider pauses that survive restarts

**Files:**
- Create: `src/Symphonist.Core/Storage/ProviderPause.cs`, `src/Symphonist.Host/Orchestration/ProviderLimits.cs`, migration `ProviderPauses`
- Modify: `src/Symphonist.Core/Storage/ITicketStore.cs`, `src/Symphonist.Integrations/Storage/SymphonistDbContext.cs`, `src/Symphonist.Integrations/Storage/SqliteTicketStore.cs`, `src/Symphonist.Host/Dashboard/NotifyingTicketStore.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeTicketStore.cs`
- Test: `tests/Symphonist.Host.Tests/Orchestration/ProviderLimitsTests.cs`, `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`

**Interfaces:**
- Consumes: `UsageLimit` (Task 1).
- Produces:
  - `public sealed record ProviderPause(string Key, DateTimeOffset Since, DateTimeOffset? ResetsAt, DateTimeOffset? NextProbeAt);`
  - `ITicketStore`: `Task<IReadOnlyList<ProviderPause>> ProviderPausesAsync(CancellationToken ct)`, `Task SaveProviderPauseAsync(ProviderPause pause, CancellationToken ct)` (upsert by key), `Task RemoveProviderPauseAsync(string key, CancellationToken ct)`.
  - `public sealed class ProviderLimits(ITicketStore store, TimeProvider time)`:
    - `public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(30);`
    - `Task LoadAsync(CancellationToken ct)`; `IReadOnlyList<ProviderPause> Pauses { get; }`
    - `Task<bool> TryStartAsync(string key, CancellationToken ct)` — true when not paused. Known reset: false before it; at or after it the pause is removed (raises `Resumed`) and true. Unknown reset: false before `NextProbeAt`; at or after it true once and `NextProbeAt = now + ProbeInterval` (persisted).
    - `Task PauseAsync(string key, UsageLimit limit, CancellationToken ct)` — create or replace; `NextProbeAt = ResetsAt is null ? now + ProbeInterval : null`; `Since` kept when already paused; raises `Paused` only when newly paused.
    - `Task SucceededAsync(string key, CancellationToken ct)` and `Task ResumeAsync(string key, CancellationToken ct)` — remove the pause if present, raise `Resumed`.
    - `event Action<ProviderPause>? Paused; event Action<string>? Resumed; event Action? Changed;` (`Changed` on every state change).

- [ ] **Step 1: Write failing tests** (FakeTimeProvider, FakeTicketStore)

```csharp
[Fact] public async Task Not_paused_starts()
[Fact] public async Task Known_reset_blocks_until_reset_then_lifts()   // Pause(reset now+2h): TryStart false at +1h59m; true at +2h; Pauses empty; Resumed raised once
[Fact] public async Task Unknown_reset_lets_one_probe_through_every_30_minutes() // false at +29m; true at +30m; false again right after; true at +60m
[Fact] public async Task Pause_survives_a_restart()   // pause, new ProviderLimits over the same store, LoadAsync → TryStart false before reset (Review Focus 5)
[Fact] public async Task Repeat_pause_keeps_since_and_raises_paused_once()
[Fact] public async Task Success_or_manual_resume_lifts_the_pause()
[Fact] public async Task Other_keys_are_unaffected() // "claude-code" paused, "codex:chatgpt" starts
// SqliteTicketStoreTests
[Fact] public async Task Provider_pauses_round_trip_and_upsert_by_key()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement** the store table (`ProviderPauses`, key primary, `DateTimeOffsetToBinaryConverter` on the dates), the migration, the fakes, and `ProviderLimits` (state cached in memory, every change written through the store; a `Lock` guards it).
- [ ] **Step 4: Run Integrations and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(orchestrator): persisted provider pauses`

---

### Task 3: Pause jobs on a usage limit instead of failing them

**Files:**
- Modify: `src/Symphonist.Core/Pipeline/PipelineEngine.cs`, `src/Symphonist.Host/Orchestration/StageExecutor.cs`, `src/Symphonist.Host/Orchestration/GateReviewer.cs`, `src/Symphonist.Host/Orchestration/EvidenceRunner.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `src/Symphonist.Host/Program.cs`, `src/Symphonist.Host/Api/TicketsApi.cs`, `src/Symphonist.Cli/Cli.cs`, `src/Symphonist.Cli/SymphonistClient.cs`, `src/Symphonist.Host/Dashboard/BoardModels.cs`, `src/Symphonist.Host/Dashboard/BoardQuery.cs`, `src/Symphonist.Host/Components/Shared/ProjectHeading.razor`
- Test: `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs`, `StageExecutorTests.cs`, `GateReviewerTests.cs`, `EvidenceRunnerTests.cs`, `tests/Symphonist.Host.Tests/Api/…`, `tests/Symphonist.Cli.Tests/CliTests.cs`, `tests/Symphonist.Host.Tests/Dashboard/BoardComponentTests.cs`

**Interfaces:**
- Consumes: `UsageLimits`, `AgentRunResult.Limit` (Task 1); `ProviderLimits` (Task 2).
- Produces:
  - `ExecutionStatus.UsageLimited`; `ExecutionResult(..., bool MergeStarted = false, UsageLimit? Limit = null, string? LimitKey = null)`.
  - `GateReviewOutcome(..., string? ReviewedBase = null, UsageLimit? Limit = null)`; `EvidenceStatus.UsageLimited`; `EvidenceOutcome` gains `UsageLimit? Limit = null` (last).
  - `PipelineEngine.StageLimited(Ticket t, string message, DateTimeOffset now) -> TransitionOutcome` — `Status = Pending`, `Attempts` unchanged, `UpdatedAt = now`, `Changed = true`, no tracker move.
  - `Orchestrator` ctor gains `ProviderLimits limits` (after `EvidenceRunner evidence`).
  - API: `GET /api/limits` → `ProviderPauseDto(string Key, string Provider, DateTimeOffset Since, DateTimeOffset? ResetsAt, DateTimeOffset? NextProbeAt)[]`; `POST /api/limits/resume` body `{ "key": "…" }` → 204, 404 when not paused.
  - CLI: `symphonist limits` (table: provider, key, since, resets / next try) and `symphonist limits resume <key>`.
  - `BoardProject` gains `IReadOnlyList<ProviderPause>? UsagePauses = null` (pauses whose key is in `UsageLimits.KeysInUse(p)`).

- [ ] **Step 1: Write failing tests**

```csharp
// OrchestratorTests (FakeAgentRunner returning Limit)
[Fact] public async Task Limited_stage_run_goes_back_to_pending_without_an_attempt()
// implement run returns Limit(reset +1h) → ticket Pending, Attempts 0, event "implement paused: Claude usage limit, resets 11:00"; limits.Pauses has "claude-code"
[Fact] public async Task Limited_key_starts_no_job_until_reset()       // second ticket Pending stays Pending (not Running) across ticks until +1h
[Fact] public async Task Other_provider_keeps_running()                 // stage override codex: runs while claude-code paused
[Fact] public async Task Limited_review_keeps_the_review_owed()        // review returns Limit → ReviewDue still true, no GateReview recorded, gate not auto-approved
[Fact] public async Task Limited_evidence_keeps_evidence_owed()
[Fact] public async Task Successful_probe_lifts_the_pause()            // unknown reset; at +30m one run starts and completes → Pauses empty
[Fact] public async Task Three_limited_runs_never_block()              // MaxAttemptsPerStage 3, three limited runs → still Pending, not Blocked
// API / CLI
[Fact] public async Task Limits_endpoint_lists_and_resumes()
[Fact] public async Task Cli_limits_prints_pauses()
// Board
[Fact] public void Heading_shows_usage_pause_with_reset_and_resume_button() // "Paused: Claude usage limit, resets 11:00"; unknown reset → "trying again every 30 minutes"
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement**
  - `StageExecutor.RunAgentAsync`: a result with `Limit` becomes `ExecutionResult(UsageLimited, result.Error, Limit: result.Limit, LimitKey: UsageLimits.KeyFor(settings))` (the agent run is still recorded).
  - `GateReviewer` / `EvidenceRunner`: a failed run with `Limit` returns early with it (agent run still recorded; no verdict guards, no evidence record).
  - `Orchestrator.AdvanceAsync`: after the concurrency check and before marking Running (stage), starting a review, or starting evidence, call `limits.TryStartAsync(UsageLimits.KeyFor(<that job's settings>))`; false → return without changes. Settings: stage `p.AgentFor(t.StageId)`, review `p.Review.Agent`, evidence `p.Evidence.Agent`.
  - Completion: a limited stage run → `PipelineEngine.StageLimited` + `limits.PauseAsync`; a limited review/evidence → event once, pause, nothing else changes. Any other result → `limits.SucceededAsync(key)`.
  - Message format (shared helper in Host `UsageLimitText.Describe(string key, DateTimeOffset? resetsAt, TimeZoneInfo zone)`): "Claude usage limit, resets HH:mm" (local; add the date when not today) or "Claude usage limit, trying again every 30 minutes".
  - `Program`: register `ProviderLimits`; `await LoadAsync` in `OrchestratorService` before `RecoverAsync`; `limits.Changed += notifier.Notify` (BoardNotifier).
  - Heading: one badge per pause plus a `Resume` button calling `ResumeAsync`.
- [ ] **Step 4: Run Host and Cli tests; all green.**
- [ ] **Step 5: Commit** — `feat(orchestrator): pause a provider on its usage limit`

---

### Task 4: Record touches

**Files:**
- Create: `src/Symphonist.Core/Storage/TouchRecord.cs`, migration `Touches`
- Modify: `src/Symphonist.Core/Pipeline/TransitionOutcome.cs`, `src/Symphonist.Core/Pipeline/PipelineEngine.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`, store files and fakes as in Task 2
- Test: `tests/Symphonist.Core.Tests/Pipeline/PipelineEngineTests.cs`, `OrchestratorTests.cs`, `SqliteTicketStoreTests.cs`

**Interfaces:**
- Produces:
  - `public enum TouchKind { SendBack, Unblock }`; `public sealed record TouchRecord(Guid TicketId, string ProjectId, string StageId, TouchKind Kind, DateTimeOffset At);`
  - `TransitionOutcome(..., GateDecisionInfo? Decision = null, TouchKind? Touch = null)`.
  - `ITicketStore.AddTouchAsync(TouchRecord touch, CancellationToken ct)`; `ITicketStore.TouchesAsync(string? projectId, DateTimeOffset since, CancellationToken ct) -> Task<IReadOnlyList<TouchRecord>>`; `DeleteAsync` also deletes the ticket's touches.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Send_back_from_gate_approved_or_blocked_is_a_touch() // SendBack(...) from each status → Touch == SendBack
[Fact] public void Unblock_is_a_touch()
[Fact] public void Deferred_card_move_back_is_a_touch()  // PendingSourceStatus of an earlier stage applied in StageSucceeded → Touch SendBack
[Fact] public void Stage_failure_and_merge_hold_are_not_touches()
// Orchestrator
[Fact] public async Task Touch_is_recorded_against_the_stage_it_left()   // send back implement → plan: TouchRecord StageId "implement"
[Fact] public async Task Teammate_pr_comment_rerun_is_not_a_touch()      // PR comment from another author (fromOthers true) → no TouchRecord
[Fact] public async Task Own_marker_comment_rerun_is_a_touch()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** `PipelineEngine.SendBack` and the deferred send-back in `StageSucceeded` set `Touch = SendBack`; `Unblock` sets `Touch = Unblock`. `Orchestrator.HandleClaudeCommentsAsync` clears `Touch` exactly where it clears `Decision` (teammate-only). `ApplyAsync` writes `TouchRecord(t.Id, t.ProjectId, fromStageId, kind, now)` when `Touch` is set.
- [ ] **Step 4: Run Core, Integrations and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(pipeline): record send-backs and unblocks as touches`

---

### Task 5: Touch stats on the board and a Stats page

**Files:**
- Create: `src/Symphonist.Core/Storage/TouchStats.cs`, `src/Symphonist.Host/Components/Pages/Stats.razor`
- Modify: `src/Symphonist.Core/Storage/ITicketStore.cs` (+ store, fakes), `src/Symphonist.Host/Dashboard/BoardModels.cs`, `BoardQuery.cs`, `src/Symphonist.Host/Components/Shared/ProjectHeading.razor`, `src/Symphonist.Host/Components/Layout/MainLayout.razor` (nav link "Stats")
- Test: `tests/Symphonist.Core.Tests/Storage/TouchStatsTests.cs`, `BoardQueryTests.cs`, `BoardComponentTests.cs`, new `tests/Symphonist.Host.Tests/Dashboard/StatsComponentTests.cs`

**Interfaces:**
- Consumes: `TouchRecord` (Task 4), `GateDecisionRecord`, `GateReview`, `GateAgreement.Latest`.
- Produces:
  - `ITicketStore.GateDecisionsAsync(string? projectId, DateTimeOffset since, CancellationToken ct)` and `ITicketStore.GateReviewsSinceAsync(string? projectId, DateTimeOffset since, CancellationToken ct)` (reviews whose round `WaitingSince >= since - 30 days`, enough to join decisions made since).
  - `public sealed record StageTouches(string ProjectId, string StageId, int SendBacks, int Unblocks, int Overrules, int ApprovedByYou, int AutoApproved, IReadOnlyList<int> WeeklyTouches);` (8-week totals; `WeeklyTouches` oldest first, length 8)
  - `public sealed record TouchSummary(int ThisWeek, int LastWeek, int SendBacks, int Unblocks, int Overrules, int AutoApproved, int Decided);` (this week's split; `AutoApproved` of `Decided` gate rounds this week)
  - `TouchStats.WeekStart(DateTimeOffset now, TimeZoneInfo zone) -> DateTimeOffset` (Monday 00:00 local)
  - `TouchStats.Compute(IEnumerable<TouchRecord>, IEnumerable<GateDecisionRecord>, IEnumerable<GateReview>, DateTimeOffset now, TimeZoneInfo zone, int weeks = 8) -> IReadOnlyList<StageTouches>`
  - `TouchStats.Summarize(IReadOnlyList<StageTouches> stages, …same inputs…) -> TouchSummary` (or computed alongside; one place).
  - `BoardProject` gains `TouchSummary? Touches = null` and `IReadOnlyList<StageTouches>? StageTouches = null` (hover text).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Week_starts_monday_local()       // Sunday 23:30 local belongs to the week starting the previous Monday
[Fact] public void Each_round_counts_by_its_latest_decision()   // approve then later send-back of the same round → counted once, as the latest
[Fact] public void Approving_a_high_is_a_touch_and_an_overrule()
[Fact] public void Sending_back_a_low_is_one_touch_and_an_overrule() // SendBacks 1, Overrules 1, weekly total +1 (not +2)
[Fact] public void Auto_approvals_are_not_touches()
[Fact] public void Weekly_buckets_cover_eight_weeks_oldest_first()
[Fact] public void Summary_compares_this_week_with_last()
// Board / Stats page
[Fact] public void Heading_shows_touch_strip_with_link_to_stats()  // "Touches this week: 4 (last week 9) · send-backs 2 · unblocks 1 · overrules 1 · auto-approved 6 of 11 gates"; link "/stats?project=orders"
[Fact] public void Stats_page_lists_stages_in_pipeline_order_with_trend() // done stage omitted; trend rendered with ▁▂▃▄▅▆▇█ scaled to the row's max (all zero → ▁ x8)
[Fact] public void Stats_page_filters_by_project()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** `BoardQuery` loads 8 weeks of data once per board load. Stats page at `/stats` (`[SupplyParameterFromQuery(Name = "project")]`), one table per project, columns: Stage, Send-backs, Unblocks, Overrules, Approved by you / auto, Trend. Use the existing table and badge styles.
- [ ] **Step 4: Run Core and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(dashboard): touch stats strip and stats page`

---

### Task 6: Notifier abstraction, Windows toasts and folding

**Files:**
- Create: `src/Symphonist.Host/Notifications/INotifier.cs`, `WindowsToastNotifier.cs`, `NullNotifier.cs`, `NotificationCenter.cs`, `toast.ps1`
- Modify: `src/Symphonist.Host/Symphonist.Host.csproj` (copy `Notifications/toast.ps1` to output, like `Containers/agent`), `src/Symphonist.Host/Program.cs`, `README.md` (env switch line only)
- Test: `tests/Symphonist.Host.Tests/Notifications/NotificationCenterTests.cs`, `WindowsToastNotifierTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DesktopNotification(string Title, string Body, Uri Link);`
  - `public interface INotifier { Task ShowAsync(DesktopNotification notification, CancellationToken ct); }` — implementations never throw (log instead).
  - `public enum NotificationKind { WaitingOnYou, Blocked, ReleaseReady, LimitPaused, LimitResumed }`
  - `public sealed record NotificationEvent(NotificationKind Kind, string? ProjectId, string? TicketKey, string Line);`
  - `public sealed class NotificationCenter(INotifier notifier, DashboardAddress address, Func<string, string> projectName, TimeProvider time, ILogger<NotificationCenter> log) : IDisposable` with `public static readonly TimeSpan FoldWindow = TimeSpan.FromSeconds(30)`, `void Post(NotificationEvent e)` (thread-safe, never throws), `Task FlushAsync()` (sends what is collected; used by the timer and tests), `static DesktopNotification Compose(IReadOnlyList<NotificationEvent> events, DashboardAddress address, Func<string, string> projectName)`. `Program` passes a lookup over `ProjectRegistry` that falls back to the id.
  - `WindowsToastNotifier(IProcessRunner runner, string scriptPath, ILogger<WindowsToastNotifier> log)`.
  - Env `SYMPHONIST_NOTIFICATIONS=off` → `NullNotifier`; otherwise Windows → `WindowsToastNotifier`, else `NullNotifier`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Single_event_gets_a_detailed_notification()
// WaitingOnYou orders ORD-412 "plan · reviewer says medium risk" → Title "ORD-412 is waiting on you", Body "Orders · plan · reviewer says medium risk",
// Link http://127.0.0.1:5199/tickets/orders/ORD-412
[Fact] public async Task Events_within_the_window_fold_into_one() // 2 waiting + 1 blocked → one notification: Title "3 tickets need you", Body "2 waiting on you · 1 blocked", Link board
[Fact] public async Task Ten_blocked_in_one_tick_send_one_notification() // Review Focus 4
[Fact] public async Task Nothing_is_sent_before_the_window_ends()  // FakeTimeProvider +29s → 0 sent; +30s → 1
[Fact] public async Task Duplicate_event_in_a_window_is_dropped()  // same (Kind, ProjectId, TicketKey) twice → counted once
[Fact] public void Project_events_link_to_the_filtered_board()     // ReleaseReady orders → http://127.0.0.1:5199/?project=orders
[Fact] public void Line_is_cut_to_200_characters()
[Fact] public async Task Toast_runs_powershell_with_json_on_stdin()
// FakeProcessRunner: FileName "powershell.exe", args ["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",scriptPath],
// StandardInput is JSON {title, body, link}; no argument contains the title or body
[Fact] public async Task Toast_failure_is_logged_not_thrown()
[Fact] public async Task Non_loopback_link_is_refused()  // Link https://example.com → not run, logged
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement**
  - Compose titles: WaitingOnYou "{key} is waiting on you"; Blocked "{key} is blocked"; ReleaseReady "{project name} has work ready to release"; LimitPaused "{Provider} usage limit reached"; LimitResumed "{Provider} jobs resumed". Body "{project name} · {line}" (via `projectName`; just the line when `ProjectId` is null). Folded: "{n} tickets need you" when every event has a ticket key, else "{n} updates from Symphonist"; body counts per kind joined with " · " ("2 waiting on you · 1 blocked · 1 ready to release · 1 usage limit").
  - `toast.ps1`: read one JSON object from stdin; register `HKCU:\Software\Classes\AppUserModelId\Symphonist.Host` with `DisplayName` "Symphonist" if missing; build a `ToastGeneric` XML with `[System.Security.SecurityElement]::Escape` on title and body and `activationType="protocol" launch="<link>"`; show it via `[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]::CreateToastNotifier("Symphonist.Host")`. Exit non-zero with a message on any error.
  - `WindowsToastNotifier` validates the link (scheme http, host 127.0.0.1) before launching; timeout 30 s.
  - `NullNotifier` logs "Desktop notifications are not available on this platform yet" once.
- [ ] **Step 4: Manual check on Windows**: run the script once by hand with a sample JSON and confirm a "Symphonist" notification appears and clicking it opens the URL. Record the result in the report.
- [ ] **Step 5: Run Host tests; all green.**
- [ ] **Step 6: Commit** — `feat(notifications): folded desktop notifications with a Windows toast notifier`

---

### Task 7: Notify on waiting gates, blocked tickets and usage limits

**Files:**
- Modify: `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `src/Symphonist.Host/Program.cs`
- Test: `OrchestratorTests.cs`

**Interfaces:**
- Consumes: `NotificationCenter` (Task 6), `ProviderLimits` events (Task 2), `PipelineEngine.WaitsOnReviewer`.
- Produces: `Orchestrator` ctor gains a last optional parameter `NotificationCenter? notifications = null`.

- [ ] **Step 1: Write failing tests** (a capturing `INotifier` + `NotificationCenter.FlushAsync`)

```csharp
[Fact] public async Task Human_gate_posts_waiting_on_you_once_per_round()  // stage succeeds → 1 event; evidence completing on the same round → no second event
[Fact] public async Task Auto_gate_posts_only_after_the_review_leaves_it_with_you() // ReviewDue: none; review high → WaitingOnYou
[Fact] public async Task Auto_approved_round_posts_nothing()
[Fact] public async Task Blocked_ticket_posts_blocked_with_reason() // Line == BlockedReason
[Fact] public async Task Limit_pause_and_resume_post_once_each()   // Paused → LimitPaused "Claude jobs paused, resets 11:00"; Resumed → LimitResumed
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** In `ApplyAsync` (outcome changed): post WaitingOnYou when `t.Status == AwaitingApproval && !PipelineEngine.WaitsOnReviewer(p, t)` and the ticket's last notified round (`Dictionary<Guid, DateTimeOffset>` under the gate) differs from `t.WaitingSince`; post Blocked when `t.Status == Blocked` and the ticket isn't already in the notified-blocked set (removed when it leaves Blocked). Line for WaitingOnYou: "{stage}" plus " · reviewer says {risk} risk" when `t.Risk` is set. Look up `ProjectConfig` with `projects.Projects.FirstOrDefault`; skip when missing. `Program` subscribes the center to `ProviderLimits.Paused/Resumed`.
- [ ] **Step 4: Run Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(notifications): notify on waiting gates, blocks and usage limits`

---

### Task 8: Release batch model in Core

**Files:**
- Modify: `src/Symphonist.Core/Pipeline/Ticket.cs`, `src/Symphonist.Core/Pipeline/PipelineEngine.cs`, `src/Symphonist.Core/Config/ProjectConfig.cs`, `src/Symphonist.Integrations/Storage/SymphonistDbContext.cs`, migration `ReleaseBatches`; every `.Gate` read that decides behaviour (grep `\.Gate\b` in `src/`: `PipelineEngine`, `ProjectConfig.Reviews/WantsEvidence`, `Orchestrator`, `GateReviewer`, `BoardQuery`)
- Test: `tests/Symphonist.Core.Tests/Pipeline/PipelineEngineTests.cs`, `tests/Symphonist.Core.Tests/Config/…`, `SqliteTicketStoreTests.cs`

**Interfaces:**
- Produces:
  - `public enum TicketKind { Work, Release }`; `Ticket.Kind` (default `Work`), `Ticket.ReleaseTicketId` (`Guid?`, a member's batch), `Ticket.ReleaseSummary` (`string?`, the release PR body; release tickets only).
  - `ProjectConfig.GateOf(StageDefinition stage) -> GatePolicy` — `Release` kind with `None` → `Human`; otherwise `stage.Gate`.
  - `ProjectConfig.ReleaseStage -> StageDefinition?` (the first `Release`-kind stage).
  - `PipelineEngine.CreateRelease(ProjectConfig p, int number, string title, string branch, string summary, DateTimeOffset now) -> Ticket` — `Kind = Release`, `Key = $"_release-{number}"`, `StageId = p.ReleaseStage!.Id`, `Status = Pending`, `Branch = branch`, `ReleaseSummary = summary`.
  - `PipelineEngine.IsReadyToRelease(ProjectConfig p, Ticket t) -> bool` — `Work` kind, at the release stage, `Pending`, `ReleaseTicketId` null, `ReleasePullRequestId` null.
  - `PipelineEngine.ReleaseCompleted(ProjectConfig p, Ticket member, string mergeCommit, DateTimeOffset now) -> TransitionOutcome` — clears `ReleaseTicketId`, sets `ReleaseMergeCommit`; if `PendingSourceStatus` maps to a stage before the release stage → send back there (message "released; sent back to {stage}: card moved to '{status}'", `Touch` not set: the move was already the developer's); otherwise enter the stage after the release stage, or Done when none.
  - `PipelineEngine.ReleaseCancelled(Ticket member, string releaseKey, DateTimeOffset now) -> TransitionOutcome` — clears `ReleaseTicketId`; message "{releaseKey} cancelled; ready to release again" (or "…; back to its own progress" when not at the release stage).
  - For `Kind == Release`: `SendBack` (any target) ends the release: `Status = Done`, `Feedback = feedback`, `WaitingSince = null`, `ReviewDue = false`, message "release cancelled", `Decision` SentBack for the waiting round, `Touch = SendBack`; `PostApprovalCompleted` sets `Status = Done` with message "release merged" instead of advancing.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Release_gate_none_is_human()      // GateOf(release, None) == Human; GateOf(implement, None) == None
[Fact] public void Release_ticket_waits_for_approval_even_with_gate_none() // CreateRelease + StageSucceeded → AwaitingApproval
[Fact] public void Ready_to_release_excludes_batched_and_legacy_tickets()
[Fact] public void Release_completed_moves_member_past_release()  // pipeline …release, deploy, done → member at deploy, ReleaseMergeCommit set
[Fact] public void Release_completed_without_later_stages_is_done()
[Fact] public void Release_completed_applies_a_card_moved_back()  // PendingSourceStatus = plan's status → member at plan
[Fact] public void Release_completed_ignores_a_card_moved_to_approved_status()
[Fact] public void Release_completed_finishes_an_also_shipped_member() // member at build → past release
[Fact] public void Send_back_of_release_ticket_cancels_it()        // Status Done, Decision SentBack, Touch SendBack
[Fact] public void Merged_release_ticket_is_done_not_advanced()
[Fact] public void Existing_tickets_load_as_work_kind()             // SQLite: migration default "Work"
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement**, including the migration (`Kind` string, default `"Work"`; `ReleaseTicketId`; `ReleaseSummary`) and routing behaviour-deciding `.Gate` reads through `GateOf`. `GateReview.Gate` records `GateOf(stage)`.
- [ ] **Step 4: Run Core, Integrations and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(pipeline): release batch model`

---

### Task 9: Release coordinator and orchestrator wiring

**Files:**
- Create: `src/Symphonist.Host/Orchestration/ReleaseCoordinator.cs`
- Modify: `src/Symphonist.Core/Integrations/IGitWorkspace.cs`, `src/Symphonist.Integrations/Git/GitWorkspace.cs`, `src/Symphonist.Core/Integrations/ISourceControl.cs`, `src/Symphonist.Integrations/Bitbucket/BitbucketSourceControl.cs`, `src/Symphonist.Integrations/GitHub/GitHubSourceControl.cs`, `src/Symphonist.Host/Orchestration/StageExecutor.cs`, `src/Symphonist.Host/Orchestration/GateReviewer.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `src/Symphonist.Host/Dashboard/TicketLinks.cs` (no issue link for release tickets), fakes (`FakeGitWorkspace`, `FakeSourceControl`)
- Test: new `tests/Symphonist.Host.Tests/Orchestration/ReleaseCoordinatorTests.cs`, `OrchestratorTests.cs`, `GitWorkspace` tests in Integrations, Bitbucket/GitHub source-control tests

**Interfaces:**
- Consumes: Task 8 model; `NotificationCenter` (Task 6).
- Produces:
  - `IGitWorkspace.FirstParentCommitsAsync(string workspace, string range, CancellationToken ct) -> Task<IReadOnlyList<string>>` (`git rev-list --first-parent {range}`, full hashes, newest first).
  - `ISourceControl.DeclinePullRequestAsync(long id, CancellationToken ct)` — Bitbucket `POST …/pullrequests/{id}/decline`; GitHub `PATCH …/pulls/{id}` `{"state":"closed"}`.
  - `public sealed record AlsoShipped(string Key, string StageId, StageStatus Status, bool Finishes);` (`Finishes` false for a ticket at an agent stage: it is being reworked)
  - `public sealed record ReleasePreview(string ProjectId, IReadOnlyList<string> Ready, IReadOnlyList<AlsoShipped> AlsoShipped, int OutsideCommits, string? MainHead, string? Unavailable);`
  - `public sealed record ReleaseStartResult(bool Started, string Message, string? ReleaseKey);`
  - `public sealed class ReleaseCoordinator(ITicketStore store, IGitWorkspace git, TimeProvider time, NotificationCenter? notifications = null)`:
    - `Task<ReleasePreview> PreviewAsync(ProjectConfig p, CancellationToken ct)`
    - `Task<ReleaseStartResult> StartAsync(ProjectConfig p, ReleasePreview confirmed, CancellationToken ct)`
    - `Task<IReadOnlyList<(Ticket Member, TransitionOutcome Outcome)>> CompleteAsync(ProjectConfig p, Ticket release, CancellationToken ct)` and `CancelAsync(...)` (same return) — the caller saves and applies each outcome.
    - `Task CheckReadyAsync(ProjectConfig p, CancellationToken ct)` — posts ReleaseReady when the project's ready set goes from empty to non-empty (first check after start only records the state).
  - `Orchestrator.PreviewReleaseAsync(string projectId, CancellationToken ct) -> Task<ReleasePreview>` and `Orchestrator.ReleaseNowAsync(string projectId, ReleasePreview confirmed, CancellationToken ct) -> Task<ReleaseStartResult>`, both under the orchestrator lock; `ReleaseNowAsync` requests a tick when started.

- [ ] **Step 1: Write failing tests**

```csharp
// ReleaseCoordinatorTests (FakeGitWorkspace with scripted first-parent commits)
[Fact] public async Task Preview_lists_ready_tickets_also_shipped_and_outside_commits()
// main..production commits: merge of ORD-1 (ready), merge of ORD-2 (at build), one unknown commit → Ready [ORD-1], AlsoShipped [ORD-2 build Finishes], OutsideCommits 1
[Fact] public async Task Reworked_ticket_is_also_shipped_but_not_finished() // ORD-3 merged, now sent back to implement → Finishes false
[Fact] public async Task Nothing_to_release_is_unavailable()        // no commits, no ready → Unavailable "nothing to release: production already contains main"
[Fact] public async Task No_release_stage_is_unavailable()
[Fact] public async Task Open_release_makes_release_now_unavailable() // and a second StartAsync is refused
[Fact] public async Task Start_refuses_when_the_preview_changed()   // a new merge since the preview → Started false, "the release changed; review it again"
[Fact] public async Task Start_creates_release_ticket_branch_and_members()
// release ticket _release-1, Branch "symphonist/release-1" pushed at MainHead; ORD-1 and ORD-2 get ReleaseTicketId; summary lists tickets and "Also shipped"
[Fact] public async Task Ready_notification_once_per_empty_to_nonempty()
// OrchestratorTests (end to end with fakes)
[Fact] public async Task Release_ticket_opens_pr_from_release_branch_waits_merges_and_finishes_members()
[Fact] public async Task Ticket_merged_after_release_now_is_not_in_the_batch()        // Review Focus 2: stays ready; PR source is the frozen branch
[Fact] public async Task Member_card_moved_to_approved_status_does_not_approve()       // Review Focus 3
[Fact] public async Task Member_card_moved_back_goes_to_that_stage_after_merge()
[Fact] public async Task Members_cards_move_to_gate_status_while_release_waits()
[Fact] public async Task Release_ticket_never_touches_the_tracker_with_its_own_key()   // FakeTracker saw no call with "_release-1"; not blocked as "left the query"
[Fact] public async Task Pr_merged_outside_symphonist_completes_the_batch()           // recorded as a human Approved decision
[Fact] public async Task Pr_closed_outside_symphonist_cancels_the_batch()
[Fact] public async Task Sending_back_the_release_declines_the_pr_and_returns_members()
[Fact] public async Task Legacy_ticket_with_open_release_pr_finishes_the_old_way()
[Fact] public async Task Auto_approval_comment_for_a_release_goes_on_the_pr()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement**
  - Preview: workspace `git.PrepareAsync(p, "_release", p.Repo.MainBranch, ct)`; resolve `refs/remotes/origin/{main}` (→ `MainHead`) and `refs/remotes/origin/{production}`; commits = `FirstParentCommitsAsync(ws, "{production}..{main}")`. Match a ticket when its `MergeCommit` equals a commit (full or ≥7-char prefix, ignoring case). Work tickets only; Done tickets never join; tickets already in a batch never join.
  - Start (under the lock): recompute the preview; refuse unless `Ready`, `AlsoShipped` keys and `OutsideCommits` and `MainHead` equal the confirmed ones. `n` = 1 + number of the project's release tickets. Create and push branch `symphonist/release-{n}` at `MainHead` (prepare workspace `_release-{n}` on that branch, which starts from origin main; verify its head equals `MainHead`, else refuse). Title "Release {n}: {keys}" (cut to 120 chars). Summary markdown: "Tickets:" list (`- KEY: title`), then "Also shipped:" (`- KEY (still at {stage})`, `- N commit(s) from outside Symphonist`) when any. Members = Ready + AlsoShipped with `Finishes`; each gets `ReleaseTicketId` and an event "in release {n}".
  - `StageExecutor.ExecuteAsync`, release stage: `Kind == Release` → open (or find) the PR from `t.Branch` to production with the summary as description; no tracker comment. `Kind == Work` without `ReleasePullRequestId` → never reached (the orchestrator skips it, below); legacy path unchanged.
  - `GateReviewer`: a release ticket's release diff is `origin/{production}...origin/{t.Branch}`; `ReviewedBranch` is `t.Branch`.
  - `Orchestrator`:
    - `AdvanceAsync`: a `Work` ticket at the release stage with no `ReleasePullRequestId` does nothing (ready or in a batch). A release ticket at `AwaitingApproval` with a PR first checks the PR state: Merged → `Approve` (human decision recorded) then the normal post-approval path; Declined → cancel as below.
    - `SyncAsync`: release tickets are never matched to sources and never blocked as "left the query".
    - `ApplyAsync` for a release ticket: never transitions or comments on the tracker with its key; a status target moves each member's card instead (same retry rules per member). When the outcome leaves it Done with `ReleaseMergeCommit` set → `CompleteAsync` and apply each member outcome; Done without one → decline the PR (best effort, event on failure) and `CancelAsync`.
    - `AutoApprovalCommentFailedAsync` for a release ticket comments on the release PR.
    - After each project's tick: `coordinator.CheckReadyAsync(p)`.
    - Members' cards follow the release ticket through the status-target mapping above (e.g. to `OnGateStatus` when it waits).
  - `TicketLinks.Issue` returns null for keys starting with `_`.
- [ ] **Step 4: Run Core, Integrations and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(release): release batches from a frozen release branch`

---

### Task 10: Release now in the dashboard, API and CLI

**Files:**
- Create: `src/Symphonist.Host/Components/Shared/ReleaseBanner.razor`
- Modify: `src/Symphonist.Host/Dashboard/BoardModels.cs`, `BoardQuery.cs`, `src/Symphonist.Host/Components/Pages/Board.razor`, `src/Symphonist.Host/Components/Pages/TicketDetail.razor`, `src/Symphonist.Host/Api/TicketsApi.cs`, `src/Symphonist.Cli/Cli.cs`, `src/Symphonist.Cli/SymphonistClient.cs`
- Test: `BoardComponentTests.cs`, `TicketDetailComponentTests.cs`, `BoardQueryTests.cs`, API tests, `CliTests.cs`

**Interfaces:**
- Consumes: `Orchestrator.PreviewReleaseAsync` / `ReleaseNowAsync`, `ReleasePreview` (Task 9).
- Produces:
  - `public sealed record ReleaseBanner(IReadOnlyList<string> ReadyKeys, string? OpenReleaseKey, Uri? OpenReleasePrUrl);` on `BoardProject` (`ReleaseBanner? Release = null`; null when the project has no release stage).
  - `TicketDetailModel` gains `IReadOnlyList<BoardCard>? ReleaseMembers = null`, `string? ReleaseSummary = null`, `string? ReleaseOf = null` (a member's release key).
  - API: `GET /api/projects/{id}/release` → `ReleasePreview`; `POST /api/projects/{id}/release` body `ReleasePreview` → 200 `ReleaseStartResult` when started, 409 with `ApiError(message)` when refused.
  - CLI: `symphonist release <project> [--yes]` — prints the preview, asks "Release N ticket(s) to {production}? [y/N]" on stdin unless `--yes`, then posts.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Banner_shows_ready_count_and_release_now()        // "3 tickets ready to release" + button
[Fact] public void Banner_shows_open_release_with_pr_link_instead()
[Fact] public async Task Release_now_shows_preview_then_confirm_starts()  // also-shipped and outside commits listed before Confirm
[Fact] public async Task Changed_preview_shows_the_refusal_and_reloads()
[Fact] public void Release_ticket_page_lists_members_summary_and_warns_on_moved_cards() // member with PendingSourceStatus → "ORD-4 was moved back to 'In Progress'"
[Fact] public void Release_ticket_send_back_reads_cancel_release()  // the send-back control offers one target, labelled "Cancel release"
[Fact] public void Member_page_links_its_release()
[Fact] public async Task Api_release_preview_and_start()  // 409 on a changed preview
[Fact] public async Task Cli_release_asks_before_starting() // "n" → nothing posted; "--yes" → posted
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** On the all-projects board the banner shows per project above the columns; on a filtered board inside the project heading. Release tickets render as normal cards (title "Release n: …").
- [ ] **Step 4: Run Host and Cli tests; all green.**
- [ ] **Step 5: Commit** — `feat(release): release now from the dashboard, API and CLI`

---

### Task 11: Reopened tickets, and pipelines without release

**Files:**
- Modify: `src/Symphonist.Core/Pipeline/PipelineEngine.cs`, `src/Symphonist.Host/Orchestration/Orchestrator.cs`
- Test: `PipelineEngineTests.cs`, `OrchestratorTests.cs`

**Interfaces:**
- Produces: `PipelineEngine.Reopen(ProjectConfig p, Ticket t, string newStatus, DateTimeOffset now) -> TransitionOutcome?` — null unless `t` is a Done `Work` ticket and some non-Done stage's `EntryStatuses` contains `newStatus`. Otherwise: enter that stage (as `EnterStage`), `Branch = $"symphonist/{t.Key}-{now:yyyyMMddHHmm}"`, and clear `PullRequestId`, `MergeCommit`, `ReleasePullRequestId`, `ReleaseMergeCommit`, `ReleaseTicketId`, `PrCommentsHandledAt`, `ClaudeCommentsHandledAt` (set to `now`), `TrackerRetryStatus`; message "reopened at {stage}: card moved to '{status}'". No touch.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Reopen_enters_the_matching_stage_with_a_fresh_branch()
[Fact] public void Reopen_returns_null_without_a_matching_entry_status()
[Fact] public void Release_tickets_never_reopen()
[Fact] public async Task Done_ticket_moved_back_by_qa_runs_again()   // tracker status → implement's entry status: Pending at implement, new branch prepared from main
[Fact] public async Task Done_ticket_moved_elsewhere_stays_done()   // existing event "does not reopen done tickets"
[Fact] public async Task Pipeline_without_release_finishes_when_the_pr_merges() // implement (human) → done: approve → merge → Done, tracker moved to done's status
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement** in `SyncAsync`'s Done branch: try `Reopen` first, fall back to today's event.
- [ ] **Step 4: Run Core and Host tests; all green.**
- [ ] **Step 5: Commit** — `feat(pipeline): restart tickets QA reopens`

---

### Task 12: Docs

**Files:**
- Modify: `README.md`, `samples/projects/orders.example.yaml`, `docs/superpowers/notes/feature-backlog.md`

- [ ] **Step 1: README** — sections "Releases" (batches, Release now, what else ships, two approvals, send back = cancel, cards never approve, frozen release branch), "Usage limits" (pause per provider and sign-in, resume rules, `symphonist limits`), "Touch stats", "Desktop notifications" (Windows only for now, the four events, folding, `SYMPHONIST_NOTIFICATIONS=off`), "Reopened tickets".
- [ ] **Step 2: Sample YAML** — the `release` stage comment: "release: tickets wait here; Release now (dashboard or `symphonist release orders`) opens one PR for all of them from a frozen copy of mainBranch; it always waits for your approval".
- [ ] **Step 3: Backlog** — add "Deferred from plan 7": scheduled release windows, the daily digest, per-ticket cost and time limits, macOS/Linux notifications, counting manual fixes as touches.
- [ ] **Step 4: Check the sample still loads** — `dotnet test tests/Symphonist.Core.Tests --filter Sample`.
- [ ] **Step 5: Commit** — `docs: release batches, usage limits, touch stats, notifications`
