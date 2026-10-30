# Swappable agent providers (Codex + Claude Code) — design

Plan 6 of the roadmap ("Codex agent + second-opinion reviewer"), reshaped by the developer.

## Intent

The developer wants to swap the AI provider behind any job Symphonist hands an agent, not only add a second reviewer.
Example: Codex does the main work and Claude Code reviews, or the reverse, or Claude plans and Codex implements. The
"second-opinion reviewer" from the original spec (section 8, item 5) falls out of this as one configuration.

Symphonist is generic: nothing here is specific to one project or repo.

## Goals

- Two agent providers: `claude-code` (today) and `codex` (OpenAI Codex CLI). Either can run every agent job:
  pipeline stages (any kind), PR-comment fix runs, evidence runs, the risk reviewer, and onboarding's proposal run.
- **Per stage, with a project default.** The project's `agent` block is the default. Each pipeline stage may override
  the provider and model. The reviewer (`review.agent`) and evidence run (`evidence.agent`) keep their own override
  slots and may name either provider.
- **Codex sign-in, both ways, per project:** an OpenAI API key (OS credential store) or the developer's ChatGPT login
  (`~/.codex/auth.json`).
- **Codex runs only in containers.** A project with `isolation: host` cannot use Codex for any job.
- Setup screen, setup checks and onboarding handle providers; the YAML writer round-trips the new keys.

## Non-goals

- Two reviewers judging the same gate and comparing verdicts (not wanted; one reviewer, any provider).
- Codex in host mode, for now (developer's decision: container only in this plan; host mode may come later, which
  needs answers on network access under Codex's sandbox and on Windows sandbox support). Keep the "codex requires
  container" rule in one place so lifting it later is a small change.
- OpenCode or other providers (plan 8; this plan builds the shared plumbing they will reuse).
- Dollar cost for Codex runs. Codex does not report one; cost shows as unknown. Plan 7's cost guardrail must treat
  unknown cost as "not counted" (noted for plan 7, not built here).
- Per-provider prompt templates. The embedded prompts stay shared; they are already agent-neutral (builder verifies
  and fixes any Claude-only wording).

## Anti-goals

- A provider swap must never weaken a guard. The read-only reviewer must stay unable to modify the workspace, and
  every write job must stay confined to its workspace and the network allowlist, whichever provider runs it.
  (Evidence runs are writable today on purpose: they build and run the app in a throwaway clone. That stays.)
- No credentials beyond the chosen provider's enter a container (a Claude run gets no OpenAI key or ChatGPT login; a
  Codex run gets no Claude token).
- Existing project files load and behave exactly as before.

## Constraints (from the main spec and earlier plans)

- .NET 10; Windows, macOS, Linux. Processes launched with `ArgumentList`, paths with `Path.Combine`.
- Secrets live in the OS credential store, never in project files.
- Container mode is the default: only the workspace mounted, outbound network allowlisted, default deny.

## Approaches considered

1. **A dedicated runner per provider (chosen).** A `CodexRunner` next to `ClaudeCodeRunner`, each knowing its own CLI's
   flags, sandbox switches, credential handling and result format. Safety switches are explicit and tested per
   provider. Cost: each future provider needs a small runner class.
2. **One generic, config-driven runner.** Providers described as command templates. Rejected: the security-critical
   parts (read-only lockdown, credentials, result parsing) differ too much between CLIs to template safely, and a
   template typo could silently drop a guard.

The developer picked 1.

## What exists today

- `IAgentRunner` / `IAgentRunners.For(type)`; `Program.cs` registers only `"claude-code"`.
- `AgentSettings(Type, Isolation, AllowedDomains, AllowedTools, TimeoutMinutes, Model, Image)` + `Packages`.
- `ProjectConfigLoader` rejects any `agent.type` / `review.agent.type` other than `claude-code`. `review.agent` already
  overrides type, model, timeout, tools; `evidence.agent` overrides timeout and model only.
- Callers: `StageExecutor` (stages and PR-fix runs, `p.Agent`), `GateReviewer` (`p.Review.Agent`), `EvidenceRunner`
  (`p.Evidence.Agent`, writable throwaway clone), `OnboardingProposer` (`draft.Review.Agent`), `ContainerCheck` and
  `SetupChecks` (`draft.Agent` only).
