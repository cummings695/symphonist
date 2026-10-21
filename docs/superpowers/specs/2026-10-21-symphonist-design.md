# Symphonist — Design

Date: 2026-10-21
Status: Draft for review

## 1. Intent

Symphonist is a local .NET application that runs on a developer's machine and orchestrates agentic development across the tools a consultant already uses: work trackers (Jira, Azure DevOps Boards, Target Process, a spreadsheet), source control (GitHub, Bitbucket, Azure Repos), coding agents (Claude Code, Codex, OpenCode), and build/hosting (Bitbucket Pipelines, Azure).

It acts **as the developer**: it looks at tickets assigned to them, works under their accounts, and moves work through a pipeline from "picked up" to "in production", stopping at gates only where a human is actually needed.

### Why

The developer (a consultant working across several client projects) currently runs this by hand plus a scheduled Claude prompt:

1. Gets a ticket assigned in Jira (Backlog or Planned).
2. Judges it suitable for an agent and adds a `Claude` label.
3. A scheduled Claude session polls Jira for labeled tickets.
   - Not yet Planned: triages it (reads ticket + code), comments with a proposed approach, stops.
   - Developer reviews, moves it to Planned.
   - Planned: the session brainstorms a spec and plan, attaches them to the ticket, then waits for the ticket to move to In Progress.
4. Developer reviews spec/plan. If it's wrong, they open a Claude session by hand and iterate.
5. Approved: the session implements with multiple agents, commits to a branch, opens a PR.
6. Developer reviews the PR, it gets built, then the developer manually creates PRs to production.

The current prompt (one specific project, hard-coded to Jira and a single long-running session):

> poll the "Planned" Status tickets in Jira with a "Claude" label and pull the top one and use your brainstorming super powers to review and work on a spec and plan. attach this to the ticket and wait for it to be moved into the "In Progress" before starting implementation using multiple agents. When you have completed the implementation and checked your work commit your changes to the branch and create a PR for me to review

Problems with this: every stage needs the developer's hands, an agent session sits idle waiting on Jira state, it is tied to one tracker and one project, there is no notion of risk, and "to production" isn't tracked anywhere.

### What success looks like

Less of the developer per ticket. Low-risk work flows through with few or no touches; high-risk work (e.g. anything touching pricing in an order-management system) stops where the developer wants to look.

### Anti-goal (what makes this a failure even if it works)

**Babysitting.** If the developer has to keep adjusting the automatic process — fixing prompts, re-mapping states, nudging stuck runs, correcting the agent's risk calls — they will turn it off. Every design choice should push toward "configure once, then it runs".

## 2. Goals

- Run locally on the developer's machine as a single install; act as that developer.
- Own a tracker-independent **ticket** model and **pipeline state** model; map each source system onto it.
- Configurable pipeline per project: stages, order, and which stages are gates.
- Risk-aware gates: an agent reviewer auto-approves low-risk stage output; high-risk output waits for the developer.
- Work with several trackers, source-control hosts, and agents through plug-in interfaces.
- Local dashboard (pipeline board), CLI, and later a tray icon.
- Reusable across client projects — everything project-specific lives in configuration.

## 3. Non-goals

- Multi-user / team server. One developer, one machine. (Other consultants install their own copy and act as themselves.)
- Hosting the developer's interactive back-and-forth with an agent. That stays a normal agent session the developer opens; Symphonist's job is to make it rarely necessary (see "Send back with comment").
- Replacing the source tracker. The tracker remains the source of truth for ticket content.
- Requiring Aspire to *run* Symphonist. (Docker is required for the default container mode; see section 6.1.)

## 4. Decisions made with the developer

These were decided in conversation and should be treated as fixed:

| Topic | Decision |
|---|---|
| Where it runs | Local app on the developer's machine, not a hosted server. |
| Identity | Acts as the developer: tickets assigned to them, actions under their accounts. |
| States | Symphonist owns its own pipeline states; source systems are mapped onto them. Cards progress on Symphonist's board even when the source has no matching status (e.g. Jira with no "To prod"). |
| Approvals | Both work: moving the card in the source system (when a mapped status exists) **and** approving in Symphonist's dashboard/CLI. |
| Pipeline config | Per project. |
| Onboarding | When a project is added, Symphonist pulls the source's actual states/workflow, an agent proposes a mapping, and the developer reviews/edits/approves it. |
| Risk | Defined per project by which parts of the system a change touches (e.g. pricing). High-risk work is gated early and, most importantly, at PR, where the developer runs the app before approving. |
| Dashboard | Layout A — pipeline board: columns are stages, cards are tickets, cards waiting on the developer are highlighted. Essential feature. |
| CLI | Wanted. |
| Tray icon | Nice to have, later. |
| Source control | GitHub, Bitbucket, and Azure Repos all need support. |
| v1 integration set | **Jira (Cloud) + Bitbucket Cloud + Claude Code**, with builds/deploys watched via **Bitbucket Pipelines** (which deploys to Azure). |
| Aspire | Use .NET Aspire for developing Symphonist (AppHost, telemetry dashboard, ServiceDefaults). End users don't need Aspire. |
| Agent isolation | In v1. Agent runs happen in a Docker container by default. Host mode is kept as a per-project fallback for repos that won't containerize. |
| Features requested | All seven below (section 8). |
| Merging and release | Merging is part of the ticket flow. When a ticket passes a merge gate, Symphonist merges it into the project's configured target branch (e.g. main). The project defines its release cadence. The production gate has its own risk assessment, so low-risk tickets can be auto-approved to production. Projects can turn production auto-approval off, and can add extra gates such as UAT. |

