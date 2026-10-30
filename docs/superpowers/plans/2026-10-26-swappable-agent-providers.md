# Swappable Agent Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude Code and OpenAI Codex become interchangeable agent providers for every agent job, chosen per project with per-stage, reviewer and evidence overrides; Codex runs only in containers and signs in with an API key or the developer's ChatGPT login.

**Architecture:** Core config learns provider types, a per-stage agent override and Codex sign-in settings. Integrations gets a `CodexRunner` beside `ClaudeCodeRunner`, both finishing through one shared structured-result helper, and `DockerLauncher` becomes provider-neutral (per-provider base domains and credentials, extra bind mounts, env forwarding). Host callers resolve the runner per job through `IAgentRunners`; setup checks, form, writer and setup screen cover every provider a project uses.

**Tech Stack:** .NET 10, YamlDotNet, System.Text.Json, Docker, Codex CLI (`codex exec`, verified against codex-cli 0.153.2), Claude Code CLI, xUnit, bUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-swappable-agent-providers-design.md`

## Global Constraints

- .NET 10 (`net10.0`); Windows, macOS, Linux. Launch processes with `ArgumentList` (`ProcessSpec`), build paths with `Path.Combine`.
- Provider type strings exactly `claude-code` and `codex`.
- Codex runs only in containers: any job naming `codex` in a project with `isolation: host` is a load error, and `CodexRunner` fails closed when the launcher does not report `ProvidesIsolation`.
- A run receives only its own provider's credential (Claude token only for `claude-code`; OpenAI key or ChatGPT `auth.json` only for `codex`).
- Secrets stay in the OS credential store; `agent.codex.apiKeySecret` names one (default `openai-api-key`).
- Existing project files load to the same config and round-trip through `ProjectYamlWriter` unchanged.
- The shared output schemas in Core and what Claude Code sees do not change.
- `HostLauncher` refuses `--dangerously-bypass-approvals-and-sandbox` and `--sandbox danger-full-access` (also `-s danger-full-access`), as it refuses Claude's bypass.
- Known accepted test failure: Core `ProjectYamlWriterTests.Update_rewrites_only_changed_sections` (CRLF checkout).
- Commit messages end with `Claude-Session: https://claude.ai/code/session_01Fa3TSErRpsDCCnGNNYVXhG`.

## Review Focus

1. A Codex run that times out or is cancelled after Codex refreshed the ChatGPT login: the refreshed `auth.json` is still written back and the per-run directory is still deleted (else the developer is silently logged out). Test in Task 5.
2. A truncated, empty or non-object `auth.json` left by the container never overwrites the developer's good file. Test in Task 5.
3. An existing Claude-only project file loads to an equal config and the writer emits byte-identical sections. Tests in Tasks 1 and 8.
4. Codex fills optional schema fields with `null` (strict mode); readers must treat them as absent (a `null` `spec_path` must not become the string "null" or break the plan stage). Test in Task 2.
5. A Claude run's container gets no `CODEX_API_KEY` and no run-files mount; a Codex run gets no `CLAUDE_CODE_OAUTH_TOKEN`. Test in Task 3.

---

### Task 1: Provider types, per-stage overrides and Codex settings in config

**Files:**
- Create: `src/Symphonist.Core/Config/AgentTypes.cs`
- Modify: `src/Symphonist.Core/Config/ProjectConfig.cs`, `src/Symphonist.Core/Config/ProjectConfigLoader.cs`
- Test: `tests/Symphonist.Core.Tests/Config/ProjectConfigLoaderTests.cs`