- `DockerLauncher` is Claude-specific: image `symphonist/agent-claude:1`, injects `CLAUDE_CODE_OAUTH_TOKEN` from secret
  `claude-code-token`, base domains `api.anthropic.com`, `statsig.anthropic.com`, `sentry.io` (mirrored in
  `init-firewall.sh`). `HostLauncher` refuses Claude's permission-bypass flags. Claude bypasses its permission prompts
  only when the launcher reports `ProvidesIsolation`.
- Claude read-only runs: `--disallowedTools` (writes, Bash, web), `--setting-sources user`, `--strict-mcp-config`, and
  a read-only workspace mount in containers.

## Decided by the developer

- Swappable providers for every job, not just the reviewer.
- Per-stage override with a project default.
- Codex sign-in: both API key and ChatGPT login, chosen per project.
- Codex is container-only.
- Dedicated runner per provider.

## Design

### Configuration (YAML)

```yaml
agent:
  type: codex                 # claude-code | codex (the key stays required, so existing files are unchanged)
  model: ""                   # provider's own model name
  isolation: container        # codex anywhere in the project requires container
  codex:
    auth: api-key             # api-key (default) | chatgpt
    apiKeySecret: openai-api-key   # default; only with auth: api-key

pipeline:
  - id: plan
    kind: plan
    agent: { type: claude-code, model: "" }      # optional per-stage override: type, model, timeoutMinutes
  - id: implement
    kind: implement
    agent: { type: codex }

review:
  mode: shadow
  agent: { type: claude-code }                   # existing slot; now accepts codex

evidence:
  agent: { type: codex, timeoutMinutes: 30 }     # existing slot; gains `type`
```

- A stage override may set only `type`, `model`, `timeoutMinutes`. Everything else (isolation, domains, packages,
  image, tools) comes from the project agent. Builder's call: when an override changes `type` and sets no `model`, the
  model is empty (provider default), not the project's model, because model names don't cross providers. Same rule
  for `review.agent` and `evidence.agent`.
- `agent.codex` is project-wide (one Codex sign-in per project). Builder's call: it is a load error when no job in the
  project uses codex, so a stale setting is visible; `apiKeySecret` with `auth: chatgpt` is also an error.
- Builder's call: the per-project secret name (`apiKeySecret`, default `openai-api-key`) follows the existing
  per-project `tokenSecret` pattern for trackers and repos, so different projects can bill different OpenAI keys.
- Load errors: unknown `type` (naming the field and allowed values); codex used by any job while
  `isolation: host` ("codex runs only in containers").
