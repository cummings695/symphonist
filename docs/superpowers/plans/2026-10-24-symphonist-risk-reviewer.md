# Risk Reviewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reviewer agent judges each gate against the project's risk areas, and the gate acts on that verdict:
- `gate: auto` stages auto-approve when the verdict is low risk.
- Shadow mode records verdicts while the developer still decides, and tracks agreement per stage.
- The production (release) gate gets its own assessment over the whole merged change.
- Every auto-approval posts a comment on the ticket.
- The dashboard, API and CLI show the risk badge, the gate reasoning and the agreement numbers.

**Architecture:**
- **Pure logic in `Symphonist.Core.Risk`:** verdict parsing (fails closed), risk-area path matching, the agreement maths and the auto-approval comment text.
- **`GateReviewer` (Host, a sibling of `StageExecutor`)** prepares the workspace, lists changed files and renders the `review` prompt. It runs the reviewer through `IAgentRunners` (resolved by `review.agent.type`, so Codex can plug in with plan 6) with a review-specific JSON schema and a read-only workspace, then returns a `GateVerdict`.
- **Gate rounds:** `PipelineEngine` gains `GatePolicy.AutoWhenLowRisk`, a `Ticket.ReviewDue` flag set when a reviewed gate starts waiting, and `ReviewCompleted`. A gate round is identified by `(StageId, WaitingSince)`.
- **`Orchestrator`** starts review runs as in-flight work, the same way it starts agent stages (same concurrency slots), and applies their verdicts under the lock.
- **Decisions:** every human or automatic gate decision is stored as its own row and joined to the review of the same round. Agreement therefore counts even when the developer decides before the reviewer finishes.

**Tech Stack:** .NET 10, EF Core + SQLite (migrations via the local `dotnet-ef` tool), `Microsoft.Extensions.FileSystemGlobbing` (risk-area globs), System.Text.Json, Blazor Server, System.CommandLine, xUnit, bUnit, FakeTimeProvider.

**Spec:** `docs/superpowers/specs/2026-10-21-symphonist-design.md`, especially:
- §4 "Risk" and "Merging and release".
- §5.5 "Gates and risk" and §5.6 (risk areas).
- §6 (output contract, `{risk.areas}`).
- §8 features 1 (shadow mode), 5 (second-opinion reviewer) and 7 (guardrails).
- §9 (risk badge, gate reasoning, shadow stats).

Roadmap item 3 is in `docs/superpowers/plans/2026-10-21-symphonist-walking-skeleton.md`. The dashboard items deferred to this plan are listed under "Out of scope" in `docs/superpowers/plans/2026-10-22-symphonist-dashboard.md`.

## Scope of this plan

**In scope:**
- Risk areas (paths + description).
- The `review` config: mode `off | shadow | active`, and the reviewer agent.
- `gate: auto`.
- Reviewer runs for triage, plan, implement (PR) and release (production) gates.
- The deterministic risk-area path guard.
- Auto-approval comments.
- Stored gate reviews and decisions, and agreement per stage.
- The risk badge and "Reviewing" label, gate reasoning on the detail page, and agreement on the project bar.
- API/CLI fields.
- The `{{risk.areas}}` placeholder in every prompt.

**Out of scope:**
- The Codex runner (plan 6). Only the seam is built here: `IAgentRunners` and `review.agent.type`.
- Agent-proposed risk areas at onboarding (plan 4).
- Evidence runs (plan 5).
- "Save the lesson" on a risk override (plan 5).
- Release-cadence windows (plan 7).
- Touch stats (plan 7).

**Picked up from the notes:**
- `feature-backlog.md`, the `@claude` item ("deciding 'the answers are sufficient, move on' automatically belongs with the risk reviewer"). A triage gate set to `auto` gets reviewed again after every `@claude` rerun, and moves on when the reviewer says low risk (Task 7).
- `2026-10-23-dashboard-deferred.md`, "Approve on a re-run gate" (worth doing soon). Dashboard approve carries the round's `WaitingSince`, which is the same round identity the reviewer uses (Task 7).
- `2026-10-22-walking-skeleton-deferred.md`, "`Block` keeps a stale `WaitingSince`". `Block` now clears `WaitingSince` and `ReviewDue` (Task 4).
- `2026-10-22-walking-skeleton-deferred.md`, "the sample test asserts little". The sample test now asserts the review and risk sections (Task 1).

## Plan-level decisions (builder's calls)

- **Which gates have a reviewer.** Reviews run only at gates of stage kinds `triage`, `plan`, `implement` and `release`, and only when `review.mode` is not `off`. The loader rejects `gate: auto` on any other kind (`build`, `uat`, `deploy`, `done`). Why: those gates have no agent-readable output to judge.
- **Modes.**
  - `off`: no reviewer runs, and `auto` gates behave as `human` (fail closed).
  - `shadow`: the reviewer judges every reviewable gate, but nothing is auto-approved.
  - `active`: `auto` gates auto-approve on low risk. `human` gates are still reviewed, so agreement keeps accumulating.