**Interfaces:**
- Produces:
  - `public static class AgentTypes { public const string ClaudeCode = "claude-code"; public const string Codex = "codex"; public static readonly IReadOnlyList<string> All; }`
  - `public enum CodexAuth { ApiKey, ChatGpt }` (YAML `api-key` | `chatgpt`, parsed the way `EvidenceMode` parses `high-risk`).
  - `public sealed record CodexSettings(CodexAuth Auth, string ApiKeySecret)` with `const string DefaultApiKeySecret = "openai-api-key"` and `static readonly CodexSettings Default`.
  - `AgentSettings.Codex { get; init; } = CodexSettings.Default` (copied into every derived settings: stage, review, evidence).
  - `StageDefinition.Agent { get; init; }` (`AgentSettings?`, null = project default).
  - `ProjectConfig.AgentFor(string stageId) -> AgentSettings` (the stage override, else `Agent`; unknown id → `Agent`).
  - `ProjectConfig.AgentTypesInUse -> IReadOnlySet<string>`: `Agent.Type`, every stage override type, `Review.Agent.Type`, `Evidence.Agent.Type` (regardless of modes).

- [ ] **Step 1: Write failing loader tests**

```csharp
[Fact] public void Existing_file_has_no_overrides_and_default_codex_settings()
{   // orders.yaml unchanged
    var p = Load("orders.yaml");
    Assert.All(p.Pipeline, s => Assert.Null(s.Agent));
    Assert.Equal(CodexSettings.Default, p.Agent.Codex);
    Assert.Equal(new HashSet<string> { "claude-code" }, p.AgentTypesInUse);
}
[Fact] public void Stage_override_sets_type_model_timeout_and_inherits_the_rest()
// pipeline item `agent: { type: codex, model: gpt-5-codex, timeoutMinutes: 15 }` →
// AgentFor("implement"): Type "codex", Model "gpt-5-codex", TimeoutMinutes 15, Isolation/AllowedDomains/Image/Packages equal p.Agent's.
[Fact] public void Override_changing_type_without_model_clears_the_model()
// agent.model "opus", stage `agent: { type: codex }` → AgentFor(stage).Model is null.
// Same rule: review.agent { type: codex } → Review.Agent.Model null; evidence.agent { type: codex } → Evidence.Agent.Model null.
[Fact] public void Override_keeping_type_without_model_inherits_the_model()
// agent.model "opus", stage `agent: { timeoutMinutes: 5 }` → Model "opus".
[Fact] public void Codex_api_key_settings_parse()
// agent.codex { auth: api-key, apiKeySecret: openai-orders } with a codex stage → Codex == new(CodexAuth.ApiKey, "openai-orders") on Agent and on AgentFor(stage).
[Fact] public void Codex_chatgpt_settings_parse()  // auth: chatgpt → CodexAuth.ChatGpt, ApiKeySecret default
[Theory]
[InlineData("agent.type: gpt", "unsupported agent.type 'gpt' (allowed: claude-code, codex)")]
[InlineData("stage agent type: gpt", "unsupported pipeline stage 'implement' agent.type 'gpt' (allowed: claude-code, codex)")]
[InlineData("codex block without codex job", "agent.codex is set but no job uses codex")]
[InlineData("apiKeySecret with chatgpt", "agent.codex.apiKeySecret applies only to auth: api-key")]
[InlineData("codex stage with isolation: host", "codex runs only in containers (isolation: host)")]
[InlineData("review.agent codex with isolation: host", "codex runs only in containers (isolation: host)")]
[InlineData("stage agent with isolation key", "<YamlDotNet unknown-property error naming 'isolation'>")]
public void Invalid_provider_config_is_rejected(string yamlCase, string message)  // assert ProjectConfigException message contains `message`
[Fact] public void Evidence_agent_accepts_type()  // evidence.agent { type: codex } → Evidence.Agent.Type "codex"
```

- [ ] **Step 2: Run them; expect failures** — `dotnet test tests/Symphonist.Core.Tests --filter ProjectConfigLoaderTests`

