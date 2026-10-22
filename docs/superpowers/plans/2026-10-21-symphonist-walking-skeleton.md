# Symphonist Walking Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One real ticket goes end to end — Jira (Cloud) → triage → plan → implement → PR → merge → build → release PR → deploy → Done — on Bitbucket Cloud + Claude Code running in a container, with human gates approved via the CLI or by moving the Jira card.

**Architecture:** A local ASP.NET Core host runs a background orchestrator that polls trackers, drives a pure pipeline state machine (`PipelineEngine`), and executes stage side effects (`StageExecutor`) through plug-in interfaces defined in `Symphonist.Core`. Integrations (Jira, Bitbucket, Claude Code, Docker, Git, SQLite) live in `Symphonist.Integrations`. A separate `symphonist` .NET tool talks to the host's localhost API.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core + SQLite, YamlDotNet, System.CommandLine, Devlooped.CredentialManager, xUnit, .NET Aspire (AppHost + ServiceDefaults, dev only), Docker, Claude Code CLI.

**Spec:** `docs/superpowers/specs/2026-10-21-symphonist-design.md`

## Scope of this plan

This is plan 1 of several. It covers the minimum to run one real ticket end to end. Later plans (not here):

2. Dashboard pipeline board (Blazor Server, SignalR).
3. Risk reviewer: auto gates, shadow mode, risk areas, prod risk assessment, auto-approval comments.
4. Onboarding: fetch source statuses, agent-proposed mapping and risk areas, setup screen, container check.
5. Evidence on high-risk PRs, PR comment loop, send-back "save the lesson".
6. Codex agent + second-opinion reviewer.
7. Release cadence windows, UAT environments, digest, native notifications, touch tracking, usage-limit pause, cost (dollar) guardrail.
8. Other plug-ins (ADO, Target Process, spreadsheet, GitHub, Azure Repos, OpenCode), repo `devcontainer.json` images.
9. Platform polish: headless-Linux encrypted secret fallback, tray icon, autostart, packaging.

Plan-level decisions (builder's calls, consistent with the spec):

- **Per-ticket workspace is a local `git clone` of the developer's checkout, not a `git worktree`.** A worktree's `.git` file points at an absolute host path that doesn't resolve inside a container; a local clone is self-contained, so it mounts cleanly. Same isolation, one branch per ticket.
- **Git push and commit happen on the host with the developer's own git identity and credential helper** (acts as them; no Git tokens enter the container).
- **Agent output contract = Claude Code `--json-schema` structured output** (one shared schema, `StageOutput`).
- **Gates in this plan are `None` or `Human` only.** `AutoWhenLowRisk` arrives with plan 3.
- **Local API protected by a random token** in `~/.symphonist/api-token`, sent as header `X-Symphonist-Token`, in addition to binding to `127.0.0.1`.
- **CI = GitHub Actions matrix (ubuntu, windows, macos).** Swap for Bitbucket Pipelines if this repo is hosted there.

## Global Constraints

- .NET 10 (`net10.0`) for every project.
- Windows, macOS, and Linux supported in v1. No Windows-only APIs in the core; platform-specific pieces (secrets, notifications, tray, autostart) sit behind small abstractions. Paths, shells, line endings, and process launching must work on all three. CI builds and tests on all three.
- Launch every process with `ProcessStartInfo.ArgumentList` (never a shell string); build paths with `Path.Combine`.
- Acts as the developer: tickets assigned to them, actions under their accounts.
- Symphonist owns its own pipeline states; source systems are mapped onto them. Cards progress even when the source has no matching status.
- Approvals work both by moving the card in the source system (when a mapped status exists) and through Symphonist (CLI/API).
- Secrets: OS credential store on each platform, never in plain config files.
- Container mode (default): only the ticket workspace mounted; developer's Git/tracker credentials never enter the container; outbound network restricted to an allowlist, default deny.
- Host mode: explicit opt-in per project; per-stage tool allowlist; no blanket permission bypass.
- Local API bound to localhost only.
- Aspire is for development only; end users don't need it.
- Data directory: `$SYMPHONIST_HOME` if set, else `~/.symphonist` (`Environment.SpecialFolder.UserProfile`).

## Review Focus

1. **Jira workflow has no transition to a mapped status** (e.g. no direct Backlog → In Review). Expect: card still moves in Symphonist, a ticket event "no transition to 'In Review'" is recorded, nothing throws. → test in Task 9 and Task 12.
2. **Agent run returns garbage**: non-zero exit, `is_error: true`, missing/invalid structured output, or timeout. Expect: stage fails, attempt counted, ticket goes Blocked with the error after `maxAttemptsPerStage`. → tests in Task 7 and Task 4.
3. **Host restarts mid-run.** Expect: agent-stage tickets left in `Running` reset to `Pending` on startup (attempt counted), not stuck forever. → test in Task 5.
4. **Ticket leaves the tracker query mid-flight** (unassigned, label removed). Expect: ticket goes Blocked "no longer matches tracker query"; work (branch, PR) is kept; a failed query never blocks anything. → test in Task 12.
5. **Rerun after send-back** finds the workspace, branch, and PR from the previous attempt. Expect: workspace reused (no re-clone, no "branch exists" failure), existing PR updated by push rather than a second PR opened. → tests in Task 6 and Task 11.

---

## File Structure

```
Symphonist.slnx
.github/workflows/ci.yml
src/
  Symphonist.Core/                    domain + interfaces, no I/O
    Config/ProjectConfig.cs           records: ProjectConfig, TrackerConfig, RepoConfig, BuildConfig, AgentSettings, StageDefinition, enums
    Config/ProjectConfigLoader.cs     YAML -> ProjectConfig + validation
    Pipeline/Ticket.cs                Ticket entity, StageStatus
    Pipeline/TicketEvent.cs           history entry
    Pipeline/PipelineEngine.cs        pure transitions
    Pipeline/TransitionOutcome.cs
    Prompts/PromptRenderer.cs         {{placeholder}} rendering
    Prompts/PromptLibrary.cs          default templates + overrides
    Prompts/Defaults/*.md             embedded default templates
    Agents/IAgentRunner.cs            AgentRunRequest, AgentRunResult, StageOutput, StageOutputSchema
    Integrations/ITracker.cs          SourceTicket, SourceComment
    Integrations/ISourceControl.cs    PullRequestInfo, MergeResult
    Integrations/IBuildWatcher.cs     BuildStatus
    Integrations/IGitWorkspace.cs
    Integrations/IIntegrationFactory.cs  ProjectIntegrations
    Integrations/ISecretStore.cs
    Storage/ITicketStore.cs
    SymphonistPaths.cs
  Symphonist.Integrations/
    Processes/ProcessRunner.cs        IProcessRunner, ProcessSpec, ProcessResult
    Git/GitWorkspace.cs
    ClaudeCode/ClaudeCodeRunner.cs
    ClaudeCode/IAgentLauncher.cs      HostLauncher
    Containers/DockerLauncher.cs
    Containers/agent/Dockerfile, entrypoint.sh, init-firewall.sh   (copied to output)
    Jira/JiraTracker.cs, Jira/Adf.cs
    Bitbucket/BitbucketSourceControl.cs, Bitbucket/BitbucketPipelinesWatcher.cs
    Storage/SymphonistDbContext.cs, Storage/SqliteTicketStore.cs
    Secrets/CredentialManagerSecretStore.cs
    IntegrationFactory.cs
  Symphonist.Host/
    Program.cs
    Orchestration/StageExecutor.cs
    Orchestration/Orchestrator.cs      tick logic
    Orchestration/OrchestratorService.cs  BackgroundService timer
    Api/TicketsApi.cs, Api/ApiTokenMiddleware.cs
  Symphonist.Cli/
    Program.cs, SymphonistClient.cs
  Symphonist.ServiceDefaults/          (Aspire template)
  Symphonist.AppHost/                  (Aspire template)
tests/
  Symphonist.Core.Tests/
  Symphonist.Integrations.Tests/       Http/StubHttpHandler.cs, Fixtures/*.json
  Symphonist.Host.Tests/               Fakes/*.cs
  Symphonist.Cli.Tests/
```

---

### Task 1: Solution scaffold, Aspire, CI

**Files:**
- Create: `Symphonist.slnx`, all `src/*/` and `tests/*/` projects above, `.github/workflows/ci.yml`, `Directory.Build.props`
- Test: `tests/Symphonist.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: `SymphonistPaths.Home` (string), `SymphonistPaths.Resolve(params string[] parts)` in `src/Symphonist.Core/SymphonistPaths.cs`.

- [ ] **Step 1: Create projects**

```bash
dotnet new install Aspire.ProjectTemplates
dotnet new sln -n Symphonist
dotnet new classlib -n Symphonist.Core -o src/Symphonist.Core -f net10.0
dotnet new classlib -n Symphonist.Integrations -o src/Symphonist.Integrations -f net10.0
dotnet new web -n Symphonist.Host -o src/Symphonist.Host -f net10.0
dotnet new console -n Symphonist.Cli -o src/Symphonist.Cli -f net10.0
dotnet new aspire-servicedefaults -n Symphonist.ServiceDefaults -o src/Symphonist.ServiceDefaults
dotnet new aspire-apphost -n Symphonist.AppHost -o src/Symphonist.AppHost
for t in Core Integrations Host Cli; do dotnet new xunit -n Symphonist.$t.Tests -o tests/Symphonist.$t.Tests -f net10.0; done
dotnet sln add src/*/*.csproj tests/*/*.csproj
```

References: Integrations → Core; Host → Core, Integrations, ServiceDefaults; AppHost → Host; each test project → its subject (Host.Tests also → Integrations). `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`. AppHost `Program.cs`: `builder.AddProject<Projects.Symphonist_Host>("host");`. Host `Program.cs`: `builder.AddServiceDefaults();` and `app.MapDefaultEndpoints();`.

- [ ] **Step 2: Write failing test** `tests/Symphonist.Core.Tests/SmokeTests.cs`

```csharp
[Fact]
public void Home_uses_SYMPHONIST_HOME_when_set()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Environment.SetEnvironmentVariable("SYMPHONIST_HOME", dir);
    try { Assert.Equal(dir, SymphonistPaths.Home); Assert.Equal(Path.Combine(dir, "a", "b"), SymphonistPaths.Resolve("a", "b")); }
    finally { Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null); }
}