## 5. Core model

### 5.1 Project

A configured client project. Holds:

- Tracker connection + query (e.g. Jira site, project key, "assigned to me", label `Claude`).
- Source-control connection + repo(s), main branch, production branch (or release mechanism).
- Build/deploy watcher (e.g. Bitbucket Pipelines).
- Agent choices per stage (implementer, reviewer).
- Pipeline definition (stages, gates).
- State mapping (Symphonist state ↔ source status, per direction).
- Risk areas.
- Stage prompt template overrides.
- Guardrails (cost/time/retry limits, concurrency).

### 5.2 Ticket

Symphonist's own record of a work item, linked to its source item. Source is the truth for content (title, description, assignee, comments, attachments). Symphonist is the truth for pipeline state, run history, gate decisions, risk verdicts, and artifacts produced (spec, plan, branch, PR, evidence).

### 5.3 Pipeline and states

Default pipeline (each project may change it):

| State | Moved on by | Gate? |
|---|---|---|
| Picked up | Symphonist (ticket matches project query) | — |
| Triage | Agent run; then gate | Yes |
| Plan | Agent run (spec + plan); then gate | Yes |
| Implementing | Agent run | — |
| PR review | PR opened; then gate (approval = OK to merge) | Yes |
| Merged / built | Symphonist merges the PR into the configured target branch, then watches the build | — |
| UAT (optional) | Project-defined; e.g. deployed to a UAT environment, then gate | Yes, if the project has it |
| To prod | Waits for the project's release cadence, then risk assessment + gate; Symphonist opens/merges the prod PR or triggers the release | Yes (auto-approve when low risk, if the project enables it) |
| Done | Symphonist after deploy succeeds | — |
| Blocked | Symphonist when a guardrail trips or a run fails repeatedly | — |

A ticket that enters the pipeline already in a source status mapped beyond Triage (e.g. already "Planned") starts at that stage.

### 5.4 State mapping and sync

- Each Symphonist state maps to zero or one source status per project.
- When a Symphonist state changes and a mapped source status exists, Symphonist updates the source. When no mapping exists, the source status is left alone; Symphonist may add a label or comment (configurable).
- When the developer changes the source status to one mapped to a gate's "approved" outcome, Symphonist treats that as approval. Same for the dashboard/CLI.
- Moving a source card backwards (e.g. back to Backlog) is treated as "send back" to the mapped stage.
- Onboarding auto-mapping: fetch the source's statuses/workflow, have an agent propose the mapping (and propose pipeline tweaks if the source workflow suggests them), show it in a setup screen for the developer to edit and save. Re-runnable when the source workflow changes.

### 5.5 Gates and risk

Every gate is decided one of two ways:

- **Auto**: a reviewer agent reads the stage output (and diff, for PR) against the project's risk areas and returns `low` or `high` with reasons. `low` → auto-approve.
- **Human**: the card waits, highlighted, until the developer approves or sends back.

Rules:

- When unsure, the reviewer must return `high`.
- Risk can rise mid-flight: a plan judged low risk whose implementation touches a risk area turns the PR gate human.
- Every auto-approval posts a short comment on the source ticket: what was checked and why it passed.
- Gate policy is per project and per stage (e.g. "To prod: always human" for a project with UAT, or "To prod: auto when low risk").
- The production gate runs its own risk assessment over the whole change as merged (not just reusing the PR verdict), plus the build/UAT results.
- If the source-control host requires other approvals before merge (branch rules, client reviewers), the merge waits for them; the card shows what it's waiting on.

### 5.6 Release cadence