- Core config gains a way to resolve the effective `AgentSettings` for a stage (`ProjectConfig.AgentFor(stage)` or a
  resolved field on `StageDefinition`; builder's call). PR-fix runs use the agent of the stage whose gate they serve.
  Onboarding's proposal run keeps using the review agent.

### Credentials

- `claude-code`: unchanged (`claude-code-token` secret → `CLAUDE_CODE_OAUTH_TOKEN`).
- `codex`, `api-key`: the project's `apiKeySecret` from the OS credential store, set with the existing secrets
  CLI/setup flow, passed as the env var Codex reads in `exec` mode (builder verifies the current name:
  `CODEX_API_KEY` / `OPENAI_API_KEY`).
- `codex`, `chatgpt`: the developer runs `codex login` on their machine once. For each run Symphonist copies their
  `auth.json` (from `CODEX_HOME`, default `~/.codex`) into a fresh per-run temp directory mounted into the container as
  Codex's home. After the run, if the copy changed and still parses, it is written back atomically to the developer's
  `auth.json`. Builder's call: a process-wide lock around the write-back is enough; a run whose token was rotated by
  another run fails with a message telling the developer to retry or run `codex login`. The per-run copy is deleted
  afterwards, on success, failure, timeout or cancel.
- Each run gets only its own provider's credential. A missing credential fails the run with one clear message naming
  the secret or the `codex login` step.

### Codex runner

`CodexRunner : IAgentRunner`, registered as `"codex"`. Non-interactive `codex exec` with the prompt on stdin, always in
a container:

- Refuses (fails closed, with a clear error) any request whose launcher does not report `ProvidesIsolation`. The loader
  already blocks host projects; this is defense in depth.
- Sandbox: Codex's own sandbox generally cannot run inside Docker, so, exactly as Claude bypasses its permission prompts
  only under `ProvidesIsolation`, Codex runs with its sandbox and approvals off
  (`--dangerously-bypass-approvals-and-sandbox` or the current equivalent) and the container is the guard: firewall
  allowlist, read-only `.git`, and for read-only jobs a read-only workspace mount. `HostLauncher` refuses that flag
  (and `--sandbox danger-full-access`) the same way it refuses Claude's.
- Output schema: `request.OutputSchema` written to a temp file and passed with `--output-schema`. Codex applies OpenAI's
  strict structured-output rules (all properties required, `additionalProperties: false`), so the runner rewrites the
  schema privately (optional properties become required-but-nullable) and drops nulls before handing the result to the
  existing readers (`StageOutputSchema.Read`, `PrFixOutputSchema`, reviewer and evidence readers). The shared schemas
  and what Claude sees do not change. Builder verifies the current CLI's rules.
- The final message is read via `--output-last-message <file>` or the `--json` event stream (builder's call).
- Model: `-m <model>` when set.
- Read-only hardening, mirroring Claude's: the run must not load project-level Codex config from the reviewed
  workspace that could widen it (repo `.codex/` config, project MCP servers), and must have no web search tool.
  Builder finds Codex's equivalents (`-c` overrides, profile / ignore-project-config flags) and fails closed if a
  guard cannot be applied. `AGENTS.md` from the workspace: not loaded for read-only runs if Codex allows turning that
  off; otherwise the review prompt's untrusted-content fence is the guard and this is recorded as a known difference.
- Result: success/failed, duration, raw log (stdout + stderr), `CostUsd = null`. Token usage, if reported, goes into
  the raw log only.
- Timeout and cancellation: same as Claude (process timeout; launcher cleanup).

### Containers

- One image with both CLIs (builder's call: simpler than an image per provider). New default tag
  (`symphonist/agent:2` or similar). The packages image builds on whatever base image the project uses, unchanged.
- Builder's call on custom `agent.image`: allowed for either provider; setup checks verify the chosen CLIs exist in the
  image (`claude --version` / `codex --version`), so a Claude-only custom image used with Codex fails at setup, not
  mid-run. Images built `FROM symphonist/agent-claude:1` keep working for Claude-only projects because that tag stays
  on the developer's machine; the README notes rebuilding on the new tag to use Codex.
- `DockerLauncher` stops being Claude-specific: per-provider credential env vars, mounts and base allowed domains come
  from a small per-provider description. Codex base domains: `api.openai.com`, plus what ChatGPT login and refresh
  need (`auth.openai.com`, `chatgpt.com`); builder verifies the exact set against the current CLI. `init-firewall.sh`
  takes the full list from the env var instead of mirroring a hard-coded one.

### Setup screen, checks and onboarding

- Project agent: provider dropdown (Claude Code / Codex), model, and for Codex the sign-in choice and API-key secret
  name. Codex is unavailable (with a hint) while isolation is host.
- Each stage row: optional provider and model override ("project default" by default).
- Reviewer and evidence: provider dropdown alongside their existing fields.
- Checks run for every provider any job in the project uses, not only the default: the CLI exists in the image, and
  the provider's credential exists (Claude token secret, OpenAI key secret, or a readable `auth.json`). Builder's call
  on shaping `ContainerCheck` / `SetupChecks` for that.
- `ProjectYamlWriter` writes the new keys only when they differ from defaults, so existing files round-trip unchanged.
- Onboarding's proposal keeps the default `claude-code`; the developer changes it on the setup screen.

### Docs

README: providers section (picking per stage, Codex sign-in both ways, container-only, no Codex cost, rebuilding
custom images). `samples/projects/orders.example.yaml`: commented examples of the new keys.

## Testing expectations

- Loader: defaults unchanged; per-stage overrides; type change clears model; bad types, stale `agent.codex`, and codex
  with host isolation are errors.
- `CodexRunner`: argument building per job kind (write vs read-only, model, schema file), schema rewriting, result
  parsing (completed, failed, malformed, timeout), refusal without isolation.
- `HostLauncher` refuses Codex bypass flags.
- `DockerLauncher`: right env, mounts and domains per provider; no cross-provider credentials; auth.json copy and
  write-back (changed / unchanged / corrupt / run failed); per-run copy always deleted.
- Orchestrator: a stage with a codex override runs through the codex runner; reviewer and evidence choose their own.
- Setup checks cover every provider used. Writer round trip; setup screen edits.
- A manual smoke run with the real Codex CLI (both sign-ins) before calling it done; the CLI's flags move fast.

## Left to the builder

Marked "builder's call" above, plus: exact class and file names; how per-provider launcher details are shaped;
verifying current Codex CLI flag, env and domain names.