[Fact]
public void Home_defaults_to_dot_symphonist_in_user_profile()
{
    Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
    var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".symphonist");
    Assert.Equal(expected, SymphonistPaths.Home);
}
```

- [ ] **Step 3: Run** `dotnet test tests/Symphonist.Core.Tests` — Expected: FAIL (`SymphonistPaths` not defined).
- [ ] **Step 4: Implement** `public static class SymphonistPaths { public static string Home { get; } ... ; public static string Resolve(params string[] parts) }` — `Home` is a computed property (read env each call; tests mutate it).
- [ ] **Step 5: Run** `dotnet build && dotnet test` — Expected: build succeeds, all tests PASS.
- [ ] **Step 6: Add CI** `.github/workflows/ci.yml`: on push/PR, matrix `os: [ubuntu-latest, windows-latest, macos-latest]`, `actions/setup-dotnet` with `dotnet-version: 10.0.x`, run `dotnet test --filter "Category!=Docker&Category!=Live"`.
- [ ] **Step 7: Commit** `git add -A && git commit -m "chore: scaffold solution, Aspire AppHost, CI"`

---

### Task 2: Project config model and YAML loader

**Files:**
- Create: `src/Symphonist.Core/Config/ProjectConfig.cs`, `src/Symphonist.Core/Config/ProjectConfigLoader.cs`
- Test: `tests/Symphonist.Core.Tests/Config/ProjectConfigLoaderTests.cs`

**Interfaces:**
- Produces (all `public sealed record` unless noted):

```csharp
public enum StageKind { Triage, Plan, Implement, Build, Uat, Release, Deploy, Done }
public enum GatePolicy { None, Human }
public enum Isolation { Container, Host }

public sealed record StageDefinition(string Id, StageKind Kind, GatePolicy Gate,
    IReadOnlyList<string> EntryStatuses, string? OnEnterStatus, string? OnGateStatus, string? ApprovedWhenStatus);
public sealed record TrackerConfig(string Type, Uri Site, string Email, string TokenSecret, string Jql);
public sealed record RepoConfig(string Type, string Workspace, string Slug, string LocalPath,
    string MainBranch, string ProductionBranch, string Email, string TokenSecret, string MergeStrategy);
public sealed record BuildConfig(string Type, int TimeoutMinutes);
public sealed record AgentSettings(string Type, Isolation Isolation, IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> AllowedTools, int TimeoutMinutes, string? Model, string? Image);
public sealed record ProjectConfig(string Id, string Name, TrackerConfig Tracker, RepoConfig Repo, BuildConfig Build,
    AgentSettings Agent, int MaxAttemptsPerStage, IReadOnlyList<StageDefinition> Pipeline,
    IReadOnlyDictionary<StageKind, string> PromptOverrides, string ConfigDirectory)
{
    public StageDefinition Stage(string id);          // throws KeyNotFoundException
    public StageDefinition? NextStage(string id);      // null after last
}
public static class ProjectConfigLoader
{
    public static ProjectConfig Load(string yamlPath);
    public static IReadOnlyList<ProjectConfig> LoadAll(string projectsDirectory); // *.yaml, *.yml
}
public sealed class ProjectConfigException(string message) : Exception(message);
```

YAML shape (camelCase keys; this exact document is the test fixture `tests/Symphonist.Core.Tests/Config/orders.yaml`):

```yaml
id: orders
name: Orders (Acme)
tracker:
  type: jira
  site: https://acme.atlassian.net
  email: dev@example.com
  tokenSecret: jira-acme
  jql: assignee = currentUser() AND labels = Claude AND statusCategory != Done
repo:
  type: bitbucket
  workspace: acme
  slug: orders
  localPath: ../acme-orders
  mainBranch: main
  productionBranch: production
  email: dev@example.com
  tokenSecret: bitbucket-acme
build:
  type: bitbucket-pipelines
agent:
  type: claude-code
  allowedDomains: [api.nuget.org, registry.npmjs.org]
pipeline:
  - { id: triage,    kind: triage,    gate: human, entryStatuses: [Backlog], approvedWhenStatus: Planned }
  - { id: plan,      kind: plan,      gate: human, entryStatuses: [Planned], onEnterStatus: Planned, approvedWhenStatus: In Progress }
  - { id: implement, kind: implement, gate: human, onEnterStatus: In Progress, onGateStatus: In Review }
  - { id: build,     kind: build }
  - { id: release,   kind: release,   gate: human }
  - { id: deploy,    kind: deploy }
  - { id: done,      kind: done,      onEnterStatus: Done }
prompts:
  plan: prompts/plan.md
```

Defaults: `agent.isolation: container`, `agent.timeoutMinutes: 60`, `agent.allowedTools: []`, `build.timeoutMinutes: 45`, `repo.mergeStrategy: merge_commit`, `maxAttemptsPerStage: 3`, `gate: none`, `entryStatuses: []`. `localPath` and prompt paths resolve relative to the YAML file's directory (`ConfigDirectory`).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Loads_fixture_with_defaults()
{
    var p = ProjectConfigLoader.Load(Fixture("orders.yaml"));
    Assert.Equal("orders", p.Id);
    Assert.Equal(Isolation.Container, p.Agent.Isolation);
    Assert.Equal(60, p.Agent.TimeoutMinutes);
    Assert.Equal(3, p.MaxAttemptsPerStage);
    Assert.Equal("merge_commit", p.Repo.MergeStrategy);
    Assert.Equal(7, p.Pipeline.Count);
    Assert.Equal(GatePolicy.None, p.Stage("build").Gate);
    Assert.Equal("In Progress", p.Stage("plan").ApprovedWhenStatus);
    Assert.True(Path.IsPathFullyQualified(p.Repo.LocalPath));
    Assert.Equal(Path.Combine(p.ConfigDirectory, "prompts", "plan.md"), p.PromptOverrides[StageKind.Plan]);
    Assert.Equal("deploy", p.NextStage("release")!.Id);
    Assert.Null(p.NextStage("done"));
}

[Theory]
[InlineData("pipeline: []", "pipeline must contain at least one stage")]
[InlineData("duplicate-stage-id", "duplicate stage id 'plan'")]
[InlineData("last-stage-not-done", "last stage must be kind 'done'")]
[InlineData("host-without-tools", "host isolation requires agent.allowedTools")]
[InlineData("unknown-kind", "unknown stage kind 'qa'")]
public void Rejects_invalid_config(string variant, string expectedMessage) { /* write fixture variant, assert ProjectConfigException message contains expectedMessage */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter ProjectConfigLoaderTests` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** records and loader. Deserialize with YamlDotNet (`CamelCaseNamingConvention`) into private mutable DTOs, then map + validate into records. Validation rules = the five messages above, plus `tracker.type` must be `jira`, `repo.type` must be `bitbucket`, `agent.type` must be `claude-code` (message: `unsupported <field> '<value>'`).
- [ ] **Step 4: Run** same command — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -am "feat(core): project config model and YAML loader"` (add new files first).

---

### Task 3: Prompt renderer and default templates

**Files:**
- Create: `src/Symphonist.Core/Prompts/PromptRenderer.cs`, `src/Symphonist.Core/Prompts/PromptLibrary.cs`, `src/Symphonist.Core/Prompts/Defaults/{triage,plan,implement}.md` (EmbeddedResource)
- Test: `tests/Symphonist.Core.Tests/Prompts/PromptRendererTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig.PromptOverrides`, `StageKind`.
- Produces:

```csharp
public static class PromptRenderer
{
    public static readonly IReadOnlySet<string> KnownPlaceholders; // ticket.key, ticket.title, ticket.description, ticket.comments, ticket.url, project.name, branch, feedback, spec_path, plan_path
    public static string Render(string template, IReadOnlyDictionary<string, string?> values);
    public static void Validate(string template); // throws PromptTemplateException("unknown placeholder '{{x}}'")
}
public static class PromptLibrary
{
    public static string Get(ProjectConfig project, StageKind kind); // override file if configured, else embedded default; validated
}
public sealed class PromptTemplateException(string message) : Exception(message);
```

Placeholder syntax `{{name}}`. Missing/null values render as empty string. Values are inserted literally — a value containing `{{ticket.key}}` is **not** expanded again (feedback text is user input).

Default templates (derived from the developer's current prompt, tracker-neutral). Each ends with: "Return your result in the required structured output." Content:
- `triage.md`: Read ticket {{ticket.key}} "{{ticket.title}}" ({{ticket.url}}), its description and comments, and the code in this repository. Decide whether it is clear and feasible, and propose the best path forward. Do not change any files. Put a concise comment for the ticket (approach, affected areas, open questions) in `comment_markdown`. If `{{feedback}}` is non-empty, it is the developer's feedback on a previous attempt; address it.
- `plan.md`: Use your brainstorming superpowers (if available) to review ticket {{ticket.key}} … and write a spec and implementation plan as Markdown files under `docs/specs/` and `docs/plans/` in this repository. Work non-interactively: make reasonable decisions and record open questions in the spec. Return repo-relative paths in `spec_path` and `plan_path`, and a short summary. Feedback clause as above.
- `implement.md`: Implement ticket {{ticket.key}} following the spec at {{spec_path}} and the plan at {{plan_path}}, using multiple agents if available. Write tests, run them, and check your work. Do not commit or push; leave changes in the working tree. Return `pr_title`, `pr_description`, `tests_passed`, and a summary. Feedback clause as above.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Renders_known_placeholders_and_blanks_nulls()
    => Assert.Equal("ORD-1: Fix tax / ", PromptRenderer.Render("{{ticket.key}}: {{ticket.title}} / {{feedback}}",
        new Dictionary<string, string?> { ["ticket.key"] = "ORD-1", ["ticket.title"] = "Fix tax", ["feedback"] = null }));

[Fact] public void Does_not_expand_placeholders_inside_values()
    => Assert.Equal("say {{ticket.key}}", PromptRenderer.Render("say {{feedback}}",
        new Dictionary<string, string?> { ["feedback"] = "{{ticket.key}}", ["ticket.key"] = "X" }));

[Fact] public void Validate_rejects_unknown_placeholder()
    => Assert.Contains("unknown placeholder '{{ticket.nope}}'",
        Assert.Throws<PromptTemplateException>(() => PromptRenderer.Validate("{{ticket.nope}}")).Message);

[Theory, InlineData(StageKind.Triage), InlineData(StageKind.Plan), InlineData(StageKind.Implement)]
public void Embedded_defaults_exist_and_validate(StageKind kind)
    => Assert.Contains("{{ticket.key}}", PromptLibrary.Get(ConfigWithoutOverrides(), kind));

[Fact] public void Override_file_wins() { /* write temp override "custom {{ticket.key}}", assert Get returns it */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter Prompt` — Expected: FAIL.
- [ ] **Step 3: Implement.** Single-pass `Regex.Replace(template, @"\{\{([a-z_.]+)\}\}", ...)` — single pass is what guarantees no re-expansion.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): prompt templates and renderer"`