- [ ] **Step 3: Implement**
  - `StageDto.Agent` is a new `StageAgentDto { Type, Model, TimeoutMinutes }`; `EvidenceAgentDto` gains `Type`; `AgentDto` gains `Codex` (`CodexDto { Auth, ApiKeySecret }`). Unknown keys keep failing as today.
  - Replace both `Supported(…, "claude-code")` calls with one validator against `AgentTypes.All` producing the messages above.
  - One private helper derives an override's `AgentSettings` from the project settings: `type ?? base.Type`, `model ?? (type changed ? null : base.Model)`, `timeoutMinutes ?? <slot default>`, everything else from base (including `Packages` and `Codex`). Review and evidence use it too (their timeout defaults stay 20 and `EvidenceLimits.DefaultTimeoutMinutes`).
  - After building the config: the host-isolation check and the stale-`agent.codex` check use `AgentTypesInUse`. Keep the "codex requires container" rule in one private method (spec: lifting it later must be a small change).

- [ ] **Step 4: Run Core tests** — all pass except the known CRLF test.

- [ ] **Step 5: Commit** — `feat(config): per-stage agent overrides and codex provider settings`

---

### Task 2: Strict-schema adapter and shared structured-result helper

**Files:**
- Create: `src/Symphonist.Integrations/Codex/CodexSchema.cs`, `src/Symphonist.Core/Agents/AgentResults.cs`
- Modify: `src/Symphonist.Integrations/ClaudeCode/ClaudeCodeRunner.cs` (use `AgentResults`)
- Test: `tests/Symphonist.Integrations.Tests/Codex/CodexSchemaTests.cs`, `tests/Symphonist.Core.Tests/Agents/AgentResultsTests.cs`

**Interfaces:**
- Produces:
  - `CodexSchema.Strict(string schemaJson) -> string`: recursively, every object schema gets `"additionalProperties": false` and `required` = all its property names; a property not originally required becomes nullable (`"type": [t, "null"]`, and `null` appended to its `enum` if it has one). Array `items` are processed too.
  - `CodexSchema.DropNulls(JsonElement value) -> JsonElement`: a copy with every null-valued object property removed, recursively (array elements kept).
  - `AgentResults.FromStructured(AgentRunRequest request, JsonElement? structured, decimal? costUsd, TimeSpan duration, string rawLog) -> AgentRunResult` — exactly today's tail of `ClaudeCodeRunner.Parse` after the exit-code/is_error check: stage schema → `StageOutputSchema.Read`, missing → failed "missing structured output", status ≠ completed → failed with the summary; other schemas → success with `Structured` or failed "missing structured output".

- [ ] **Step 1: Write failing tests**