- **Default mode.** When `review` is absent, the mode is `off` (developer's decision, 2026-10-24): no review runs and no extra agent cost until a project opts in with `mode: shadow`. Shadow still comes before auto-approve: the developer turns on auto-approval explicitly, per project with `mode: active` and per stage with `gate: auto`.
- **Production auto-approval is the release stage's gate policy.** `release` with `gate: auto` auto-approves; `gate: human` turns it off (e.g. for a project with UAT). There is no separate flag. The sample keeps `release: human`. Why: the spec's own example ("To prod: always human") is a per-stage gate policy.
- **What counts as low risk.** The verdict is low only if all of these hold:
  - The reviewer returned `risk: "low"` with at least one `checked` item.
  - The reviewer named no `risk_areas_touched`.
  - For implement and release gates, no changed file matches any risk area's `paths`.
  - The run succeeded and its output parsed.

  Anything else is high, and the reason is recorded. Why: the spec says "when unsure, the reviewer must return `high`", and an agent's word alone must not clear a risk-area path.
- **Each gate is judged on its own verdict.** An earlier high does not force later gates human. The reviewer prompt does list earlier verdicts and decisions (`{{review.history}}`). The badge shows the latest verdict. Why: spec §5.5 says risk "can rise"; per-gate judgment covers that, and doesn't keep stopping a ticket whose earlier concern the developer already cleared.
- **The production assessment** reviews `origin/{productionBranch}...origin/{mainBranch}`, the whole change about to ship, including other tickets released with it. Its history lists the build result (merge commit) and the stages the ticket has passed, including any UAT stage.
- **A failed review doesn't retry.**
  - A reviewer run that fails, times out or returns unparseable output gives a high verdict. One event is recorded (`"reviewer failed: {error}; waiting on you"`), and the card waits on the developer.
  - A host restart during a review reruns it, because `ReviewDue` is still set.
  - Why: the anti-babysitting goal and the guardrails rule out retry storms.
- **The reviewer cannot write.**
  - In container mode the workspace is mounted read-only.
  - In host mode, the reviewer's `allowedTools` default to exactly `Read, Grep, Glob` (no shell: `git diff --output` can write files). A read-only host run with any other tool is refused, and every read-only run also gets `--disallowedTools Write,Edit,NotebookEdit,Bash`. The review prompt carries the diff.
  - Symphonist never commits after a review.
- **Reviewer agent.** `review.agent` overrides `type`, `model`, `timeoutMinutes` (default 20) and `allowedTools` (host mode). It inherits isolation, image and `allowedDomains` from `agent`. Only `claude-code` is accepted until plan 6.
- **Who posts the auto-approval comment, and its text.** Symphonist posts it under the developer's account, signed with the usual footer, before approving. The text is fixed by `AutoApprovalComment.Format` (Task 2). If the comment can't be posted, the gate is **not** auto-approved: the card waits on the developer, with the event `"auto-approval held: could not post comment: {error}; waiting on you"`. Why: the spec says "every auto-approval posts a comment". No comment is posted for shadow or high verdicts; the dashboard shows those.
- **What "agreement" means.** Only the developer's own decisions count:
  - low + approved: agree.
  - high + sent back: agree.
  - low + sent back: a "false low", reported separately, because that is the case that matters before turning on `auto`.
  - high + approved: disagree.

  Auto-approved rounds are counted separately as "auto-approved". A send-back from `Blocked` or `Approved` is not a gate decision.
- **"Waiting on you"** is false while an `auto` gate in `active` mode is still being reviewed. The card shows "Reviewing" instead. In shadow mode the card waits on the developer from the start.
- **Review runs are agent runs.** They take a slot from `MaxConcurrentAgentRuns`, are recorded as `AgentRunRecord` with `IsReview = true`, and count toward the ticket's cost.

## Global Constraints

- .NET 10 (`net10.0`) for every project.
- Windows, macOS, and Linux supported in v1. No Windows-only APIs in the core; platform-specific pieces sit behind small abstractions. Paths, shells, line endings, and process launching must work on all three. CI builds and tests on all three.
- Launch every process with `ProcessStartInfo.ArgumentList` (never a shell string); build paths with `Path.Combine`.
- Acts as the developer: tickets assigned to them, actions under their accounts. Symphonist's tracker comments are signed with `TrackerComments.Footer` ("— posted by Symphonist").
- "When unsure, the reviewer must return `high`." Every error path in the reviewer yields high, never low.
- "Every auto-approval posts a short comment on the source ticket: what was checked and why it passed."
- "The production gate runs its own risk assessment over the whole change as merged (not just reusing the PR verdict), plus the build/UAT results."
- Agent output comes only from schema-validated structured output, never from free text.
- Ticket text, agent summaries, reasons and events are rendered as text only in the dashboard (no `MarkupString`). Status and risk are never shown by color alone: badges carry the words "Low risk" / "High risk" / "Reviewing".
- Local API bound to localhost only; `/api/*` requires `X-Symphonist-Token`.
- Tests use a temp `SYMPHONIST_HOME`; never touch the real `~/.symphonist`.
- `Orchestrator.cs` may have changed under plan 3a: reference methods by name (`SyncAsync`, `AdvanceAsync`, `ApplyAsync`, `CompleteAgentStageAsync`, `CommandAsync`), not line numbers.
- Every commit message ends with the `Claude-Session:` trailer line given in the dispatch.

## Review Focus

1. **The developer approves or sends back while the reviewer is still running.** Expect:
   - The human decision takes effect at once.
   - The late verdict is stored but not applied (event `"review of {stage} finished after the gate was decided; not applied"`).
   - The round still counts toward agreement.

   → test `Human_decision_during_review_wins_and_still_counts` (Task 7).
2. **The reviewer fails, times out, returns garbage, or says low with no evidence.** Expect a high verdict, the card waiting on the developer, one event, and no second review run in later ticks. → tests `Review_failure_fails_closed_without_retry` (Task 7), and `Low_without_checked_items_is_high` / `Missing_output_is_high` (Task 2).
3. **The PR's diff touches a risk-area path but the reviewer says low.** Expect a high verdict with the reason `"touches risk area 'pricing': src/Pricing/Calc.cs"`, and no auto-approval. → test `Risk_area_path_forces_high_even_when_reviewer_says_low` (Task 6).
4. **The ticket is sent back to the same gate and waits again, and then a verdict or a dashboard click from the earlier round arrives.** Expect neither to approve the new round. → tests `Stale_review_of_earlier_round_is_not_applied` (Task 4) and `Approve_with_stale_waiting_since_changes_nothing` (Task 7).
5. **Jira is down when an auto-approval comment must be posted.** Expect the gate not to be approved, the card waiting on the developer, and the event `"auto-approval held: …"`. → test `Auto_approval_held_when_comment_fails` (Task 7).

---

## File Structure

```
src/Symphonist.Core/Config/ProjectConfig.cs              (modify) GatePolicy.AutoWhenLowRisk, RiskArea, ReviewMode, ReviewConfig, ProjectConfig.RiskAreas/Review
src/Symphonist.Core/Config/ProjectConfigLoader.cs        (modify) riskAreas, review, gate auto
src/Symphonist.Core/Risk/RiskLevel.cs                    (create) RiskLevel, GateDecision
src/Symphonist.Core/Risk/GateVerdict.cs                  (create) GateVerdict, GateReviewSchema, GateVerdictParser
src/Symphonist.Core/Risk/RiskAreaMatcher.cs              (create) glob matching of changed files
src/Symphonist.Core/Risk/AutoApprovalComment.cs          (create) comment text
src/Symphonist.Core/Risk/ReviewAgreement.cs              (create) StageAgreement, DecidedReview, Compute
src/Symphonist.Core/Agents/IAgentRunner.cs               (modify) OutputSchema, ReadOnlyWorkspace, Structured; IAgentRunners/AgentRunners
src/Symphonist.Core/Pipeline/{Ticket,TransitionOutcome,PipelineEngine}.cs (modify) ReviewDue, Risk, GateDecisionInfo, ReviewCompleted
src/Symphonist.Core/Storage/ITicketStore.cs              (modify) GateReview, GateDecisionRecord, new store methods, AgentRunRecord.IsReview
src/Symphonist.Core/Integrations/IGitWorkspace.cs        (modify) ChangedFilesAsync
src/Symphonist.Core/Prompts/{PromptLibrary,PromptRenderer}.cs, Defaults/review.md, Defaults/plan.md (modify/create)
src/Symphonist.Integrations/ClaudeCode/ClaudeCodeRunner.cs (modify) schema per request, Structured
src/Symphonist.Integrations/Containers/DockerLauncher.cs (modify) read-only workspace mount
src/Symphonist.Integrations/Git/GitWorkspace.cs          (modify) ChangedFilesAsync
src/Symphonist.Integrations/Storage/{SymphonistDbContext,SqliteTicketStore}.cs, Migrations/<stamp>_RiskReviewer.cs
src/Symphonist.Host/Orchestration/GateReviewer.cs        (create)
src/Symphonist.Host/Orchestration/{Orchestrator,StageExecutor}.cs (modify)
src/Symphonist.Host/Program.cs                           (modify) IAgentRunners, GateReviewer
src/Symphonist.Host/Dashboard/{BoardModels,BoardQuery,NotifyingTicketStore}.cs (modify)
src/Symphonist.Host/Components/Shared/{TicketCard,ProjectBar}.razor, Pages/{Board,TicketDetail}.razor, wwwroot/app.css (modify)
src/Symphonist.Host/Api/TicketsApi.cs                    (modify) Risk, Reviews, Review/Agreement on projects
src/Symphonist.Cli/{StatusTable,SymphonistClient}.cs     (modify) risk column, review in show, agreement in projects
samples/projects/orders.example.yaml, README.md          (modify)
tests/...                                                 (see each task)
```

---

### Task 1: Risk areas, review config and `gate: auto`

**Files:**
- Modify: `src/Symphonist.Core/Config/ProjectConfig.cs`, `src/Symphonist.Core/Config/ProjectConfigLoader.cs`, `samples/projects/orders.example.yaml`.
- Test: `tests/Symphonist.Core.Tests/Config/ProjectConfigLoaderTests.cs`, `tests/Symphonist.Core.Tests/Config/SampleConfigTests.cs`.

**Interfaces:**
- Produces:

```csharp
public enum GatePolicy { None, Human, AutoWhenLowRisk }        // YAML: none | human | auto (also accepts autoWhenLowRisk)
public enum ReviewMode { Off, Shadow, Active }
public sealed record RiskArea(string Name, string Description, IReadOnlyList<string> Paths);
/// <param name="PromptPath">Absolute path of a review prompt override, or null for the embedded default.</param>
public sealed record ReviewConfig(ReviewMode Mode, AgentSettings Agent, string? PromptPath)
{
    public static readonly IReadOnlyList<string> ReadOnlyTools = ["Read", "Grep", "Glob"];
}
// ProjectConfig: init properties (existing positional constructor calls keep compiling)
public IReadOnlyList<RiskArea> RiskAreas { get; init; } = [];
public ReviewConfig Review { get; init; }   // default when constructed directly: Mode Off, Agent = the project's Agent
public static bool IsReviewable(StageKind kind);   // Triage, Plan, Implement, Release
public bool Reviews(StageDefinition stage);        // Review.Mode != Off && stage.Gate != None && IsReviewable(stage.Kind)
```

The default `Review` needs the project's `Agent`. The implementer chooses how: a computed fallback in the getter, or set in the loader and in test builders. Either way, existing direct constructions must end up with `Mode = Off`.

YAML shape (all optional):

```yaml
riskAreas:
  - name: pricing
    description: Anything that changes how order prices, discounts or tax are computed.
    paths: ["src/Pricing/**", "src/Orders/Discounts/**"]
review:
  mode: shadow            # off (default) | shadow | active
  prompt: prompts/review.md
  agent: { type: claude-code, model: "", timeoutMinutes: 20, allowedTools: [] }
```

Loader rules:
- `riskAreas[].name` and `description` are required, and names must be unique. `paths` is optional, and blank items are rejected.
- Missing `review` means `Mode = Off`.
- `review.agent.type` must be `claude-code` (`unsupported review.agent.type 'codex'`).
- Review `AgentSettings` = the project agent with `Type`, `Model`, `TimeoutMinutes` (default 20) and `AllowedTools` (default `ReadOnlyTools`) overridden.
- `gate: auto` on a non-reviewable kind fails with `stage 'build': gate 'auto' is only supported for triage, plan, implement and release`.
- The `review.prompt` path resolves relative to the config directory, like `prompts`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Risk_areas_and_review_are_parsed() { /* pricing area: Name, Description, Paths ["src/Pricing/**"]; Review.Mode Active; Review.Agent.Model "opus"; Review.Agent.TimeoutMinutes 20; Isolation/Image/AllowedDomains copied from agent */ }
[Fact] public void Review_defaults_to_off_with_read_only_tools() { /* no review section -> Mode Off, Agent.AllowedTools == ReviewConfig.ReadOnlyTools, PromptPath null */ }
[Theory] [InlineData("auto")] [InlineData("autoWhenLowRisk")]
public void Auto_gate_is_parsed(string gate) { /* implement gate -> GatePolicy.AutoWhenLowRisk */ }
[Fact] public void Auto_gate_on_build_is_rejected() { /* ProjectConfigException contains "gate 'auto' is only supported for triage, plan, implement and release" */ }
[Fact] public void Unsupported_review_agent_is_rejected() { /* type codex -> "unsupported review.agent.type 'codex'" */ }
[Fact] public void Duplicate_or_incomplete_risk_area_is_rejected() { /* duplicate name -> "duplicate risk area 'pricing'"; missing description -> "missing required 'riskAreas[].description'" */ }
[Fact] public void Reviews_only_gated_reviewable_stages() { /* Mode Shadow: triage(human) true, build(none) false, release(human) true; Mode Off: all false */ }
// SampleConfigTests (strengthened)
[Fact] public void Sample_config_loads_with_review_and_risk_areas() { /* existing load + RiskAreas.Count >= 1, Review.Mode Off, every stage's Gate is None or Human, implement has OnGateStatus "In Review" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter "ProjectConfigLoaderTests|SampleConfigTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Sample config:
  - Add a commented-out `riskAreas` example with one `pricing` entry.
  - Add `review: { mode: off }`, with comment lines explaining the three modes (off is the default; set `shadow` to start collecting verdicts) and that `gate: auto` needs `mode: active`.
  - Extend the `gate:` comment block with `auto = approve automatically when the reviewer judges low risk (triage, plan, implement, release only)`.
  - Keep every sample gate `human`.
- [ ] **Step 4: Run** the same tests, then `dotnet build Symphonist.slnx`. Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat(config): risk areas, review mode and auto gates`

---

### Task 2: Verdicts, risk-area matching, agreement and comment text (pure logic)

**Files:**
- Create: `src/Symphonist.Core/Risk/{RiskLevel,GateVerdict,RiskAreaMatcher,AutoApprovalComment,ReviewAgreement}.cs`.
- Modify: `src/Symphonist.Core/Symphonist.Core.csproj` (add `Microsoft.Extensions.FileSystemGlobbing`).
- Test: `tests/Symphonist.Core.Tests/Risk/{GateVerdictParserTests,RiskAreaMatcherTests,AutoApprovalCommentTests,ReviewAgreementTests}.cs`.

**Interfaces:**
- Consumes: `RiskArea` (Task 1).
- Produces:

```csharp
namespace Symphonist.Core.Risk;
public enum RiskLevel { Low, High }
public enum GateDecision { Approved, SentBack }

public sealed record GateVerdict(RiskLevel Risk, string Summary, IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Checked, IReadOnlyList<string> AreasTouched, string? Error = null)
{
    public static GateVerdict Failed(string error);                      // High, Summary "reviewer failed: {error}", Reasons [error]
    public GateVerdict WithForcedHigh(string reason);                    // High, reason appended to Reasons
}

public static class GateReviewSchema { public const string Json = /* see below */; }

public static class GateVerdictParser
{
    /// Fails closed: anything but a well-formed, evidenced "low" is High (with a reason saying why).
    public static GateVerdict Parse(JsonElement? structured);
}

public static class RiskAreaMatcher
{
    /// (area name, file) for every changed file matching an area's Paths; '/'-separated, case-insensitive globs.
    public static IReadOnlyList<(string Area, string File)> Touched(IReadOnlyList<RiskArea> areas, IEnumerable<string> files);
}

public static class AutoApprovalComment { public static string Format(string stageId, GateVerdict verdict); } // unsigned; caller signs

public sealed record DecidedReview(string ProjectId, string StageId, RiskLevel Risk, GateDecision Decision);
public sealed record StageAgreement(string ProjectId, string StageId, int Decided, int Agreed, int FalseLows, int AutoApproved)
{
    public double? Rate => Decided == 0 ? null : (double)Agreed / Decided;
}
public static class ReviewAgreement
{
    public static IReadOnlyList<StageAgreement> Compute(IEnumerable<DecidedReview> human, IEnumerable<(string ProjectId, string StageId)> autoApproved);
}
```

Schema (exact):

```json
{"type":"object","properties":{"risk":{"type":"string","enum":["low","high"]},"summary":{"type":"string"},"reasons":{"type":"array","items":{"type":"string"}},"checked":{"type":"array","items":{"type":"string"}},"risk_areas_touched":{"type":"array","items":{"type":"string"}}},"required":["risk","summary","reasons","checked"]}
```

Parse rules, in order. Blank strings in arrays are dropped.
- Null input or a non-object gives `Failed("reviewer returned no structured output")`.
- A `risk` other than `"low"`/`"high"` gives High with the reason `"unrecognised risk '{x}'"`.
- `"low"` with no `checked` items gives High with the reason `"reviewer reported low risk without saying what it checked"`.
- `"low"` with `risk_areas_touched` non-empty gives High with the reason `"reviewer named risk areas touched: {a, b}"`.

Comment text (exact):

```
Auto-approved {stageId}: low risk.

Checked:
- {checked[0]}
- …

Why it passed: {summary}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Evidenced_low_is_low() { /* {"risk":"low","summary":"s","reasons":["r"],"checked":["c"]} -> Low, Checked ["c"] */ }
[Fact] public void High_is_high() { }
[Fact] public void Low_without_checked_items_is_high() { /* reason contains "without saying what it checked" */ }
[Fact] public void Low_naming_touched_areas_is_high() { }
[Fact] public void Missing_output_is_high() { /* Parse(null) -> High, Error "reviewer returned no structured output" */ }
[Fact] public void Unknown_risk_value_is_high() { }
[Theory]
[InlineData("src/Pricing/Calc.cs", true)] [InlineData("src/pricing/calc.cs", true)] [InlineData("src/Orders/Api.cs", false)]
public void Matches_area_globs(string file, bool touched) { /* area pricing paths ["src/Pricing/**"] */ }
[Fact] public void Area_without_paths_never_matches() { }
[Fact] public void Backslash_paths_are_normalised() { /* "src\\Pricing\\Calc.cs" matches */ }
[Fact] public void Comment_lists_checks_and_reason() { /* exact text above for stage "implement", checked ["tests pass","no pricing files"], summary "Docs-only change" */ }
[Fact] public void Agreement_counts_agree_disagree_and_false_lows() { /* implement: low+Approved, low+SentBack, high+SentBack, high+Approved -> Decided 4, Agreed 2, FalseLows 1; Rate 0.5 */ }
[Fact] public void Auto_approved_rounds_are_counted_separately() { /* two auto for plan -> plan row Decided 0, AutoApproved 2, Rate null */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter "FullyQualifiedName~Risk"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Use `Matcher(StringComparison.OrdinalIgnoreCase)` with `Match(file)` per area (`Microsoft.Extensions.FileSystemGlobbing`). Output rows are ordered by project, then stage, ordinally.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(risk): fail-closed gate verdicts, risk-area matching and agreement`

---

### Task 3: Agent runs with a per-request schema, a read-only workspace and a runner registry

**Files:**
- Modify: `src/Symphonist.Core/Agents/IAgentRunner.cs`, `src/Symphonist.Integrations/ClaudeCode/ClaudeCodeRunner.cs`, `src/Symphonist.Integrations/Containers/DockerLauncher.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeAgentRunner.cs`.
- Test: `tests/Symphonist.Integrations.Tests/ClaudeCode/ClaudeCodeRunnerTests.cs`, `tests/Symphonist.Integrations.Tests/Containers/DockerLauncherTests.cs`, `tests/Symphonist.Core.Tests/Agents/AgentRunnersTests.cs`.

**Interfaces:**
- Consumes: `GateReviewSchema.Json` (Task 2), used in tests only.
- Produces:

```csharp
public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, AgentSettings Settings,
    string OutputSchema = StageOutputSchema.Json, bool ReadOnlyWorkspace = false);
public sealed record AgentRunResult(bool Succeeded, StageOutput? Output, decimal? CostUsd, TimeSpan Duration,
    string? Error, string RawLog, JsonElement? Structured = null);

public interface IAgentRunners { IAgentRunner For(string agentType); }   // NotSupportedException "no agent runner for '{type}'"
public sealed class AgentRunners(IReadOnlyDictionary<string, IAgentRunner> runners) : IAgentRunners;
// FakeAgentRunner additions
public static AgentRunResult Review(string risk, string summary = "reviewed", string[]? reasons = null, string[]? checkedItems = null, string[]? areas = null); // Structured set, Output null
public static bool IsReview(AgentRunRequest r) => r.OutputSchema == GateReviewSchema.Json;
```

ClaudeCodeRunner rules:
- Pass `request.OutputSchema` to `--json-schema`.
- Always return `Structured` = a clone of `structured_output` when it is an object.
- When the schema is `StageOutputSchema.Json`, behavior is unchanged.
- For any other schema, `Succeeded` = exit 0, not `is_error`, and `Structured` present. `Output` is null.

DockerLauncher, when `ReadOnlyWorkspace` is set: mount `{ws}:/workspace:ro` and omit the separate `.git` mount. Everything else (firewall, env, uid) is unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Custom_schema_is_passed_and_structured_output_returned() { /* request OutputSchema = GateReviewSchema.Json; args after "--json-schema" == that schema; structured {"risk":"low",...} -> Succeeded, Output null, Structured.risk == "low", cost read */ }
[Fact] public async Task Custom_schema_without_structured_output_fails() { /* -> Succeeded false, Error "missing structured output" */ }
[Fact] public async Task Stage_schema_behaviour_unchanged() { /* default request -> args contain StageOutputSchema.Json; Output parsed as before */ }
[Fact] public void Read_only_request_mounts_workspace_read_only() { /* args contain "{ws}:/workspace:ro" and no "/workspace/.git:ro" mount */ }
[Fact] public void Default_request_keeps_writable_workspace_and_read_only_git() { }
[Fact] public void Unknown_agent_type_is_reported() { /* AgentRunners({claude-code}).For("codex") -> NotSupportedException "no agent runner for 'codex'" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter "ClaudeCodeRunnerTests|DockerLauncherTests"` and `dotnet test tests/Symphonist.Core.Tests --filter AgentRunnersTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**, then the full `dotnet test Symphonist.slnx`. Expected: PASS. The default parameters keep the existing call sites unchanged.
- [ ] **Step 5: Commit** `feat(agents): per-request output schema, read-only workspace, runner registry`

---

### Task 4: Pipeline engine: auto gates, review rounds and gate decisions

**Files:**
- Modify: `src/Symphonist.Core/Pipeline/Ticket.cs`, `src/Symphonist.Core/Pipeline/TransitionOutcome.cs`, `src/Symphonist.Core/Pipeline/PipelineEngine.cs`.
- Test: `tests/Symphonist.Core.Tests/Pipeline/PipelineEngineTests.cs`.

**Interfaces:**
- Consumes: `ProjectConfig.Reviews(stage)` and `ReviewMode` (Task 1); `GateVerdict`, `RiskLevel` and `GateDecision` (Task 2).
- Produces:

```csharp
// Ticket
public bool ReviewDue { get; set; }          // a reviewer run is owed for the current gate round
public RiskLevel? Risk { get; set; }         // latest verdict (badge)
// TransitionOutcome
public sealed record TransitionOutcome(bool Changed, string? SourceStatusToSet, string Message, GateDecisionInfo? Decision = null);
public sealed record GateDecisionInfo(string StageId, DateTimeOffset WaitingSince, GateDecision Decision, bool Auto);
// PipelineEngine
public static bool WouldAutoApprove(ProjectConfig p, Ticket t, GateVerdict v, DateTimeOffset waitingSince);
    // AwaitingApproval && t.WaitingSince == waitingSince && t.ReviewDue && gate Auto && Mode Active && v.Risk Low
public static TransitionOutcome ReviewCompleted(ProjectConfig p, Ticket t, GateVerdict v, DateTimeOffset waitingSince,
    bool allowAutoApprove, DateTimeOffset now);
public static bool WaitsOnReviewer(ProjectConfig p, Ticket t);  // AwaitingApproval && ReviewDue && gate Auto && Mode Active
```

Rules:
- `StageSucceeded` waits at any gate other than `None` (Human and AutoWhenLowRisk alike). It sets `ReviewDue = p.Reviews(stage)` and `OnGateStatus` as today. The deferred-approval path is unchanged and leaves `ReviewDue` false.
- `EnterStage`, `Approve` and `Block` set `ReviewDue = false`. `Block` also sets `WaitingSince = null` (deferred item).
- `Approve` from `AwaitingApproval` returns `Decision = new(stageId, WaitingSince, Approved, Auto: false)`. `SendBack` from `AwaitingApproval` returns `SentBack`. Both are captured before the state changes. Neither sets a decision from `Approved` or `Blocked`.
- `ReviewCompleted`:
  - When `t.Status != AwaitingApproval` or `t.WaitingSince != waitingSince`, it returns `NoChange("review of {stage} finished after the gate was decided; not applied")`.
  - Otherwise it sets `Risk` and clears `ReviewDue`.
  - Then, when `allowAutoApprove && WouldAutoApprove(p, t, v, waitingSince)` (evaluated before `ReviewDue` is cleared), it returns the `Approve` outcome with the message `"auto-approved {stage}: low risk"` and `Decision.Auto = true`.
  - Otherwise it returns `Changed: true` with one of these messages:
    - shadow: `"reviewer (shadow): {low|high} risk — {summary}"`
    - high: `"{stage} needs you: high risk — {Reasons[0] ?? summary}"`
    - a human gate in active mode: `"reviewer: {low|high} risk — {summary}"`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Auto_gate_waits_and_owes_a_review() { /* implement gate Auto, Mode Active: StageSucceeded -> AwaitingApproval, ReviewDue true, SourceStatusToSet "In Review" */ }
[Fact] public void Human_gate_owes_review_only_when_review_is_on() { /* Mode Shadow -> ReviewDue true; Mode Off -> false */ }
[Fact] public void Low_verdict_auto_approves_auto_gate_in_active_mode() { /* plan Auto: ReviewCompleted(low) -> advanced to implement, Message starts "auto-approved plan", Decision.Auto true, Risk Low */ }
[Fact] public void Low_verdict_in_shadow_mode_only_records() { /* stays AwaitingApproval, ReviewDue false, Message starts "reviewer (shadow): low risk" */ }
[Fact] public void High_verdict_waits_on_you() { /* auto gate active -> AwaitingApproval, Risk High, Message "implement needs you: high risk — touches pricing" */ }
[Fact] public void Auto_approval_not_allowed_keeps_waiting() { /* allowAutoApprove false, low -> AwaitingApproval */ }
[Fact] public void Stale_review_of_earlier_round_is_not_applied() { /* WaitingSince differs -> Changed false, Risk unchanged */ }
[Fact] public void Approve_and_send_back_report_gate_decision() { /* Approve -> Decision(Approved, WaitingSince, Auto false); SendBack from AwaitingApproval -> SentBack; SendBack from Blocked -> Decision null */ }
[Fact] public void Block_clears_waiting_since_and_review_due() { }
[Fact] public void Waits_on_reviewer_only_for_active_auto_gates() { /* shadow -> false; active auto + ReviewDue -> true */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter PipelineEngineTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `PipelineEngineTests`, then the full `Symphonist.Core.Tests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(pipeline): auto gates, review rounds and recorded gate decisions`

---

### Task 5: Storing gate reviews, decisions and review runs

**Files:**
- Modify: `src/Symphonist.Core/Storage/ITicketStore.cs`, `src/Symphonist.Integrations/Storage/SymphonistDbContext.cs`, `src/Symphonist.Integrations/Storage/SqliteTicketStore.cs`, `src/Symphonist.Host/Dashboard/NotifyingTicketStore.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeTicketStore.cs`.
- Create: the migration, via `dotnet tool run dotnet-ef migrations add RiskReviewer --project src/Symphonist.Integrations`.
- Test: `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`, `tests/Symphonist.Host.Tests/Dashboard/NotifyingTicketStoreTests.cs`.

**Interfaces:**
- Consumes: Tasks 1, 2 and 4 types.
- Produces:

```csharp
public sealed record AgentRunRecord(Guid TicketId, string StageId, DateTimeOffset StartedAt, TimeSpan Duration,
    bool Succeeded, decimal? CostUsd, string? Error, string RawLogPath, string? Summary = null, bool IsReview = false);

/// <param name="Decision">The latest decision of the same round (joined on read); null while undecided.</param>
public sealed record GateReview(Guid TicketId, string ProjectId, string StageId, DateTimeOffset WaitingSince, DateTimeOffset At,
    ReviewMode Mode, GatePolicy Gate, GateVerdict Verdict, GateDecision? Decision = null, bool AutoDecided = false);
public sealed record GateDecisionRecord(Guid TicketId, string ProjectId, string StageId, DateTimeOffset WaitingSince,
    GateDecision Decision, bool Auto, DateTimeOffset At);

// ITicketStore
Task AddGateReviewAsync(GateReview review, CancellationToken ct);
Task AddGateDecisionAsync(GateDecisionRecord decision, CancellationToken ct);
Task<IReadOnlyList<GateReview>> GateReviewsAsync(Guid ticketId, CancellationToken ct);           // newest first, Decision joined
Task<IReadOnlyList<StageAgreement>> ReviewAgreementAsync(string? projectId, CancellationToken ct); // via ReviewAgreement.Compute
```

Rules:
- The review/decision join is on `(TicketId, StageId, WaitingSince)`. The latest decision wins.
- `ReviewAgreementAsync` feeds human decisions that have a matching review as `DecidedReview`, and auto decisions as `autoApproved`.
- `DeleteAsync` (forget) also deletes the ticket's reviews and decisions.
- Columns: `Ticket.ReviewDue`, `Ticket.Risk` (string conversion), `AgentRuns.IsReview`.
- New tables:
  - `GateReviews`: lists stored as JSON text; times use the existing `DateTimeOffset` converter.
  - `GateDecisions`.
  - Both indexed by `(TicketId, StageId, WaitingSince)`.
- `NotifyingTicketStore` forwards the new methods and notifies on the two writes.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Gate_review_round_trips_with_lists() { /* reasons/checked/areas, Mode, Gate, Error */ }
[Fact] public async Task Decision_is_joined_to_its_round_only() { /* review round A + decision for A, review round B undecided -> A.Decision Approved, B.Decision null */ }
[Fact] public async Task Decision_before_review_still_joins() { /* AddGateDecision then AddGateReview for same round -> Decision set */ }
[Fact] public async Task Agreement_is_computed_per_project_stage() { /* 2 human-decided reviews + 1 auto decision -> StageAgreement Decided 2, AutoApproved 1; filter by projectId excludes others */ }
[Fact] public async Task Ticket_review_fields_and_run_flag_round_trip() { /* ReviewDue true, Risk High, AgentRunRecord IsReview true */ }
[Fact] public async Task Delete_removes_reviews_and_decisions() { }
[Fact] public async Task Gate_writes_notify() { /* NotifyingTicketStoreTests: AddGateReview / AddGateDecision -> notifier raised */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter SqliteTicketStoreTests` and `dotnet test tests/Symphonist.Host.Tests --filter NotifyingTicketStoreTests`. Expected: FAIL.
- [ ] **Step 3: Implement**, including the migration and `FakeTicketStore`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(storage): gate reviews, gate decisions and review runs`

---

### Task 6: `GateReviewer`: the review run

**Files:**
- Create: `src/Symphonist.Host/Orchestration/GateReviewer.cs`, `src/Symphonist.Core/Prompts/Defaults/review.md`.
- Modify: `src/Symphonist.Core/Integrations/IGitWorkspace.cs`, `src/Symphonist.Integrations/Git/GitWorkspace.cs`, `tests/Symphonist.Host.Tests/Fakes/FakeGitWorkspace.cs`, `src/Symphonist.Core/Prompts/{PromptLibrary,PromptRenderer}.cs`, `src/Symphonist.Core/Prompts/Defaults/plan.md`, `src/Symphonist.Host/Orchestration/StageExecutor.cs` (shared prompt values).
- Test: `tests/Symphonist.Host.Tests/Orchestration/GateReviewerTests.cs`, `tests/Symphonist.Integrations.Tests/Git/GitWorkspaceTests.cs`, `tests/Symphonist.Core.Tests/Prompts/PromptRendererTests.cs`.

**Interfaces:**
- Consumes: `IAgentRunners`, `AgentRunRequest(..., OutputSchema, ReadOnlyWorkspace)` (Task 3); `GateVerdictParser`, `RiskAreaMatcher` (Task 2); `ITicketStore.AddAgentRunAsync` with `IsReview` and `GateReviewsAsync` (Task 5).
- Produces:

```csharp
// IGitWorkspace
/// Files changed in `git diff --name-only {range}` ('/'-separated), e.g. "origin/main...HEAD".
Task<IReadOnlyList<string>> ChangedFilesAsync(string workspace, string range, CancellationToken ct);
// PromptLibrary
public static string GetReview(ProjectConfig project);   // ReviewConfig.PromptPath override, else Defaults/review.md; validated
// PromptRenderer.KnownPlaceholders += "risk.areas", "gate.stage", "gate.kind", "review.diff_range", "review.changed_files",
//                                     "review.history", "review.stage_summary"
// GateReviewer
public sealed class GateReviewer(IGitWorkspace git, IAgentRunners agents, ITicketStore store, TimeProvider time)
{
    /// Never throws for agent or git failures (returns GateVerdict.Failed); honours ct.
    public Task<GateVerdict> ReviewAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct);
    public static string RiskAreasText(IReadOnlyList<RiskArea> areas);  // "- pricing: {description} (paths: src/Pricing/**)" per line; "(none configured)" when empty
}
```

Per stage kind:

| Kind | `review.diff_range` | Path guard |
|---|---|---|
| Triage | "" | no |
| Plan | "" (spec/plan paths are in `spec_path`/`plan_path`) | no |
| Implement | `origin/{main}...HEAD` | yes |
| Release | `origin/{production}...origin/{main}` | yes |

Other rules:
- `review.stage_summary` is the newest non-review `AgentRunRecord.Summary` for the stage, or "".
- `review.history` has one line per earlier `GateReview`: `"{stage}: {risk} risk ({decision or 'auto-approved' or 'undecided'})"`. For release it also has `"build: succeeded at {MergeCommit}"` and `"passed: {each earlier uat stage id}"`.
- The run: `git.PrepareAsync`, then `ChangedFilesAsync` (path-guarded kinds only), then render, then `agents.For(p.Review.Agent.Type).RunAsync(new(ws, prompt, p.Review.Agent, GateReviewSchema.Json, ReadOnlyWorkspace: true))`.
- The raw log is written to `logs/{project}/{key}/{stage}-review-{stamp}.json`. Record an `AgentRunRecord` with `IsReview: true` and `Summary = "{risk} risk: {summary}"`. Parse with `GateVerdictParser`; a failed run gives `GateVerdict.Failed(error)`.
- Then, for each touched `(area, file)`, apply `WithForcedHigh($"touches risk area '{area}': {file}")`. Only the first 5 files per area are listed, followed by `"(+N more)"`.

`review.md` must contain these instructions:
1. You are the gate reviewer for `{{gate.stage}}` ({{gate.kind}}) of ticket `{{ticket.key}}`.
2. Judge the output against the risk areas `{{risk.areas}}`.
3. Inspect `git diff {{review.diff_range}}` when it is non-empty.
4. Do not modify files.
5. Return `high` whenever you are unsure.
6. List what you checked in `checked`.
7. Name any risk area the change touches in `risk_areas_touched`.
8. Return the required structured output.

`plan.md` gains one line: `Risk areas for this project: {{risk.areas}}. Say in the plan which of them the change touches.`

- [ ] **Step 1: Write the failing tests**

```csharp
// GitWorkspaceTests (real git, temp repos)
[Fact] public async Task Changed_files_lists_branch_changes() { /* commit a/b.cs on branch -> ["a/b.cs"] for "origin/main...HEAD" */ }
// PromptRendererTests
[Fact] public void Review_placeholders_are_known() { /* Validate(default review.md) does not throw; PromptLibrary.GetReview(project) returns it */ }
// GateReviewerTests (fakes)
[Fact] public async Task Implement_review_runs_read_only_with_review_schema_and_diff_range() { /* request.ReadOnlyWorkspace, OutputSchema == GateReviewSchema.Json, Settings == p.Review.Agent, prompt contains "origin/main...HEAD" and the pricing area text */ }
[Fact] public async Task Release_review_covers_production_to_main_and_build_result() { /* prompt contains "origin/production...origin/main" and "build: succeeded at abc123" */ }
[Fact] public async Task Risk_area_path_forces_high_even_when_reviewer_says_low() { /* changed ["src/Pricing/Calc.cs"], agent low -> High, Reasons contains "touches risk area 'pricing': src/Pricing/Calc.cs" */ }
[Fact] public async Task Plan_review_skips_path_guard() { /* ChangedFilesAsync not called */ }
[Fact] public async Task Agent_failure_is_high_and_recorded_as_review_run() { /* Fail("timed out") -> High, Error "timed out"; store run IsReview true, Succeeded false */ }
[Fact] public async Task Runner_exception_is_high() { /* agent throws -> High, no exception escapes */ }
[Fact] public async Task Reviewer_uses_configured_agent_type() { /* AgentRunners resolves "claude-code" only; review.agent.type "claude-code" with model "opus" -> Settings.Model "opus" */ }
[Fact] public async Task Stage_prompts_get_risk_areas() { /* StageExecutor plan prompt contains RiskAreasText */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter GateReviewerTests` and the git/prompt tests. Expected: FAIL.
- [ ] **Step 3: Implement.** Move the shared prompt values from `StageExecutor.PromptValues` into an `internal static` helper both classes use, so `ticket.*`, `project.name`, `branch`, `spec_path`, `plan_path` and `risk.areas` are filled identically. `feedback` stays empty for reviews.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: gate reviewer runs a read-only, fail-closed review against risk areas`

---

### Task 7: Orchestrator: review runs, auto-approval, recorded decisions

**Files:**
- Modify: `src/Symphonist.Host/Orchestration/Orchestrator.cs`, `src/Symphonist.Host/Program.cs`, `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs` (its `Create` helper gains a `GateReviewer`; its `OrdersYaml` gains `review: { mode: off }` so existing tests are unchanged), and every other test that constructs `Orchestrator`.
- Test: `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs` (new tests use their own YAML with `riskAreas` and `review`).

**Interfaces:**
- Consumes: `GateReviewer.ReviewAsync` (Task 6); `PipelineEngine.ReviewCompleted`, `WouldAutoApprove`, `TransitionOutcome.Decision` (Task 4); `AutoApprovalComment.Format` (Task 2); store methods (Task 5).
- Produces:

```csharp
public sealed class Orchestrator(ProjectRegistry projects, ITicketStore store, IIntegrationFactory integrations,
    StageExecutor executor, GateReviewer reviewer, TimeProvider time, OrchestratorOptions options, ILogger<Orchestrator> log);
/// expectedWaitingSince: the gate round the developer saw; a different round changes nothing.
public Task<TransitionOutcome> ApproveAsync(string projectId, string key, CancellationToken ct,
    string? expectedStageId = null, DateTimeOffset? expectedWaitingSince = null);
// Program.cs: IAgentRunners = new AgentRunners({ ["claude-code"] = ClaudeCodeRunner }); AddSingleton<GateReviewer>()
```

Behavior:
- **`AdvanceAsync`:** a new case `AwaitingApproval when t.ReviewDue`.
  - If `inFlight.Count < MaxConcurrentAgentRuns`, start `inFlight[t.Id] = RunReviewAsync(p, io, t.Clone(), source)` and record the event `"reviewing {stage}"` once.
  - The ticket stays `AwaitingApproval`. Nothing is saved.
- **`RunReviewAsync`** mirrors `RunAgentStageAsync`: it runs outside the lock, then applies under the lock.
- **Applying a verdict** (`CompleteReviewAsync`), for a ticket reloaded by key with the same `Id`:
  1. Store `GateReview(run.WaitingSince, Mode, Gate, verdict)`. This always happens, even when the review is stale.
  2. If `WouldAutoApprove(p, t, verdict, run.WaitingSince)`, so never for a stale round, post `TrackerComments.Sign(AutoApprovalComment.Format(stage, verdict))`. Integration or comment failure sets `allowAutoApprove = false` and records the event `"auto-approval held: could not post comment: {error}; waiting on you"`.
  3. Call `PipelineEngine.ReviewCompleted(p, t, verdict, run.WaitingSince!.Value, allowAutoApprove, now)`.
  4. Save, then `ApplyAsync`. If `verdict.Error` is set, also record the event `"reviewer failed: {error}; waiting on you"`.
  5. A stale result records its `NoChange` message as an event (`AddEventAsync` directly, since `ApplyAsync` skips unchanged outcomes).
- **`ApplyAsync`:** when `outcome.Decision is { } d`, call `store.AddGateDecisionAsync(new(t.Id, t.ProjectId, d.StageId, d.WaitingSince, d.Decision, d.Auto, now))` before the tracker move. This one place covers CLI, API, dashboard, tracker moves, `@claude` and auto-approvals.
- **`ApproveAsync`** with `expectedWaitingSince` that differs from `t.WaitingSince` returns `NoChange("{stage} is waiting on a newer round; reload to see it")`.
- **`HandleClaudeCommentsAsync` is unchanged.** Its send-back reruns triage, and `StageSucceeded` makes the review due again. That is how the `@claude` backlog item is closed.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Low_risk_auto_gate_is_approved_with_comment() { /* plan gate auto, mode active; plan run -> review run (IsReview request) low -> advanced to implement; tracker comment "Auto-approved plan: low risk." signed; GateDecision Auto stored; Risk Low */ }
[Fact] public async Task High_risk_auto_gate_waits_on_you() { /* -> AwaitingApproval, Risk High, ReviewDue false, event "plan needs you: high risk — …", no comment */ }
[Fact] public async Task Shadow_mode_records_verdict_and_never_approves() { /* mode shadow, gate auto -> stays waiting; developer approves via CLI -> GateReviewsAsync[0].Decision Approved; ReviewAgreementAsync Decided 1 Agreed 1 */ }
[Fact] public async Task Human_decision_during_review_wins_and_still_counts() { /* review blocked on TCS; ApproveAsync -> advanced; release review low -> event "review of plan finished after the gate was decided; not applied"; review stored with Decision Approved */ }
[Fact] public async Task Review_failure_fails_closed_without_retry() { /* review Fail("timed out") -> waiting, Risk High, event "reviewer failed: timed out; waiting on you"; 3 more ticks -> agent review requests still 1 */ }
[Fact] public async Task Auto_approval_held_when_comment_fails() { /* tracker.CommentAsync throws -> AwaitingApproval, event starts "auto-approval held: could not post comment" */ }
[Fact] public async Task Review_uses_a_concurrency_slot() { /* maxRuns 1: ticket A reviewing (blocked) -> ticket B Pending plan does not start */ }
[Fact] public async Task Review_interrupted_by_restart_runs_again() { /* ticket AwaitingApproval ReviewDue true, fresh Orchestrator -> review request issued */ }
[Fact] public async Task Send_back_records_gate_decision() { /* SendBackAsync at waiting implement -> GateDecisionRecord SentBack with that round's WaitingSince */ }
[Fact] public async Task Approve_with_stale_waiting_since_changes_nothing() { /* expectedWaitingSince = old round -> Changed false, still waiting */ }
[Fact] public async Task Claude_comment_rerun_at_auto_triage_is_reviewed_again() { /* triage auto active, first verdict high -> waiting; "@claude answers" -> rerun -> review low -> advanced to plan with comment */ }
[Fact] public async Task Release_gate_auto_approves_low_risk_production_change() { /* release gate auto, release PR open, review low -> Approved -> merge -> deploy */ }
[Fact] public async Task Human_release_gate_is_reviewed_but_never_auto_approved() { /* release gate human, mode active, low -> stays waiting, event "reviewer: low risk — …" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter OrchestratorTests`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement**, including `Program.cs` wiring.
- [ ] **Step 4: Run** the full `OrchestratorTests`, then `dotnet test Symphonist.slnx`. Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: reviewer runs at gates, auto-approves low risk and records every gate decision`

---

### Task 8: Dashboard: risk badge, gate reasoning, agreement numbers

**Files:**
- Modify: `src/Symphonist.Host/Dashboard/BoardModels.cs`, `src/Symphonist.Host/Dashboard/BoardQuery.cs`, `src/Symphonist.Host/Components/Shared/TicketCard.razor`, `src/Symphonist.Host/Components/Shared/ProjectBar.razor`, `src/Symphonist.Host/Components/Pages/Board.razor`, `src/Symphonist.Host/Components/Pages/TicketDetail.razor`, `src/Symphonist.Host/wwwroot/app.css`.
- Test: `tests/Symphonist.Host.Tests/Dashboard/{BoardQueryTests,BoardComponentTests,TicketDetailComponentTests}.cs`.

**Interfaces:**
- Consumes: `Ticket.Risk`, `PipelineEngine.WaitsOnReviewer` (Task 4); `GateReviewsAsync`, `ReviewAgreementAsync` (Task 5); `ApproveAsync(..., expectedWaitingSince)` (Task 7).
- Produces:

```csharp
public sealed record BoardCard(/* existing */, RiskLevel? Risk = null, bool Reviewing = false);   // WaitingOnYou = AwaitingApproval && !Reviewing
public sealed record BoardProject(/* existing */, ReviewMode Review = ReviewMode.Off, IReadOnlyList<StageAgreement>? Agreement = null);
public sealed record TicketDetailModel(/* existing */, IReadOnlyList<GateReview> Reviews);
```

Rendering (text, never color alone):
- **Card:** a `badge-risk-low` / `badge-risk-high` badge reading "Low risk" / "High risk". A `Reviewing` card has status label "Reviewing", no Approve button, and is not highlighted.
- **`BoardCounts.WaitingOnYou`** counts `card.WaitingOnYou`.
- **Project bar:** the badge "Reviewer: shadow" / "active" / "off". Then, for each stage with `Decided > 0` or `AutoApproved > 0`: `"{stage}: agrees {Agreed}/{Decided}"`. Append `" · {FalseLows} low-risk call(s) sent back"` when FalseLows > 0, and `" · {AutoApproved} auto-approved"` when AutoApproved > 0.
- **Detail page:** a "Gate reviews" section (newest first). Each review shows the stage, local time, mode, risk badge, outcome ("auto-approved", "you approved", "you sent back", "undecided"), summary, and the Reasons, Checked and Risk areas touched lists, plus the error when present. When there are none: "No gate reviews yet."
- **Approve** (board card and detail page) passes `expectedWaitingSince: card.Since`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Card_carries_risk_and_reviewing() { /* BoardQuery: Risk High -> card.Risk High; active auto gate ReviewDue -> Reviewing true, WaitingOnYou false, Counts.WaitingOnYou 0 */ }
[Fact] public async Task Project_carries_review_mode_and_agreement() { /* store with decided reviews -> BoardProject.Agreement[0] Decided 2 Agreed 1 FalseLows 1 */ }
[Fact] public void Risk_badge_is_labelled() { /* bUnit: "High risk" text, class badge-risk-high */ }
[Fact] public void Reviewing_card_has_no_approve_button() { }
[Fact] public void Project_bar_shows_agreement_numbers() { /* "Reviewer: shadow", "implement: agrees 1/2", "1 low-risk call(s) sent back" */ }
[Fact] public void Detail_shows_gate_reasoning_as_text() { /* reasons "<b>x</b>" rendered literally; "you approved"; Checked items listed */ }
[Fact] public async Task Approve_sends_the_round_it_showed() { /* click Approve -> ApproveAsync with expectedWaitingSince == card.Since (ticket sent back and waiting again in between -> status line "waiting on a newer round") */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~Dashboard"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Screenshot check** at 400×860 and 1440×900. Use a temp `SYMPHONIST_HOME`, port 5299 and seeded tickets (one high-risk waiting, one reviewing, one with three gate reviews), and never touch `~/.symphonist`. Confirm the badges wrap cleanly on phone width and the reasons lists are readable. Note the result in the report.
- [ ] **Step 6: Commit** `feat(dashboard): risk badge, gate reasoning and reviewer agreement`

---

### Task 9: API, CLI and README

**Files:**
- Modify: `src/Symphonist.Host/Api/TicketsApi.cs`, `src/Symphonist.Cli/StatusTable.cs`, `src/Symphonist.Cli/SymphonistClient.cs` (DTO mirrors), `README.md`.
- Test: `tests/Symphonist.Host.Tests/Api/TicketsApiTests.cs`, `tests/Symphonist.Cli.Tests/CliTests.cs`.

**Interfaces:**
- Consumes: Tasks 5 and 7.
- Produces:

```csharp
public sealed record TicketDto(/* existing */, RiskLevel? Risk = null);
public sealed record GateReviewDto(string Stage, DateTimeOffset At, ReviewMode Mode, RiskLevel Risk, string Summary,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Checked, IReadOnlyList<string> AreasTouched, string? Decision, string? Error);
public sealed record TicketDetailDto(/* existing */, IReadOnlyList<GateReviewDto> Reviews);
public sealed record ProjectDto(/* existing */, ReviewMode Review, IReadOnlyList<StageAgreement> Agreement);
```

CLI output:
- **`status`** adds a `RISK` column ("low", "high" or empty) after `STATUS`.
- **`show`** prints the latest review after the status lines: `Review:   {stage} ({mode}) {risk} risk: {summary}`, followed by `  - {reason}` lines.
- **`projects`** adds a `REVIEW` column (`shadow`/`active`/`off`). Under each project with agreement data it prints indented lines `  {stage}: agrees {A}/{D}[, {F} low-risk sent back][, {N} auto-approved]`.

README: a new "Risk reviewer" section after "Answering Symphonist from Jira". It covers:
- `riskAreas` (paths + description).
- The three `review.mode` values, with off as the default and how to opt in with `shadow`.
- `gate: auto` and that it needs `mode: active`.
- That the release gate with `auto` is production auto-approval, and that `human` turns it off.
- What the auto-approval comment says.
- That failures and unsure verdicts wait on you.
- That review runs count toward agent concurrency and cost.
- Where to read the agreement numbers.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Ticket_detail_includes_gate_reviews() { /* GET /api/tickets/orders/ORD-1 -> Reviews[0].Risk "High", Reasons, Decision "Approved" */ }
[Fact] public async Task Projects_include_review_mode_and_agreement() { }
[Fact] public async Task Status_shows_risk_column() { /* CLI: header contains "RISK"; row contains "high" */ }
[Fact] public async Task Show_prints_latest_review() { /* "Review:   implement (shadow) high risk: touches pricing" and "  - touches risk area 'pricing'" */ }
[Fact] public async Task Projects_prints_agreement() { /* "REVIEW" header, "  implement: agrees 9/10, 1 low-risk sent back" */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter TicketsApiTests` and `dotnet test tests/Symphonist.Cli.Tests`. Expected: FAIL.
- [ ] **Step 3: Implement**, including the README.
- [ ] **Step 4: Run** the full `dotnet test Symphonist.slnx`. Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: risk and gate reviews in the API and CLI; document the risk reviewer`