---

### Task 4: Ticket model and pipeline state machine

**Files:**
- Create: `src/Symphonist.Core/Pipeline/{Ticket,TicketEvent,PipelineEngine,TransitionOutcome}.cs`
- Test: `tests/Symphonist.Core.Tests/Pipeline/PipelineEngineTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig`, `StageDefinition`, `StageKind`, `GatePolicy`.
- Produces:

```csharp
public enum StageStatus { Pending, Running, AwaitingApproval, Approved, Blocked, Done }

public sealed class Ticket   // mutable; EF entity
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string StageId { get; set; } = "";
    public StageStatus Status { get; set; }
    public string? BlockedReason { get; set; }
    public string? Feedback { get; set; }
    public int Attempts { get; set; }
    public string? LastSourceStatus { get; set; }
    public string? Branch { get; set; }
    public string? SpecPath { get; set; }
    public string? PlanPath { get; set; }
    public long? PullRequestId { get; set; }
    public string? MergeCommit { get; set; }
    public long? ReleasePullRequestId { get; set; }
    public string? ReleaseMergeCommit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StageStartedAt { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }
}
public sealed class TicketEvent { public long Id { get; set; } public Guid TicketId { get; set; } public DateTimeOffset At { get; set; } public string StageId { get; set; } = ""; public string Message { get; set; } = ""; }

public sealed record TransitionOutcome(bool Changed, string? SourceStatusToSet, string Message);

public static class PipelineEngine
{
    public static Ticket Create(ProjectConfig p, string key, string title, string sourceStatus, DateTimeOffset now);
    public static TransitionOutcome StageSucceeded(ProjectConfig p, Ticket t, DateTimeOffset now);
    public static TransitionOutcome StageFailed(ProjectConfig p, Ticket t, string error, DateTimeOffset now);
    public static TransitionOutcome Approve(ProjectConfig p, Ticket t, DateTimeOffset now);
    public static TransitionOutcome PostApprovalCompleted(ProjectConfig p, Ticket t, DateTimeOffset now);
    public static TransitionOutcome SendBack(ProjectConfig p, Ticket t, string? feedback, string? toStageId, DateTimeOffset now);
    public static TransitionOutcome SourceStatusChanged(ProjectConfig p, Ticket t, string newStatus, string? latestFeedback, DateTimeOffset now);
    public static TransitionOutcome Block(Ticket t, string reason, DateTimeOffset now);
    public static TransitionOutcome Unblock(Ticket t, DateTimeOffset now);
    public static bool HasPostApprovalAction(StageKind kind); // true for Implement (merge PR) and Release (merge release PR)
}
```

Rules (status compare case-insensitive everywhere):
- `Create`: stage = first stage whose `EntryStatuses` contains `sourceStatus`, else first stage; `Status=Pending`; `LastSourceStatus=sourceStatus`; `Branch = $"symphonist/{key}"`.
- Entering any stage: `Status=Pending`, `Attempts=0`, `StageStartedAt=now`, `SourceStatusToSet = stage.OnEnterStatus`. Entering a `Done` stage sets `Status=Done`.
- `StageSucceeded`: gate `Human` → `AwaitingApproval`, `WaitingSince=now`, `SourceStatusToSet=OnGateStatus`; gate `None` → advance to next stage. Clears `Feedback`.
- `StageFailed`: `Attempts++`; if `Attempts >= MaxAttemptsPerStage` → `Blocked`, `BlockedReason = $"{stageId} failed {n} times: {error}"`; else `Pending`.
- `Approve`: only from `AwaitingApproval` (else `Changed=false`). If `HasPostApprovalAction(kind)` → `Approved`; else advance.
- `PostApprovalCompleted`: from `Approved` → advance.
- `SendBack`: target stage = `toStageId` ?? current; `Status=Pending`, `Attempts=0`, `Feedback=feedback`; `SourceStatusToSet = target.OnEnterStatus`. Allowed from `AwaitingApproval`, `Approved`, `Blocked`.
- `SourceStatusChanged` (always sets `LastSourceStatus`): if `AwaitingApproval` and `newStatus == stage.ApprovedWhenStatus` → `Approve`. Else if `newStatus` is in `EntryStatuses` or equals `OnEnterStatus` of an **earlier** stage → `SendBack(latestFeedback, thatStage)`. Else no change.
- `Block` / `Unblock`: `Unblock` → `Pending`, `Attempts=0`, `BlockedReason=null`.

- [ ] **Step 1: Write failing tests** (use the Task 2 fixture `orders.yaml`, `now = DateTimeOffset.Parse("2026-10-21T10:00Z")`):

```csharp
[Fact] public void Create_starts_at_stage_matching_source_status()
{ Assert.Equal("plan", PipelineEngine.Create(P, "ORD-1", "t", "planned", Now).StageId); Assert.Equal("triage", PipelineEngine.Create(P, "ORD-2", "t", "Selected", Now).StageId); }

[Fact] public void Human_gate_waits_and_sets_gate_status()
{ var t = At("implement"); var o = PipelineEngine.StageSucceeded(P, t, Now);
  Assert.Equal(StageStatus.AwaitingApproval, t.Status); Assert.Equal("In Review", o.SourceStatusToSet); Assert.Equal(Now, t.WaitingSince); }

[Fact] public void No_gate_advances_and_sets_enter_status()
{ var t = At("deploy"); var o = PipelineEngine.StageSucceeded(P, t, Now); Assert.Equal("done", t.StageId); Assert.Equal(StageStatus.Done, t.Status); Assert.Equal("Done", o.SourceStatusToSet); }

[Fact] public void Failure_blocks_after_max_attempts()
{ var t = At("plan"); PipelineEngine.StageFailed(P, t, "boom", Now); PipelineEngine.StageFailed(P, t, "boom", Now);
  Assert.Equal(StageStatus.Pending, t.Status);
  PipelineEngine.StageFailed(P, t, "boom", Now);
  Assert.Equal(StageStatus.Blocked, t.Status); Assert.Equal("plan failed 3 times: boom", t.BlockedReason); }

[Fact] public void Approve_implement_goes_to_Approved_then_build()
{ var t = Awaiting("implement"); PipelineEngine.Approve(P, t, Now); Assert.Equal(StageStatus.Approved, t.Status);
  PipelineEngine.PostApprovalCompleted(P, t, Now); Assert.Equal("build", t.StageId); Assert.Equal(StageStatus.Pending, t.Status); }

[Fact] public void Approve_ignored_unless_awaiting() { var t = At("plan"); Assert.False(PipelineEngine.Approve(P, t, Now).Changed); }

[Fact] public void Source_status_approves_gate()
{ var t = Awaiting("plan"); PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now); Assert.Equal("implement", t.StageId); }

[Fact] public void Source_status_moving_back_sends_back_with_feedback()
{ var t = Awaiting("plan"); PipelineEngine.SourceStatusChanged(P, t, "Backlog", "needs caching", Now);
  Assert.Equal("triage", t.StageId); Assert.Equal("needs caching", t.Feedback); Assert.Equal(StageStatus.Pending, t.Status); }

[Fact] public void Unrelated_source_status_is_recorded_but_no_change()
{ var t = Awaiting("plan"); var o = PipelineEngine.SourceStatusChanged(P, t, "Code Review", null, Now); Assert.False(o.Changed); Assert.Equal("Code Review", t.LastSourceStatus); }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter PipelineEngineTests` — Expected: FAIL.
- [ ] **Step 3: Implement** per the rules. One private `EnterStage(ProjectConfig, Ticket, StageDefinition, DateTimeOffset)` used by every advance/send-back path. Every method sets `UpdatedAt=now` when `Changed`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): ticket model and pipeline state machine"`