```csharp
[Theory, MemberData(nameof(AllSchemas))] // StageOutputSchema.Json, PrFixOutputSchema.Json, GateReviewSchema.Json, EvidenceSchema.Json, ContainerCheckSchema.Json, OnboardingProposalSchema.Json
public void Strict_schema_requires_every_property_and_forbids_extras(string schema)
// walk the result: every "type":"object" node has additionalProperties false and required ⊇ properties.Keys
[Fact] public void Optional_property_becomes_nullable_required_property()
// StageOutputSchema: spec_path → type ["string","null"], in required; status keeps "string" and enum unchanged
[Fact] public void Optional_enum_property_gets_null_in_enum()
[Fact] public void DropNulls_removes_null_properties_recursively()
// {"status":"completed","summary":"s","spec_path":null,"replies":[{"comment_id":"1","reply":"r","x":null}]} → no spec_path, no x
[Fact] public void Null_optional_fields_read_as_absent_stage_output()
// StageOutputSchema.Read(DropNulls(json with spec_path null)) → SpecPath null, Status "completed"
[Fact] public void FromStructured_stage_completed_succeeds() / _failed_status_fails_with_summary() / _missing_fails() / _non_stage_returns_structured()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement**, then switch `ClaudeCodeRunner.Parse` to `AgentResults.FromStructured` for its tail (keep its exit-code/`is_error` failure path as is).
- [ ] **Step 4: Run Core and Integrations tests; `ClaudeCodeRunnerTests` stay green unchanged.**
- [ ] **Step 5: Commit** — `feat(agents): codex strict-schema adapter and shared structured result`

---

### Task 3: Provider-neutral launchers and firewall

**Files:**
- Create: `src/Symphonist.Integrations/Containers/AgentProviderInfo.cs`
- Modify: `src/Symphonist.Integrations/Processes/ProcessRunner.cs`, `src/Symphonist.Integrations/Containers/DockerLauncher.cs`, `src/Symphonist.Integrations/ClaudeCode/IAgentLauncher.cs` (`HostLauncher`), `src/Symphonist.Integrations/Containers/agent/init-firewall.sh`
- Test: `tests/Symphonist.Integrations.Tests/Containers/DockerLauncherTests.cs`, `tests/Symphonist.Integrations.Tests/ClaudeCode/IsolationLauncherTests.cs` (or wherever `HostLauncher` is tested), `tests/Symphonist.Integrations.Tests/Containers/AgentImageTests.cs`

**Interfaces:**
- Consumes: `AgentTypes`, `AgentSettings.Codex`, `CodexAuth` (Task 1).
- Produces:
  - `public sealed record BindMount(string HostPath, string ContainerPath, bool ReadOnly = false);` and `ProcessSpec(..., IReadOnlyList<BindMount>? Mounts = null)` (last optional parameter).
  - `AgentProviderInfo.BaseDomains(string agentType) -> IReadOnlyList<string>`: claude-code → `api.anthropic.com, statsig.anthropic.com, sentry.io`; codex → `api.openai.com, auth.openai.com, chatgpt.com`. Unknown type throws.
  - `DockerLauncher.CodexApiKeyVar = "CODEX_API_KEY"`, `DockerLauncher.BaseDomainsVar = "SYMPHONIST_BASE_DOMAINS"`.
  - `DockerLauncher.HasCommandAsync(string image, string command, CancellationToken ct) -> Task<bool>`: `docker run --rm --pull=never --entrypoint <command> <image> --version`, true on exit 0.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Claude_run_gets_claude_token_and_anthropic_domains_only()
// Wrap(claude cmd, request type claude-code): args contain "-e","CLAUDE_CODE_OAUTH_TOKEN", not "CODEX_API_KEY"; env[SYMPHONIST_BASE_DOMAINS] == "api.anthropic.com,statsig.anthropic.com,sentry.io"
[Fact] public void Codex_api_key_run_gets_its_project_secret_and_openai_domains_only()
// settings.Codex = new(ApiKey, "openai-orders"); getSecret("openai-orders") = "sk-x" → env CODEX_API_KEY "sk-x"; no CLAUDE_CODE_OAUTH_TOKEN in args; base domains are the codex list
[Fact] public void Codex_chatgpt_run_gets_no_api_key()
[Fact] public void Codex_api_key_run_without_secret_throws_naming_the_secret()
// InvalidOperationException message contains "openai-orders" and "not set"
[Fact] public void Command_environment_is_forwarded_by_name()
// agentCommand.Environment {"CODEX_HOME":"/symphonist-run/home"} → args "-e","CODEX_HOME"; wrapped env has the value
[Fact] public void Mounts_become_volume_arguments()
// BindMount("C:/tmp/run","/symphonist-run") → "-v","C:/tmp/run:/symphonist-run"; ReadOnly → ":ro" suffix
[Fact] public void Allowed_domains_no_longer_include_base_domains()
// SYMPHONIST_ALLOWED_DOMAINS carries only settings.AllowedDomains
[Fact] public void Host_launcher_refuses_codex_bypass_and_full_access()
// args "--dangerously-bypass-approvals-and-sandbox" → throws; "--sandbox","danger-full-access" → throws; "-s","danger-full-access" → throws; "--sandbox=danger-full-access" → throws
[Fact] public void Host_launcher_refuses_mounts()
[Fact] public void Firewall_script_takes_base_domains_from_env()
// AgentImageTests-style text assertion: init-firewall.sh has no hard-coded "api.anthropic.com" and reads SYMPHONIST_BASE_DOMAINS; empty → script fails (exit non-zero path present)
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** `DockerLauncher.Wrap` picks credentials by `request.Settings.Type` (and `Codex.Auth`); base domains from `AgentProviderInfo`; adds `-e NAME` for each `agentCommand.Environment` key; adds mounts. Remove `BaseAllowedDomains`. `init-firewall.sh`: `BASE_DOMAINS` read from `SYMPHONIST_BASE_DOMAINS` (comma list, required, each still added with `required=1`); refuse to continue when empty.
- [ ] **Step 4: Run Integrations tests.**
- [ ] **Step 5: Commit** — `feat(containers): provider-neutral docker launcher and firewall base domains`

---

### Task 4: Agent image with both CLIs

**Files:**
- Modify: `src/Symphonist.Integrations/Containers/agent/Dockerfile`, `src/Symphonist.Integrations/Containers/DockerLauncher.cs` (`DefaultImage`)
- Test: `tests/Symphonist.Integrations.Tests/Containers/AgentImageTests.cs`, any test asserting the old tag

**Interfaces:**
- Produces: `DockerLauncher.DefaultImage = "symphonist/agent:2"`.

- [ ] **Step 1: Failing tests** — `Dockerfile_installs_both_clis` (text contains `npm install -g @anthropic-ai/claude-code @openai/codex` or two installs, and `codex --version`); `Default_image_tag_is_agent_2`.
- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** Update the Dockerfile header comment (`symphonist/agent:2`, "Claude Code and Codex"). Grep the repo for `agent-claude:1` and update docs/tests that mean the default.
- [ ] **Step 4: Run Integrations tests. If Docker is available, `docker build` the context once and run `docker run --rm --entrypoint codex symphonist/agent:2 --version`; record the result in the report (skip with a note if Docker is unavailable).**
- [ ] **Step 5: Commit** — `feat(containers): agent image ships Claude Code and Codex`

---

### Task 5: CodexRunner

**Files:**
- Create: `src/Symphonist.Integrations/Codex/CodexRunner.cs`
- Test: `tests/Symphonist.Integrations.Tests/Codex/CodexRunnerTests.cs`

**Interfaces:**
- Consumes: `CodexSchema`, `AgentResults.FromStructured` (Task 2); `BindMount`, `ProcessSpec.Mounts` (Task 3); `AgentSettings.Codex` (Task 1).
- Produces: `public sealed class CodexRunner(IProcessRunner runner, IAgentLauncher launcher, string codexHome, string runFilesRoot) : IAgentRunner` with `public const string RunFilesMount = "/symphonist-run";`. `codexHome` is the developer's Codex home (Program passes `CODEX_HOME` or `~/.codex`); `runFilesRoot` is where per-run directories are created.

Behaviour (the decisions the tests pin):
- Not `launcher.ProvidesIsolation(request)` → failed result "codex runs only in containers", nothing launched.
- Per run: create `runFilesRoot/<guid>/` with `schema.json` (= `CodexSchema.Strict(request.OutputSchema)`) and `home/`. Mount it at `/symphonist-run`; env `CODEX_HOME=/symphonist-run/home`.
- ChatGPT auth: `codexHome/auth.json` missing or unreadable → failed "Codex is not signed in: run `codex login` on this machine", nothing launched; else copied to `home/auth.json` and its bytes remembered.
- Arguments, in order: `exec`, `--dangerously-bypass-approvals-and-sandbox`, `--skip-git-repo-check`, `--ephemeral`, `--color`, `never`, `--output-schema`, `/symphonist-run/schema.json`, `-o`, `/symphonist-run/last-message.json`, then for `ReadOnlyWorkspace`: `-c`, `project_doc_max_bytes=0`, `-c`, `web_search="disabled"`; then `-m <model>` when set; last `-`. Prompt on stdin.
- After the process (in `finally`, also on timeout/cancel): ChatGPT write-back when `home/auth.json` differs from the remembered bytes and parses as a JSON object — under one static `SemaphoreSlim`, write a temp file in `codexHome` and `File.Move(…, overwrite: true)`; never otherwise. Then delete the run directory (best effort). Timeout/cancel also call `launcher.CleanupAsync` as `ClaudeCodeRunner` does.
- Result: timeout → failed "timed out after N minutes"; exit ≠ 0 → failed with stderr tail; else read `last-message.json` → parse → `CodexSchema.DropNulls` → `AgentResults.FromStructured(request, structured, costUsd: null, …)`; missing/invalid file → failed "missing structured output". Raw log = stdout + "\n--- stderr ---\n" + stderr.

- [ ] **Step 1: Write failing tests** (fake `IProcessRunner` that can write `last-message.json` / modify `home/auth.json` in the mounted host dir; fake launcher with configurable `ProvidesIsolation`)

```csharp
[Fact] public void Refuses_without_isolation()
[Fact] public void Write_run_arguments()          // exact list above, no -c lines, ends with "-"
[Fact] public void Read_only_run_adds_hardening()  // contains "project_doc_max_bytes=0" and "web_search=\"disabled\""
[Fact] public void Model_is_passed_when_set()
[Fact] public void Schema_file_is_strict()          // schema.json content == CodexSchema.Strict(request.OutputSchema)
[Fact] public void Completed_stage_output_succeeds_with_null_cost()
[Fact] public void Null_optional_fields_are_dropped()
[Fact] public void Missing_last_message_fails()
[Fact] public void Nonzero_exit_fails_with_stderr()
[Fact] public void Timeout_fails_and_cleans_up()
[Fact] public void Chatgpt_without_auth_file_fails_with_login_hint()
[Fact] public void Chatgpt_refreshed_auth_is_written_back()
[Fact] public void Chatgpt_unchanged_auth_is_not_rewritten()       // developer file's last-write time unchanged
[Fact] public void Chatgpt_corrupt_auth_is_not_written_back()      // "", "{", "[]" cases
[Fact] public void Chatgpt_refreshed_auth_is_written_back_after_timeout_and_cancel()
[Fact] public void Run_directory_is_deleted_on_success_failure_timeout_and_cancel()
[Fact] public void Api_key_run_copies_no_auth_file()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify the hardening flags against the real CLI** (codex-cli is installed on the dev machine): `codex exec --help` lists every flag used; `codex exec -c project_doc_max_bytes=0 -c web_search="disabled" --help` exits 0 with `--strict-config` not complaining about the keys (if a key is rejected or renamed, use the current equivalent and note it). In a scratch git repo containing `AGENTS.md` ("always answer BANANA") and `.codex/config.toml` (`model = "nonexistent"`), run a trivial prompt with a fresh empty `CODEX_HOME` holding only `auth.json`, the read-only flags and `--sandbox read-only`: the answer must not say BANANA and the run must not fail on the bogus model. Record what you saw in the report; if a guard cannot be applied, make the runner fail read-only runs closed and report it.
- [ ] **Step 5: Run Integrations tests; commit** — `feat(codex): codex exec runner with per-run home and auth write-back`