Each project defines when production promotions happen: continuously (each ticket as it clears its gates) or on a schedule/window (e.g. Tuesdays 10:00, or a manually opened release window). Tickets that clear the prior gates wait in "To prod" until the next window, then go through the production gate together or one by one (builder's choice, configurable later). Exact cadence options beyond "continuous" and "scheduled window" are left to the builder.

Risk areas are defined per project as both code paths/globs (e.g. `src/Pricing/**`) and a plain-language description the reviewer reads (a ticket can affect pricing without touching those paths by name). Onboarding offers an agent scan of the repo to propose risk areas for the developer to confirm.

## 6. Stage execution

Symphonist owns polling, state, waiting, gates, retries, and guardrails. No agent session waits on tracker state.

Each stage is one **short agent run**:

- Started by Symphonist as a headless agent CLI run (e.g. `claude` non-interactive), working inside that ticket's git worktree, in container mode or host mode (6.1).
- Driven by a **stage prompt template** with placeholders filled per run, e.g. `{ticket.key}`, `{ticket.title}`, `{ticket.description}`, `{ticket.comments}`, `{project.repo}`, `{branch}`, `{risk.areas}`, `{feedback}`, `{spec.path}`, `{plan.path}`.
- Default templates ship with Symphonist (triage, plan, implement, gate review, address PR comments, prod promotion check). Projects may override any of them. The developer's current prompt is the basis for the default plan + implement templates, minus the Jira-specific parts.
- Templates may invoke agent skills when the agent supports them (e.g. Claude Code with superpowers: brainstorming, writing-plans, subagent-driven-development). Agents without skills get equivalent plain instructions.
- **Output contract**: every stage returns structured results Symphonist can act on without parsing chat — e.g. spec/plan file paths, PR URL, risk verdict + reasons, test results, cost/duration. Format is left to the builder (e.g. a JSON file written to a known path, or the agent CLI's structured output mode).

### 6.1 Run isolation

**Container mode (default).**

- Each agent run executes in a Docker container with only that ticket's worktree mounted.
- Inside the container the agent runs with permission prompts bypassed, so unattended runs never stall, while being confined to the worktree.
- The developer's credentials stay on the host. Symphonist itself performs pushes, PR operations, and tracker updates. The container receives only what the agent needs to run (its own login/API key), never Git or tracker tokens.
- Outbound network is restricted to a per-project allowlist (agent API, package feeds such as NuGet/npm). Default: deny everything else. Claude Code's reference devcontainer firewall is a known pattern to follow.
- Toolchain image: the repo's `devcontainer.json` if present, otherwise a Symphonist default image (.NET SDK, Node, git, headless browser for evidence screenshots) that the project can override.
- Evidence runs (build, tests, running the app) happen inside the container.
- Project onboarding includes a check that the image builds and the repo's build/tests pass inside it.
- Docker Desktop (or an equivalent Docker engine) is required on the developer's machine for this mode.

**Host mode (per-project fallback).** For repos that won't containerize. The agent runs directly on the host in the worktree with a per-stage allowlist of tools/commands (no blanket permission bypass). The project config must opt into host mode explicitly, and the dashboard marks such projects.

### 6.2 Work isolation

- Each ticket gets its own branch and git worktree, so tickets can run in parallel.
- Queue in tracker priority order with a configurable concurrency limit (default 1–2 running at once).

Artifacts:

- Spec and plan are committed to the ticket branch and linked/commented on the source ticket; which of those happens is a per-project setting.

## 7. Interfaces (plug-ins)

Each external system sits behind an interface so new ones can be added without touching the core:

- **Tracker**: query tickets for "me", read ticket content/comments, list available statuses/workflow, transition status, comment, label, attach/link.
- **SourceControl**: create branch, push, open PR, read PR comments/approvals, merge state, open production PR.
- **Agent**: run a stage prompt in a working directory, stream output, return structured result, report cost/duration, cancel.
- **Build/Deploy**: watch build and deploy status for a branch/PR/commit.

v1 implementations: Jira Cloud, Bitbucket Cloud, Claude Code, Bitbucket Pipelines. Codex as a second Agent implementation early (needed for the second-opinion reviewer). Later: Azure DevOps Boards, Target Process, spreadsheet (Excel/CSV), GitHub, Azure Repos, OpenCode, direct Azure (for review environments).

Tracker change detection is by polling in v1 (interval configurable). Webhooks are a later option; local machines generally can't receive them without a tunnel.

## 8. Features

All seven are in scope:

1. **Shadow mode before auto-approve.** Per project, the reviewer agent judges every gate but the developer still decides. Symphonist records agreement rate per stage. The developer turns on auto-approve per project/stage once satisfied; the dashboard shows the agreement number.
2. **Send back with a comment.** At any gate, the developer rejects with a comment (in the source tracker or dashboard/CLI). Symphonist reruns that stage with the comment as `{feedback}`. Replaces most manual iteration sessions.
3. **Evidence on high-risk PRs.** Before a human PR gate, the agent builds, runs tests, runs the app locally in the worktree where feasible, and posts what it checked (test results, sample inputs/outputs for the risk area, screenshots where relevant). v1 evidence is local; throwaway Azure review environments are a later addition.
4. **PR comment loop.** Developer's PR review comments trigger an "address PR comments" run that pushes fixes, without re-tagging the ticket.
5. **Second-opinion reviewer.** Implementer and gate reviewer can be different agents (e.g. Claude Code implements, Codex reviews), configurable per project.
6. **"Waiting on you" digest.** One view (and a daily summary) across all projects: gates waiting on the developer, what was auto-approved, what is running, what is blocked.
7. **Guardrails.** Per-ticket limits on cost, wall-clock time, and retries. When one trips, the ticket parks in Blocked with one clear note — no retry storms, no notification floods.

### Anti-babysitting features

Following from the anti-goal:

- **Touch tracking.** Dashboard shows, per project and stage, how often the developer intervened (send-backs, overrides, manual fixes) and the trend.
- **Feedback sticks.** On a send-back or risk override, Symphonist offers to save the lesson to project config (new risk area, prompt note) so the same correction isn't needed twice.
- **One-time setup.** Onboarding (auto-mapped states, suggested risk areas, default templates) should make a new project usable after a single review.

## 9. User interfaces

- **Dashboard** (local web, essential): the pipeline board. Columns = pipeline stages, cards = tickets, filter by project/client, source badges (Jira/ADO/…), risk badge, highlight on cards waiting on the developer, running time/cost on active cards, blocked count. Card detail shows run history, outputs, gate reasoning, links, and approve / send-back actions. Also: project setup (connections, state mapping, risk areas, pipeline, templates, guardrails), digest view, shadow-mode and touch stats. Live updates.
- **CLI** (wanted): status, list waiting gates, approve, send back with comment, pause/resume project, trigger poll, show ticket.
- **Tray icon** (later): status at a glance, open dashboard, pause.

## 10. Technical choices (builder's defaults — the developer has not objected; revisit freely)

- .NET 10. One local process hosting the background worker, local web API, and dashboard.
- Dashboard: Blazor Server with live updates over SignalR (keeps the stack all C#).
- CLI: a separate small .NET tool that talks to the local API.
- Tray app: small Windows app, later.
- Storage: SQLite in the user's profile folder (tickets, states, runs, gate decisions, stats).
- Secrets: OS credential store (Windows Credential Manager first), never in config files.
- Project config: human-editable files (YAML or JSON), also editable through the setup screen.
- Aspire AppHost + ServiceDefaults + OpenTelemetry for development; each agent run traced.
- Windows first; avoid anything that blocks macOS/Linux later.
- Local API bound to localhost only.
- Containers driven through the Docker Engine API (e.g. Docker.DotNet) or the `docker` CLI — builder's choice.

## 11. Left to the builder

- Internal architecture, project/solution layout, and persistence schema.
- Exact output-contract format between agents and Symphonist.
- Exact default prompt wording (starting from the developer's current prompt).
- Config file format details and validation.
- Polling intervals and default guardrail values.
- How evidence is captured per project type.
- Delivery order within v1, as long as the first milestone is one real ticket going end to end on Jira + Bitbucket + Claude Code.

## 12. Builder's calls (made during review; the developer should check these)

- **Release cadence interpretation:** "project defines the release cadence" taken to mean continuous or scheduled windows (section 5.6).
- **Production promotion (v1 project):** "To prod" = Symphonist opens a PR from main into the production branch and merges it once the gate passes, matching today's manual process. "Deploy succeeded" = the Bitbucket Pipelines run (deployment step) on the production branch for that merge commit succeeds; failure moves the ticket to Blocked. Other projects may configure a release pipeline trigger instead.
- **One repo per ticket in v1.** A project may list several repos, but each ticket works in exactly one (chosen at triage). The data model should not preclude multi-repo tickets later.
- **Cost guardrails** limit wall-clock time and agent turns always, and dollars when the agent reports cost. Hitting an agent usage/rate limit pauses the queue (a distinct "Paused: usage limit" status on the dashboard) rather than marking tickets Blocked.
- **Notifications:** Windows toast notification when a gate starts waiting on the developer or a ticket goes Blocked; daily digest shown in the dashboard and as a toast summary. Email/Slack/Teams are later options. Jira's own comment emails continue as they do today.

## 13. Open items

- The developer's second (timer-based) prompt arrived as a duplicate of the daily prompt; if it differs, it should inform the default templates. Not blocking.