---

### Task 5: SQLite ticket store and startup recovery

**Files:**
- Create: `src/Symphonist.Core/Storage/ITicketStore.cs`, `src/Symphonist.Integrations/Storage/{SymphonistDbContext,SqliteTicketStore}.cs`, EF migration `Initial`
- Test: `tests/Symphonist.Integrations.Tests/Storage/SqliteTicketStoreTests.cs`

**Interfaces:**
- Consumes: `Ticket`, `TicketEvent`, `StageKind`.
- Produces:

```csharp
public interface ITicketStore
{
    Task<Ticket?> FindAsync(string projectId, string key, CancellationToken ct);
    Task<IReadOnlyList<Ticket>> ListAsync(string? projectId, CancellationToken ct);
    Task SaveAsync(Ticket ticket, CancellationToken ct);                // insert or update
    Task AddEventAsync(Guid ticketId, string stageId, string message, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken ct);
    Task AddAgentRunAsync(AgentRunRecord run, CancellationToken ct);
    Task<int> ResetInterruptedRunsAsync(Func<string, string, bool> isAgentStage, DateTimeOffset now, CancellationToken ct);
}
public sealed record AgentRunRecord(Guid TicketId, string StageId, DateTimeOffset StartedAt, TimeSpan Duration,
    bool Succeeded, decimal? CostUsd, string? Error, string RawLogPath);
public sealed class SqliteTicketStore(string databasePath) : ITicketStore  // creates/migrates on first use
```

`ResetInterruptedRunsAsync(isAgentStage(projectId, stageId))`: every ticket with `Status=Running` where `isAgentStage` is true → `Status=Pending`, `Attempts++`, event "interrupted by restart; retrying". Returns count. (Build/Deploy `Running` tickets are polling stages and stay `Running`.) Unique index on `(ProjectId, Key)`. Database file: `SymphonistPaths.Resolve("symphonist.db")` (wired in Task 13).

- [ ] **Step 1: Write failing tests** (temp db file per test)

```csharp
[Fact] public async Task Save_then_find_roundtrips_all_fields() { /* populate every Ticket property, save, find by (project,key), Assert.Equivalent */ }
[Fact] public async Task Save_twice_updates_not_duplicates() { /* save, change StageId, save, ListAsync count == 1 */ }
[Fact] public async Task Events_are_returned_in_order() { }
[Fact] public async Task Reset_interrupted_moves_running_agent_stages_to_pending()
{
    // tickets: A Running at "plan", B Running at "build", C Pending at "plan"
    var n = await store.ResetInterruptedRunsAsync((_, stage) => stage == "plan", Now, default);
    Assert.Equal(1, n);
    Assert.Equal(StageStatus.Pending, (await store.FindAsync("orders", "A", default))!.Status);
    Assert.Equal(1, (await store.FindAsync("orders", "A", default))!.Attempts);
    Assert.Equal(StageStatus.Running, (await store.FindAsync("orders", "B", default))!.Status);
}
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter SqliteTicketStoreTests` — Expected: FAIL.
- [ ] **Step 3: Implement** with `Microsoft.EntityFrameworkCore.Sqlite`; store enums as strings; `DateTimeOffset` via `DateTimeOffsetToBinaryConverter` (SQLite can't order `DateTimeOffset` natively). Add migration: `dotnet ef migrations add Initial -p src/Symphonist.Integrations`. `SqliteTicketStore` calls `Database.MigrateAsync()` once (lazy).
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(storage): SQLite ticket store with restart recovery"`

---

### Task 6: Process runner and git workspace

**Files:**
- Create: `src/Symphonist.Integrations/Processes/ProcessRunner.cs`, `src/Symphonist.Core/Integrations/IGitWorkspace.cs`, `src/Symphonist.Integrations/Git/GitWorkspace.cs`
- Test: `tests/Symphonist.Integrations.Tests/Processes/ProcessRunnerTests.cs`, `tests/Symphonist.Integrations.Tests/Git/GitWorkspaceTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    string? StandardInput = null, IReadOnlyDictionary<string, string?>? Environment = null, TimeSpan? Timeout = null);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);
public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct); }
public sealed class ProcessRunner : IProcessRunner   // kills entire process tree on timeout or cancellation

public interface IGitWorkspace
{
    Task<string> PrepareAsync(ProjectConfig project, string ticketKey, string branch, CancellationToken ct); // returns workspace path
    Task<bool> CommitAllAsync(string workspace, string message, CancellationToken ct); // false when nothing changed
    Task PushAsync(string workspace, string branch, CancellationToken ct);
}
public sealed class GitWorkspace(IProcessRunner runner, string workRoot) : IGitWorkspace
public sealed class GitCommandException(string message) : Exception(message);
```

`PrepareAsync` — workspace = `Path.Combine(workRoot, project.Id, ticketKey)`:
- If it doesn't exist: `git clone <repo.LocalPath> <workspace>` (local clone, self-contained `.git`); then `git -C <ws> remote set-url origin <url of origin in repo.LocalPath>` (from `git -C <localPath> remote get-url origin`); `git fetch origin`; `git checkout -B <branch> origin/<mainBranch>` — unless `origin/<branch>` exists, then `git checkout -B <branch> origin/<branch>`.
- If it exists (rerun): `git fetch origin`; `git checkout <branch>`; do **not** reset local work.
`CommitAllAsync`: `git add -A`, `git status --porcelain` empty → false; else `git commit -m <message>` (developer's own git identity). `PushAsync`: `git push -u origin <branch>`. Non-zero exit → `GitCommandException` with stderr.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Passes_arguments_with_spaces_and_stdin_verbatim()
{ var r = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "echo", "a b \"c\""], StandardInput: "in put"), default);
  Assert.Equal(0, r.ExitCode); Assert.Equal("a b \"c\"|in put", r.StandardOutput.Trim()); }
[Fact] public async Task Times_out_and_kills()
{ var sw = Stopwatch.StartNew(); var r = await new ProcessRunner().RunAsync(new("dotnet", [SleeperDll, "sleep"], Timeout: TimeSpan.FromSeconds(1)), default);
  Assert.True(r.TimedOut); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); }
```
Helper: `tests/Sleeper` console project (add to the solution, referenced by Integrations.Tests with `ReferenceOutputAssembly=false` and copied to output). `echo` mode prints `args[1] + "|" + stdin`; `sleep` mode sleeps 60s.

Git tests use a temp "remote" bare repo and a "developer checkout" clone of it, with one commit on `main`; the developer checkout path contains a space (`"dev checkout"`):

```csharp
[Fact] public async Task Prepare_clones_and_branches_from_main_with_real_origin()
{ var ws = await git.PrepareAsync(Project, "ORD-1", "symphonist/ORD-1", default);
  Assert.Equal(BareRemotePath, Git(ws, "remote get-url origin")); Assert.Equal("symphonist/ORD-1", Git(ws, "branch --show-current")); }
[Fact] public async Task Prepare_twice_reuses_workspace_and_keeps_changes()
{ var ws = await git.PrepareAsync(Project, "ORD-1", "symphonist/ORD-1", default); File.WriteAllText(Path.Combine(ws, "x.txt"), "keep");
  var ws2 = await git.PrepareAsync(Project, "ORD-1", "symphonist/ORD-1", default); Assert.Equal(ws, ws2); Assert.True(File.Exists(Path.Combine(ws, "x.txt"))); }