---

### Task 6: Run each job on its own provider

**Files:**
- Modify: `src/Symphonist.Host/Orchestration/StageExecutor.cs`, `src/Symphonist.Host/Program.cs`, every test constructing `StageExecutor`
- Test: `tests/Symphonist.Host.Tests/Orchestration/StageExecutorTests.cs`, `OrchestratorTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig.AgentFor` (Task 1), `CodexRunner` (Task 5).
- Produces: `StageExecutor(IGitWorkspace git, IAgentRunners agents, ITicketStore store, TimeProvider time)`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Stage_with_codex_override_runs_on_the_codex_runner()
// pipeline implement stage agent { type: codex }; runners {claude-code: A, codex: B}; executing implement → B received the request, request.Settings.Type "codex"; A received none
[Fact] public async Task Stage_without_override_runs_on_project_agent()
[Fact] public async Task Pr_comment_fix_run_uses_the_gated_stage_agent()
// ticket at implement gate with PR feedback; implement overridden to codex → B ran the PR-fix prompt
[Fact] public async Task Reviewer_and_evidence_use_their_own_providers()  // OrchestratorTests: review.agent codex, project claude → reviewer request went to B
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** `RunAgentAsync` uses `agents.For(settings.Type)` with `settings = p.AgentFor(t.StageId)`. `Program.cs` registers `CodexRunner` under `AgentTypes.Codex` (codex home: `CODEX_HOME` env or `~/.codex`; run files root: `SymphonistPaths.Resolve("run")`), and `AgentTypes.ClaudeCode` for Claude. Update test call sites by wrapping the fake runner in `new AgentRunners(new Dictionary<string, IAgentRunner> { [AgentTypes.ClaudeCode] = agent })`.
- [ ] **Step 4: Run Host tests.**
- [ ] **Step 5: Commit** — `feat(orchestrator): resolve the agent runner per stage`