[Fact] public async Task Commit_returns_false_when_clean() { }
[Fact] public async Task Commit_and_push_puts_branch_on_remote() { /* then `git -C bare branch --list symphonist/ORD-1` non-empty */ }
[Fact] public async Task Prepare_after_workspace_deleted_continues_from_pushed_branch() { /* push, delete ws dir, prepare -> file from pushed commit exists */ }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter "ProcessRunnerTests|GitWorkspaceTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.** `ProcessRunner`: `ProcessStartInfo.ArgumentList`, redirect all three streams, read stdout/stderr concurrently, write `StandardInput` then close stdin, `Kill(entireProcessTree: true)` on timeout/cancel. Tests set `GIT_AUTHOR_NAME/EMAIL` + `GIT_COMMITTER_*` via env on the test process so CI machines without git identity work.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: process runner and per-ticket git workspace"`

---

### Task 7: Claude Code agent runner (host launcher)

**Files:**
- Create: `src/Symphonist.Core/Agents/IAgentRunner.cs`, `src/Symphonist.Integrations/ClaudeCode/{ClaudeCodeRunner,IAgentLauncher}.cs`
- Create fixtures: `tests/Symphonist.Integrations.Tests/Fixtures/claude-success.json`, `claude-error.json`
- Test: `tests/Symphonist.Integrations.Tests/ClaudeCode/ClaudeCodeRunnerTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessSpec`, `AgentSettings`.
- Produces:

```csharp
public sealed record StageOutput(string Status, string Summary, string? CommentMarkdown, string? SpecPath,
    string? PlanPath, string? PrTitle, string? PrDescription, bool? TestsPassed);   // Status: "completed" | "failed"
public static class StageOutputSchema { public const string Json = """ ... """; } // JSON Schema for StageOutput, snake_case properties, required: status, summary
public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, AgentSettings Settings);
public sealed record AgentRunResult(bool Succeeded, StageOutput? Output, decimal? CostUsd, TimeSpan Duration, string? Error, string RawLog);
public interface IAgentRunner { Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct); }

public interface IAgentLauncher { ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request); }
public sealed class HostLauncher : IAgentLauncher   // returns command unchanged; WorkingDirectory = request.WorkingDirectory
public sealed class ClaudeCodeRunner(IProcessRunner runner, IAgentLauncher launcher) : IAgentRunner
```

Claude command (`FileName = "claude"`, prompt on **stdin**):
`-p --output-format json --json-schema <StageOutputSchema.Json>` plus
- container isolation: `--dangerously-skip-permissions`
- host isolation: `--allowedTools <string.Join(",", AllowedTools)>` (no bypass flag)
- `--model <Model>` when set.
Timeout = `Settings.TimeoutMinutes`.

Result parsing: stdout is one JSON object. `Succeeded` = exit 0, not timed out, `is_error` false, structured output present and `status == "completed"`. Structured output = `structured_output` property if present, else `result` string parsed as JSON. `CostUsd` = `total_cost_usd`. `Error` = first of: "timed out after N minutes", stderr tail, `result` text, "missing structured output", output `summary` when status `failed`.

- [ ] **Step 1: Capture real fixture** (requires logged-in `claude`):

```bash
echo 'Reply with status completed and summary "ok".' | claude -p --output-format json --json-schema "$(cat schema.json)" > tests/Symphonist.Integrations.Tests/Fixtures/claude-success.json
```
Inspect it: confirm where the structured output lives (`structured_output` vs `result`) and the cost field name; adjust the parsing note above if they differ. Hand-write `claude-error.json` with `"is_error": true, "result": "API Error: overloaded"`.

- [ ] **Step 2: Write failing tests** (fake `IProcessRunner` records the spec and returns canned results)

```csharp
[Fact] public async Task Container_mode_bypasses_permissions_and_sends_prompt_on_stdin()
{ await runner.RunAsync(Request(Isolation.Container), default);
  Assert.Equal("claude", fake.Last.FileName); Assert.Contains("--dangerously-skip-permissions", fake.Last.Arguments);
  Assert.Equal("do the thing", fake.Last.StandardInput); Assert.DoesNotContain("do the thing", fake.Last.Arguments); }

[Fact] public async Task Host_mode_uses_allowlist_and_never_bypasses()
{ await runner.RunAsync(Request(Isolation.Host, tools: ["Read", "Bash(dotnet test:*)"]), default);
  Assert.DoesNotContain("--dangerously-skip-permissions", fake.Last.Arguments);
  Assert.Equal("Read,Bash(dotnet test:*)", ArgAfter(fake.Last, "--allowedTools")); }

[Fact] public async Task Parses_success_fixture() { fake.Returns(0, Fixture("claude-success.json")); var r = await runner.RunAsync(Request(), default);
  Assert.True(r.Succeeded); Assert.Equal("ok", r.Output!.Summary); Assert.NotNull(r.CostUsd); }

[Theory]
[InlineData("exit-1")] [InlineData("is-error")] [InlineData("no-structured-output")] [InlineData("status-failed")] [InlineData("timeout")] [InlineData("not-json")]
public async Task Bad_runs_fail_with_error(string variant) { /* each variant -> Succeeded false, Error non-empty */ }
```

- [ ] **Step 3: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter ClaudeCodeRunnerTests` — Expected: FAIL.
- [ ] **Step 4: Implement.** Deserialize `StageOutput` with `JsonNamingPolicy.SnakeCaseLower`.
- [ ] **Step 5: Run** — Expected: PASS.
- [ ] **Step 6: Commit** `git commit -m "feat(agents): Claude Code headless runner"`

---

### Task 8: Docker launcher, agent image, network allowlist

**Files:**
- Create: `src/Symphonist.Integrations/Containers/DockerLauncher.cs`, `src/Symphonist.Integrations/Containers/agent/{Dockerfile,entrypoint.sh,init-firewall.sh}` (`CopyToOutputDirectory=PreserveNewest`, LF line endings via `.gitattributes`: `*.sh text eol=lf`)
- Test: `tests/Symphonist.Integrations.Tests/Containers/DockerLauncherTests.cs`, `tests/Symphonist.Integrations.Tests/Containers/AgentImageTests.cs` (`[Trait("Category","Docker")]`)

**Interfaces:**
- Consumes: `IAgentLauncher`, `ProcessSpec`, `AgentRunRequest`, `IProcessRunner`.
- Produces:

```csharp
public sealed class DockerLauncher(IProcessRunner runner, string agentContextDirectory, Func<string, string?> getSecret) : IAgentLauncher
{
    public const string DefaultImage = "symphonist/agent-claude:1";
    public static readonly IReadOnlyList<string> BaseAllowedDomains; // api.anthropic.com, statsig.anthropic.com, sentry.io
    public Task EnsureImageAsync(string image, CancellationToken ct); // `docker image inspect`; if missing and image == DefaultImage, `docker build -t` from agentContextDirectory
    public ProcessSpec Wrap(ProcessSpec agentCommand, AgentRunRequest request);
}
```

`Wrap` produces `FileName="docker"`, arguments in this order:
`run --rm -i --cap-add=NET_ADMIN --cap-add=NET_RAW -v <WorkingDirectory>:/workspace -w /workspace -e CLAUDE_CODE_OAUTH_TOKEN -e SYMPHONIST_ALLOWED_DOMAINS <image> <agentCommand.FileName> <agentCommand.Arguments...>`
- `-e NAME` without a value — the values go in `ProcessSpec.Environment` (`CLAUDE_CODE_OAUTH_TOKEN = getSecret("claude-code-token")`, `SYMPHONIST_ALLOWED_DOMAINS` = base + `Settings.AllowedDomains`, comma-joined), so the token never appears in a process listing.
- `StandardInput`, `Timeout` copied through. Image = `Settings.Image ?? DefaultImage`.
- No other mounts, no host env, no Git/tracker secrets.