---

### Task 7: Setup checks cover every provider in use

**Files:**
- Modify: `src/Symphonist.Host/Onboarding/SetupChecks.cs`, `src/Symphonist.Host/Onboarding/OnboardingService.cs`, `src/Symphonist.Host/Onboarding/ContainerCheck.cs`
- Test: `tests/Symphonist.Host.Tests/Onboarding/SetupChecksTests.cs`, `OnboardingServiceTests.cs`, `ContainerCheckTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig.AgentTypesInUse`, `AgentSettings.Codex` (Task 1); `DockerLauncher.HasCommandAsync` (Task 3); the codex home path (same source as Task 6; pass it into `SetupChecks` via constructor).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Missing_secrets_include_claude_token_only_when_claude_is_used()
[Fact] public void Missing_secrets_include_the_codex_api_key_secret_when_codex_uses_api_key()   // name from agent.codex.apiKeySecret
[Fact] public void Secret_names_in_use_follow_the_providers()                                    // OnboardingService.SecretNamesInUse: form with codex api-key → contains "openai-api-key", no "claude-code-token" when no claude job
[Fact] public async Task Chatgpt_sign_in_is_checked()    // SetupChecks step "Codex sign-in": passed when codexHome/auth.json exists, failed "run `codex login`" otherwise
[Fact] public async Task Container_check_verifies_each_cli_in_the_image()
// AgentTypesInUse {claude-code, codex}: "Agent image" step runs HasCommandAsync(image,"claude") and (image,"codex"); codex missing → step failed "image has no codex CLI; rebuild on symphonist/agent:2"
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** The container check's build-and-test run keeps using the project default agent.
- [ ] **Step 4: Run Host tests.**
- [ ] **Step 5: Commit** — `feat(onboarding): setup checks for every agent provider in use`

---

### Task 8: Form and YAML writer

**Files:**
- Modify: `src/Symphonist.Core/Onboarding/ProjectForm.cs`, `src/Symphonist.Core/Onboarding/ProjectYamlWriter.cs`
- Test: `tests/Symphonist.Core.Tests/Onboarding/ProjectFormTests.cs`, `ProjectYamlWriterTests.cs`

**Interfaces:**
- Consumes: Task 1 config types.
- Produces: `AgentForm.Type` (default `AgentTypes.ClaudeCode`), `AgentForm.CodexAuth` (`CodexAuth`, default `ApiKey`), `AgentForm.CodexApiKeySecret` (default `CodexSettings.DefaultApiKeySecret`); `StageForm.AgentType`, `StageForm.AgentModel`, `StageForm.AgentTimeoutMinutes` (all nullable = project default); `ReviewForm.AgentType`, `EvidenceForm.AgentType` (nullable = project agent). `From`, clone and `Validate` cover them; `Validate` repeats the loader's provider rules (codex with host isolation, apiKeySecret only for api-key) with the same messages.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Claude_only_form_writes_the_same_sections_as_before()  // Sections(From(orders.yaml config)) equal today's text for agent/pipeline/review/evidence
[Fact] public void Codex_agent_round_trips()       // type codex + codex: { auth: chatgpt } → Load(NewFile(form)) has the same settings
[Fact] public void Default_codex_settings_are_omitted()   // auth api-key + default secret → no "codex:" block
[Fact] public void Stage_override_round_trips()     // flow item gains "agent: { type: codex, model: m }"
[Fact] public void Review_and_evidence_agent_type_round_trip()
[Fact] public void Validate_rejects_codex_with_host_isolation()
```

- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.** Writer: `agent.type` from the form; `codex:` block only when not default; stage override as a nested flow mapping in the stage's flow item; review/evidence `type` only when set.
- [ ] **Step 4: Run Core tests.**
- [ ] **Step 5: Commit** — `feat(onboarding): provider fields in the project form and writer`

---

### Task 9: Setup screen

**Files:**
- Modify: `src/Symphonist.Host/Components/Pages/ProjectSetup.razor`
- Test: the existing bUnit tests for the setup screen (find with `grep -rl ProjectSetup tests/Symphonist.Host.Tests`)

**Interfaces:**
- Consumes: Task 8 form fields.

UI decisions: project agent gets a "Provider" select (Claude Code / Codex) before Model; when Codex: "Codex sign-in" select (API key / ChatGPT login) and, for API key, "OpenAI key secret" text field (hint "Name of the secret in the OS credential store"). The Codex option is disabled while isolation is host, with hint "Codex runs only in containers". Each stage row gets "Provider" (Project default / Claude Code / Codex) and "Model" fields. Reviewer and evidence sections get a "Provider" select (Project agent / Claude Code / Codex). Field hints that say "Claude Code's own" become provider-neutral ("the agent's own").

- [ ] **Step 1: Failing bUnit tests** — choosing Codex on a stage and saving writes the override; Codex disabled under host isolation; sign-in fields appear only for Codex.
- [ ] **Step 2: Run; expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run Host tests.**
- [ ] **Step 5: Commit** — `feat(setup): choose agent providers per project, stage, reviewer and evidence`

---

### Task 10: Docs and prompt wording

**Files:**
- Modify: `README.md`, `samples/projects/orders.example.yaml`, embedded prompts under `src/Symphonist.Core/Prompts/` (only Claude-specific wording)
- Test: `tests/Symphonist.Core.Tests/Config/SampleConfigTests.cs` (the sample still loads)

- [ ] **Step 1:** README "Agent providers" section: Claude Code and Codex for any job; per-stage/reviewer/evidence overrides with a YAML example; Codex sign-in (`api-key`: store the key with the secrets command under `apiKeySecret`; `chatgpt`: run `codex login` once, Symphonist round-trips `auth.json`); Codex is container-only for now; no dollar cost for Codex runs; custom `agent.image` must contain the CLIs the project uses — rebuild on `symphonist/agent:2`.
- [ ] **Step 2:** Sample YAML: commented `codex:` block and a commented stage `agent:` override.
- [ ] **Step 3:** Grep prompts for "Claude" and Claude-only tool names; make them provider-neutral where they address the agent.
- [ ] **Step 4:** Run Core tests (sample loads).
- [ ] **Step 5: Commit** — `docs: agent providers`

---

### Task 11: Real-CLI smoke run (controller, after the final review)

Not a subagent task. With Docker running and the new image built: one project configured with a Codex implement stage and a Claude reviewer, run against a scratch repo/ticket once with `auth: api-key` and once with `auth: chatgpt`; confirm the stage completes, the reviewer runs on Claude, and after a ChatGPT run the developer's `auth.json` is intact. If credentials or Docker are unavailable, report that this step was not run.