Image: `FROM mcr.microsoft.com/dotnet/sdk:10.0`; apt: `git curl ca-certificates iptables ipset dnsutils jq nodejs npm chromium`; `npm install -g @anthropic-ai/claude-code`; non-root user `agent` (uid 1000); `ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]`. `entrypoint.sh`: run `init-firewall.sh` as root, then `exec setpriv --reuid=agent --regid=agent --init-groups "$@"`. `init-firewall.sh` (adapted from Claude Code's reference devcontainer firewall): flush, allow loopback + DNS (udp/tcp 53) + established; create ipset `allowed`; for each domain in `$SYMPHONIST_ALLOWED_DOMAINS` add `dig +short A` results; `OUTPUT` allow `--match-set allowed dst`; default policy `DROP`; exit non-zero if any base domain fails to resolve.

- [ ] **Step 1: Write failing unit tests**

```csharp
[Fact] public void Wrap_mounts_only_workspace_and_passes_secrets_by_env_name()
{ var spec = launcher.Wrap(new("claude", ["-p"]), Request(@"C:\work\orders\ORD 1"));
  Assert.Equal("docker", spec.FileName);
  Assert.Equal(1, spec.Arguments.Count(a => a == "-v"));
  Assert.Contains(@"C:\work\orders\ORD 1:/workspace", spec.Arguments);
  Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", spec.Arguments); Assert.DoesNotContain(spec.Arguments, a => a.Contains("tok-123"));
  Assert.Equal("tok-123", spec.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
  Assert.Equal("api.anthropic.com,statsig.anthropic.com,sentry.io,api.nuget.org", spec.Environment["SYMPHONIST_ALLOWED_DOMAINS"]);
  Assert.Equal(["claude", "-p"], spec.Arguments.TakeLast(2)); }

[Fact] public void Wrap_uses_project_image_override() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter DockerLauncherTests` — Expected: FAIL. **Step 3: Implement** `DockerLauncher`. **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Write Docker integration tests** (`Category=Docker`; skip when `docker info` fails):

```csharp
[Fact] public async Task Image_builds_and_has_tools() { /* EnsureImageAsync; run `claude --version`, `dotnet --version`, `git --version` inside -> exit 0 */ }
[Fact] public async Task Firewall_blocks_unlisted_and_allows_listed()
{ /* run `curl -sS -m 5 https://example.com` -> non-zero exit; `curl -sS -m 10 https://api.nuget.org/v3/index.json` -> 0 */ }
[Fact] public async Task Runs_as_non_root_with_workspace_writable() { /* `id -u` -> "1000"; `touch /workspace/x` -> file appears on host */ }
```
Files written in the workspace must stay owned by the developer on Linux/macOS. `--user` won't work (firewall setup needs root), so on non-Windows `Wrap` also adds `-e SYMPHONIST_UID -e SYMPHONIST_GID` with values from libc `getuid()`/`getgid()` (P/Invoke), and `entrypoint.sh` runs `usermod -u`/`groupmod -g` on `agent` before dropping privileges. Add a unit test (skipped on Windows) asserting `SYMPHONIST_UID` is in `Environment`; on Windows neither variable is passed and the image default 1000 is used.

- [ ] **Step 6: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter Category=Docker` — Expected: PASS.
- [ ] **Step 7: Commit** `git commit -m "feat(containers): sandboxed agent image with network allowlist"`

---

### Task 9: Jira tracker

**Files:**
- Create: `src/Symphonist.Core/Integrations/ITracker.cs`, `src/Symphonist.Integrations/Jira/{JiraTracker,Adf}.cs`
- Create: `tests/Symphonist.Integrations.Tests/Http/StubHttpHandler.cs`, fixtures `jira-search.json`, `jira-transitions.json`
- Test: `tests/Symphonist.Integrations.Tests/Jira/JiraTrackerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record SourceComment(string Author, bool IsMine, DateTimeOffset Created, string Body);
public sealed record SourceTicket(string Key, string Title, string Description, string Status, IReadOnlyList<SourceComment> Comments, Uri WebUrl);
public interface ITracker
{
    Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct);  // all pages; throws on any HTTP failure (never partial)
    Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct); // false when no transition leads to targetStatus
    Task CommentAsync(string key, string markdown, CancellationToken ct);
}
public sealed class JiraTracker(HttpClient http, TrackerConfig config, string apiToken) : ITracker
public static class Adf { public static string ToPlainText(JsonElement adf); public static object FromMarkdown(string markdown); }

// test helper
public sealed class StubHttpHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; }
    public StubHttpHandler On(HttpMethod method, string pathPrefix, HttpStatusCode status, string body); // first registered match wins; unmatched -> 404
}
```

Jira Cloud REST v3, basic auth `email:apiToken`, base `TrackerConfig.Site`:
- `GET /rest/api/3/myself` once (cache `accountId`) → `IsMine` = comment author accountId match.
- `POST /rest/api/3/search/jql` body `{ "jql": <Jql>, "fields": ["summary","description","status","comment"], "maxResults": 50, "nextPageToken": <token or omitted> }`; loop while `nextPageToken` present.
- Transition: `GET /rest/api/3/issue/{key}/transitions`; pick the one with `to.name` equal (case-insensitive) to `targetStatus`; none → return `false`; else `POST` same path `{ "transition": { "id": ... } }`.
- Comment: `POST /rest/api/3/issue/{key}/comment` `{ "body": Adf.FromMarkdown(markdown) }`. `FromMarkdown`: one ADF paragraph per blank-line-separated block, plain text (rich formatting is a later improvement).
- `WebUrl` = `{Site}/browse/{key}`. Description/comment bodies via `Adf.ToPlainText` (concatenate `text` nodes, newline between block nodes).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Query_follows_pages_and_maps_fields() { /* page1 has nextPageToken "p2", page2 none; assert 3 tickets, second request body contains "p2", Status == "Planned", Description plain text, comment IsMine true for own accountId */ }
[Fact] public async Task Query_throws_on_http_error() { /* 500 on page 2 -> HttpRequestException */ }
[Fact] public async Task Transition_posts_matching_id_case_insensitive() { /* target "in review" matches to.name "In Review" id "31" -> POST body contains "31", returns true */ }
[Fact] public async Task Transition_returns_false_when_not_available() { /* no match -> false, no POST sent */ }
[Fact] public async Task Comment_posts_adf_paragraphs() { /* "a\n\nb" -> body has two paragraph nodes */ }
[Fact] public async Task Sends_basic_auth_header() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter JiraTrackerTests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(jira): Jira Cloud tracker"`

---

### Task 10: Bitbucket Cloud source control and Pipelines watcher

**Files:**
- Create: `src/Symphonist.Core/Integrations/{ISourceControl,IBuildWatcher}.cs`, `src/Symphonist.Integrations/Bitbucket/{BitbucketSourceControl,BitbucketPipelinesWatcher}.cs`
- Fixtures: `bb-pr-create.json`, `bb-pr-merged.json`, `bb-pipelines.json`
- Test: `tests/Symphonist.Integrations.Tests/Bitbucket/BitbucketTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum PullRequestState { Open, Merged, Declined }
public sealed record PullRequestInfo(long Id, Uri WebUrl, PullRequestState State, string? MergeCommitHash);
public sealed record MergeResult(bool Merged, string? MergeCommitHash, string? Error);
public interface ISourceControl
{
    Task<PullRequestInfo> OpenPullRequestAsync(string sourceBranch, string destinationBranch, string title, string description, CancellationToken ct);
    Task<PullRequestInfo?> FindOpenPullRequestAsync(string sourceBranch, string destinationBranch, CancellationToken ct);
    Task<PullRequestInfo> GetPullRequestAsync(long id, CancellationToken ct);
    Task<MergeResult> MergePullRequestAsync(long id, CancellationToken ct); // Merged=false + Error when host refuses (e.g. missing approvals)
    Uri BranchFileUrl(string branch, string path);
}
public enum BuildStatus { NotFound, Running, Succeeded, Failed }
public interface IBuildWatcher { Task<BuildStatus> GetStatusAsync(string commitHash, CancellationToken ct); }
public sealed class BitbucketSourceControl(HttpClient http, RepoConfig config, string apiToken) : ISourceControl
public sealed class BitbucketPipelinesWatcher(HttpClient http, RepoConfig config, string apiToken) : IBuildWatcher
```

Bitbucket Cloud API 2.0, base `https://api.bitbucket.org/2.0/repositories/{workspace}/{slug}`, basic auth `email:apiToken` (Bitbucket API token):
- Open: `POST /pullrequests` `{ title, description, source: { branch: { name } }, destination: { branch: { name } }, close_source_branch: false }`.
- Find: `GET /pullrequests?state=OPEN&q=source.branch.name="<src>" AND destination.branch.name="<dst>"` (URL-encoded) → first or null.
- Get: `GET /pullrequests/{id}`; `state` OPEN/MERGED/DECLINED (SUPERSEDED → Declined); `merge_commit.hash`; `links.html.href`.
- Merge: `POST /pullrequests/{id}/merge` `{ merge_strategy: <MergeStrategy> }`; 200 → hash from `merge_commit.hash`; 4xx → `Merged=false`, `Error` = response `error.message`.
- `BranchFileUrl` = `https://bitbucket.org/{workspace}/{slug}/src/{branch}/{path}`.
- Pipelines: `GET /pipelines/?sort=-created_on&pagelen=50`; first item with `target.commit.hash` starting with the given hash; none → `NotFound`; `state.name` PENDING/IN_PROGRESS → `Running`; COMPLETED + `state.result.name` SUCCESSFUL → `Succeeded`, else `Failed`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Open_pr_posts_branches_and_maps_response() { }
[Fact] public async Task Find_open_pr_encodes_query() { /* request path contains q=source.branch.name%3D%22symphonist%2FORD-1%22 */ }
[Fact] public async Task Merge_success_returns_hash() { }
[Fact] public async Task Merge_refused_returns_error_not_throw() { /* 400 {"error":{"message":"Requires 2 approvals"}} -> Merged false, Error "Requires 2 approvals" */ }
[Theory]
[InlineData("abc123", BuildStatus.Succeeded)] [InlineData("def456", BuildStatus.Running)] [InlineData("bad999", BuildStatus.Failed)] [InlineData("zzz000", BuildStatus.NotFound)]
public async Task Pipeline_status_by_commit(string hash, BuildStatus expected) { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter BitbucketTests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(bitbucket): PRs, merge, and Pipelines status"`

---

### Task 11: Stage executor

**Files:**
- Create: `src/Symphonist.Core/Integrations/IIntegrationFactory.cs`, `src/Symphonist.Host/Orchestration/StageExecutor.cs`
- Create fakes: `tests/Symphonist.Host.Tests/Fakes/{FakeTracker,FakeSourceControl,FakeBuildWatcher,FakeGitWorkspace,FakeAgentRunner}.cs`
- Test: `tests/Symphonist.Host.Tests/Orchestration/StageExecutorTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–10.
- Produces:

```csharp
public sealed record ProjectIntegrations(ITracker Tracker, ISourceControl SourceControl, IBuildWatcher BuildWatcher);
public interface IIntegrationFactory { ProjectIntegrations Create(ProjectConfig project); }

public enum ExecutionStatus { Completed, InProgress, Failed }
public sealed record ExecutionResult(ExecutionStatus Status, string? Error = null, string? Event = null);

public sealed class StageExecutor(IGitWorkspace git, IAgentRunner agent, ITicketStore store, TimeProvider time)
{
    public static bool IsAgentStage(StageKind kind);  // Triage, Plan, Implement
    public Task<ExecutionResult> ExecuteAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, SourceTicket? source, CancellationToken ct);
    public Task<ExecutionResult> PostApprovalAsync(ProjectConfig p, ProjectIntegrations io, Ticket t, CancellationToken ct);
}
```

`ExecuteAsync` by stage kind (mutates `t` fields; caller applies engine transitions):
- **Triage**: `git.PrepareAsync`; render triage prompt; run agent; on success `Tracker.CommentAsync(output.CommentMarkdown ?? output.Summary)`.
- **Plan**: prepare; run; `CommitAllAsync($"{key}: spec and plan")`; `PushAsync`; set `t.SpecPath/PlanPath`; comment: summary + `BranchFileUrl` links to both files.
- **Implement**: prepare; run with `spec_path`/`plan_path`; commit `$"{key}: {output.PrTitle ?? title}"`; push; if `t.PullRequestId` null → `FindOpenPullRequestAsync(branch, MainBranch)` ?? `OpenPullRequestAsync(branch, MainBranch, $"{key}: {PrTitle}", PrDescription + link to ticket)`; set `t.PullRequestId`; comment "PR ready for review: <url>". On rerun with existing PR: push only, comment "PR updated".
- **Build**: `MergeCommit` null → Failed "no merge commit". `BuildWatcher.GetStatusAsync(MergeCommit)`: Succeeded → Completed; Failed → Failed "build failed"; Running → InProgress; NotFound → InProgress until `StageStartedAt + Build.TimeoutMinutes`, then Failed "no pipeline ran for <hash>".
- **Uat**: Completed.
- **Release**: if `ReleasePullRequestId` null → find or open PR `MainBranch → ProductionBranch` titled `$"Release {key}"`; set id; comment link. Completed.
- **Deploy**: same as Build using `ReleaseMergeCommit`.
- **Done**: Completed.
- Agent stages record an `AgentRunRecord` (raw log written to `SymphonistPaths.Resolve("logs", projectId, key, $"{stage}-{timestamp}.json")`). Agent `Succeeded=false` → Failed with its `Error`. Any `GitCommandException`/`HttpRequestException` → Failed with message.

Prompt values: `ticket.key`, `ticket.title`, `ticket.description`, `ticket.comments` (each `"{Author}: {Body}"`, newest last), `ticket.url`, `project.name`, `branch`, `feedback` = `t.Feedback`, `spec_path`, `plan_path`.

`PostApprovalAsync`:
- **Implement**: `MergePullRequestAsync(PullRequestId)`; merged → `t.MergeCommit`, Completed; refused → InProgress with `Event = $"waiting to merge: {error}"` (retried next tick — this is how branch rules / client approvals are waited on). If PR already `Merged` (merged by hand) → take its hash, Completed.
- **Release**: same with `ReleasePullRequestId` → `ReleaseMergeCommit`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Plan_commits_pushes_and_comments_links() { /* fake agent returns spec_path docs/specs/a.md; assert git commit+push called, t.SpecPath set, tracker comment contains BranchFileUrl */ }
[Fact] public async Task Implement_opens_pr_once_and_reuses_on_rerun() { /* run twice -> OpenPullRequestAsync called once, second comment "PR updated" */ }
[Fact] public async Task Implement_reuses_open_pr_found_on_host() { /* t.PullRequestId null, FindOpen returns #7 -> no open call, t.PullRequestId 7 */ }
[Fact] public async Task Feedback_reaches_prompt() { /* t.Feedback "use X" -> fake agent prompt contains "use X" */ }
[Fact] public async Task Agent_failure_returns_failed_with_error() { }
[Fact] public async Task Build_not_found_times_out() { /* StageStartedAt 46 min ago -> Failed "no pipeline ran" ; 10 min ago -> InProgress */ }
[Fact] public async Task Merge_refused_waits() { /* MergeResult(false,null,"Requires 2 approvals") -> InProgress, Event contains it */ }
[Fact] public async Task Merge_already_done_by_hand_completes() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter StageExecutorTests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(host): stage executor"`

---

### Task 12: Orchestrator tick and background service

**Files:**
- Create: `src/Symphonist.Host/Orchestration/{Orchestrator,OrchestratorService,ProjectRegistry}.cs`
- Test: `tests/Symphonist.Host.Tests/Orchestration/OrchestratorTests.cs`

**Interfaces:**
- Consumes: `ProjectConfigLoader`, `ITicketStore`, `IIntegrationFactory`, `StageExecutor`, `PipelineEngine`.
- Produces:

```csharp
public sealed class ProjectRegistry   // loads SymphonistPaths.Resolve("projects") at startup; Reload()
{
    public IReadOnlyList<ProjectConfig> Projects { get; }
    public ProjectConfig Get(string id);
    public bool IsPaused(string id); public void SetPaused(string id, bool paused); // persisted in SymphonistPaths.Resolve("paused.json")
}
public sealed record OrchestratorOptions(TimeSpan PollInterval, int MaxConcurrentAgentRuns); // defaults 2 min, 1
public sealed class Orchestrator(ProjectRegistry projects, ITicketStore store, IIntegrationFactory integrations,
    StageExecutor executor, TimeProvider time, OrchestratorOptions options, ILogger<Orchestrator> log)
{
    public Task RecoverAsync(CancellationToken ct);  // store.ResetInterruptedRunsAsync with IsAgentStage
    public Task TickAsync(CancellationToken ct);     // one full pass (below); also callable via API "poll"
    public Task WhenIdleAsync();                     // completes when no agent runs in flight (tests)
    public Task<TransitionOutcome> ApproveAsync(string projectId, string key, CancellationToken ct);
    public Task<TransitionOutcome> SendBackAsync(string projectId, string key, string feedback, string? toStageId, CancellationToken ct);
    public Task<TransitionOutcome> UnblockAsync(string projectId, string key, CancellationToken ct);
}
public sealed class OrchestratorService(Orchestrator o, OrchestratorOptions options) : BackgroundService // RecoverAsync once, then TickAsync every PollInterval; never dies on exceptions (log + continue)
```

`TickAsync`, per non-paused project (a `SemaphoreSlim` serializes ticks):
1. **Sync**: `Tracker.QueryAsync`. On exception: log, add no events, skip step 1 for this project (Review Focus 4: a failed query blocks nothing).
   - New key → `PipelineEngine.Create`, save, event "picked up at <stage>".
   - Known key, status differs from `LastSourceStatus` → `SourceStatusChanged(..., latestFeedback)` where `latestFeedback` = body of newest `IsMine` comment created after `WaitingSince` (null if none).
   - Known active ticket (not `Done`/`Blocked`) missing from results → `Block("no longer matches tracker query")`.
2. **Advance** each active ticket:
   - `Pending` + agent stage → if in-flight runs < `MaxConcurrentAgentRuns`: set `Running`, save, start `ExecuteAsync` as a background task (tracked; tickets already in flight are skipped). On completion apply `StageSucceeded`/`StageFailed`, save.
   - `Pending`/`Running` + non-agent stage → `ExecuteAsync` inline: Completed → `StageSucceeded`; InProgress → set `Running`; Failed → `StageFailed`.
   - `Approved` → `PostApprovalAsync`: Completed → `PostApprovalCompleted`; InProgress → record `Event` once (don't repeat identical consecutive events).
3. **Apply outcomes**: every outcome with `SourceStatusToSet` → `Tracker.TransitionAsync`; `false` → event `no transition to '<status>' in tracker`; exception → event and continue. Every changed outcome → event with `Message`.

Approve/SendBack/Unblock methods load ticket, apply the engine method, save, apply step 3, and return the outcome. Unknown ticket → `KeyNotFoundException`.

- [ ] **Step 1: Write failing tests** (fakes from Task 11; `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`)

```csharp
[Fact] public async Task New_ticket_is_picked_up_at_mapped_stage() { /* tracker returns ORD-1 "Planned" -> store has ORD-1 at plan */ }
[Fact] public async Task Plan_runs_then_waits_for_gate_and_sets_no_status() { /* Tick; WhenIdle; Tick -> AwaitingApproval; plan has no OnGateStatus -> no transition call */ }
[Fact] public async Task Jira_move_to_In_Progress_approves_plan() { /* plan AwaitingApproval; tracker now reports "In Progress" -> ticket at implement, Pending */ }
[Fact] public async Task Missing_transition_records_event_and_keeps_moving() { /* fake TransitionAsync false -> event "no transition to 'In Review' in tracker", ticket still AwaitingApproval */ }
[Fact] public async Task Ticket_leaving_query_is_blocked_but_failed_query_blocks_nothing() { }
[Fact] public async Task Respects_max_concurrent_agent_runs() { /* 3 Pending plan tickets, max 1, fake agent blocks on TaskCompletionSource -> only 1 Running */ }
[Fact] public async Task Send_back_via_cli_reruns_with_feedback() { /* SendBackAsync("orders","ORD-1","use X",null) -> Pending, next run prompt contains "use X" */ }
[Fact] public async Task Restart_recovery_resets_running_agent_tickets() { }
[Fact] public async Task Full_pipeline_end_to_end_with_fakes()
{   // ORD-1 "Backlog": triage -> approve -> plan -> approve -> implement -> approve -> merge -> build(Succeeded) -> release -> approve -> merge -> deploy(Succeeded) -> done
    // assert final Status Done, PR opened twice (feature + release), two merges, tracker transitions include "Planned","In Progress","In Review","Done"
}
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter OrchestratorTests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(host): orchestrator loop"`

---

### Task 13: Host wiring, secrets, local API

**Files:**
- Create: `src/Symphonist.Core/Integrations/ISecretStore.cs`, `src/Symphonist.Integrations/Secrets/CredentialManagerSecretStore.cs`, `src/Symphonist.Integrations/IntegrationFactory.cs`, `src/Symphonist.Integrations/ClaudeCode/IsolationLauncher.cs`, `src/Symphonist.Host/Api/{TicketsApi,ApiTokenMiddleware}.cs`
- Modify: `src/Symphonist.Host/Program.cs`
- Test: `tests/Symphonist.Host.Tests/Api/TicketsApiTests.cs`, `tests/Symphonist.Integrations.Tests/Secrets/SecretStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface ISecretStore { string? Get(string name); void Set(string name, string value); void Remove(string name); }
public sealed class CredentialManagerSecretStore : ISecretStore
// Get: env var SYMPHONIST_SECRET_<NAME upper, '-'->'_'> wins (CI/headless); else Devlooped.CredentialManager:
// CredentialManager.Create("symphonist"), service "symphonist", account = name.
public sealed class IntegrationFactory(IHttpClientFactory http, ISecretStore secrets) : IIntegrationFactory
// throws InvalidOperationException($"secret '{name}' not set; run: symphonist secret set {name}") when missing
public static class ApiToken { public static string LoadOrCreate(string path); public const string Header = "X-Symphonist-Token"; }
```

HTTP API (all JSON, all require `X-Symphonist-Token`; `401` otherwise). Kestrel binds `http://127.0.0.1:{port}`, port from `SYMPHONIST_PORT` env, default `5199`; token file `SymphonistPaths.Resolve("api-token")` (32 random bytes, base64url; created with user-only permissions — `File.SetUnixFileMode(UserRead|UserWrite)` on non-Windows).

| Method | Path | Body | Returns |
|---|---|---|---|
| GET | `/api/tickets?project=` | — | `TicketDto[]` (key, project, title, stage, status, blockedReason, attempts, waitingSince, prUrl) |
| GET | `/api/tickets/{project}/{key}` | — | `TicketDto` + `events[]` |
| POST | `/api/tickets/{project}/{key}/approve` | — | `TransitionOutcome` |
| POST | `/api/tickets/{project}/{key}/send-back` | `{ "feedback": "...", "toStage": null }` | `TransitionOutcome` |
| POST | `/api/tickets/{project}/{key}/unblock` | — | `TransitionOutcome` |
| POST | `/api/poll` | — | `202` (runs `TickAsync` in background) |
| GET | `/api/projects` | — | `[{ id, name, paused, isolation }]` |
| POST | `/api/projects/{id}/pause` · `/resume` | — | `204` |

Unknown ticket/project → `404`. `Program.cs` registers: `ServiceDefaults`, `ISecretStore`, `IHttpClientFactory`, `IntegrationFactory`, `SqliteTicketStore(SymphonistPaths.Resolve("symphonist.db"))`, `ProcessRunner`, `GitWorkspace(..., SymphonistPaths.Resolve("work"))`, `ClaudeCodeRunner` with a launcher chosen **per run** from `Settings.Isolation` (`DockerLauncher` or `HostLauncher` — add a small `IsolationLauncher : IAgentLauncher` that delegates), `StageExecutor`, `ProjectRegistry`, `Orchestrator`, `OrchestratorService`. On startup, if any project uses container isolation, `DockerLauncher.EnsureImageAsync` (log a clear error if Docker isn't available; those projects' agent stages fail with "Docker not available").

- [ ] **Step 1: Write failing tests** with `WebApplicationFactory<Program>` (fake orchestrator dependencies, temp `SYMPHONIST_HOME`):

```csharp
[Fact] public async Task Rejects_missing_token() => Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);
[Fact] public async Task Lists_tickets_with_token() { }
[Fact] public async Task Approve_unknown_ticket_is_404() { }
[Fact] public async Task Send_back_passes_feedback() { }
[Fact] public async Task Token_file_is_user_only_on_unix() { /* skip on Windows; File.GetUnixFileMode == UserRead|UserWrite */ }
// SecretStoreTests:
[Fact] public void Env_override_wins() { /* SYMPHONIST_SECRET_JIRA_ACME=abc -> Get("jira-acme") == "abc" */ }
[Fact, Trait("Category","Live")] public void Roundtrips_through_os_store() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Host.Tests --filter TicketsApiTests` and `dotnet test tests/Symphonist.Integrations.Tests --filter SecretStoreTests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(host): wiring, secrets, localhost API"`

---

### Task 14: CLI tool

**Files:**
- Create: `src/Symphonist.Cli/{Program,SymphonistClient}.cs`
- Modify: `src/Symphonist.Cli/Symphonist.Cli.csproj` (`PackAsTool=true`, `ToolCommandName=symphonist`)
- Test: `tests/Symphonist.Cli.Tests/CliTests.cs`

**Interfaces:**
- Consumes: HTTP API from Task 13, `ISecretStore`, `ApiToken.Header`, `SymphonistPaths`.
- Produces commands (System.CommandLine):

| Command | Does |
|---|---|
| `symphonist status [--project <id>]` | table: KEY, PROJECT, STAGE, STATUS, WAITING (age), NOTE (blocked reason) — rows waiting on you first |
| `symphonist show <project> <key>` | ticket + events |
| `symphonist approve <project> <key>` | POST approve; prints outcome message |
| `symphonist send-back <project> <key> -m <feedback> [--to <stage>]` | POST send-back |
| `symphonist unblock <project> <key>` | POST unblock |
| `symphonist poll` | POST poll |
| `symphonist projects [pause\|resume <id>]` | list / pause / resume |
| `symphonist secret set <name>` | reads value from stdin (no echo when interactive) → `ISecretStore.Set` directly (no host needed) |

Exit codes: `0` success, `1` API error (prints server message), `2` host not running (`"Symphonist host is not running on 127.0.0.1:<port>"`).

- [ ] **Step 1: Write failing tests** (`SymphonistClient` takes an `HttpMessageHandler`; use `StubHttpHandler` copied or referenced)

```csharp
[Fact] public async Task Status_lists_waiting_tickets_first() { /* output first data row is AwaitingApproval ticket */ }
[Fact] public async Task Approve_sends_token_header() { }
[Fact] public async Task Send_back_requires_message() { /* exit code != 0, "Option '-m' is required" */ }
[Fact] public async Task Host_down_exits_2_with_message() { }
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Cli.Tests` — Expected: FAIL. **Step 3: Implement.** **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Verify packaging** `dotnet pack src/Symphonist.Cli -o artifacts && dotnet tool install --tool-path artifacts/tool --add-source artifacts Symphonist.Cli && artifacts/tool/symphonist --help` — Expected: help lists all commands.
- [ ] **Step 6: Commit** `git commit -m "feat(cli): symphonist command-line tool"`

---

### Task 15: Real end-to-end run and runbook

**Files:**
- Create: `README.md` (setup + runbook), `samples/projects/orders.example.yaml` (the Task 2 fixture with placeholder values)

- [ ] **Step 1: Write README** sections: prerequisites (.NET 10, Docker-compatible engine, `claude` CLI logged in, git credentials for Bitbucket); one-time setup (`claude setup-token` → `symphonist secret set claude-code-token`; `symphonist secret set jira-<id>`; `symphonist secret set bitbucket-<id>` with a Bitbucket API token scoped to repositories read/write, pull requests read/write, pipelines read); copy the example YAML to `~/.symphonist/projects/`; start host (`dotnet run --project src/Symphonist.Host`, or the AppHost during development); daily use (`symphonist status`, `approve`, `send-back`); troubleshooting (blocked tickets, "no transition" events, Docker not running).
- [ ] **Step 2: Live run on a throwaway ticket** in the real client Jira + Bitbucket (or a sandbox Jira project + Bitbucket repo with a trivial `bitbucket-pipelines.yml`): label a low-risk ticket `Claude`, assigned to you, in Backlog. Walk it through every stage approving via both CLI and Jira moves at least once each, and one `send-back -m`. Expected: ticket reaches `Done`; Jira shows triage comment, plan links, PR link, release PR link; Bitbucket shows feature PR merged to `main` and release PR merged to `production`; `symphonist show` lists every transition.
- [ ] **Step 3: Record findings** — anything that needed a manual fix goes into `docs/superpowers/notes/2026-xx-xx-first-live-run.md` as input for plan 2+.
- [ ] **Step 4: Commit** `git add README.md samples docs && git commit -m "docs: setup and runbook; first live run notes"`
