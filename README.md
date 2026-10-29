# Symphonist

A local, cross-platform (.NET 10) orchestrator for agentic development. It watches Jira tickets or GitHub Projects issues for your projects,
runs Claude Code on them stage by stage (triage, plan, implement, build, release, deploy), pauses at the gates you configure
for your approval, and drives the tracker card (Jira or GitHub Projects) and the pull requests (Bitbucket or GitHub)
along the way. A host process does the work; the `symphonist` CLI talks to it over a local API.

## Prerequisites

- .NET 10 SDK.
- A Docker-compatible engine, running (needed for the default `container` isolation).
- The `claude` CLI, logged in (used once to create a long-lived token). Only needed for projects that use Claude Code;
  see [Agent providers](#agent-providers) for Codex.
- git, with credentials for Bitbucket or GitHub already working on this machine. Symphonist pushes from the host with your git
  credentials; they are never given to the agent.
- A Jira Cloud account and a Bitbucket Cloud account for the project you want to automate, or a GitHub account (github.com)
  if the project uses GitHub Projects, a GitHub repository and GitHub Actions. The slots can be mixed, see step 5.

## One-time setup

Secrets live in the OS credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service).
`symphonist secret set <name>` prompts for the value without echo (or reads one line from stdin when piped); the value
is never taken from arguments.

1. Claude token (container mode):

   ```
   claude setup-token
   symphonist secret set claude-code-token
   ```

   Projects that run Codex need their own sign-in instead; see [Agent providers](#agent-providers).

2. Jira: create an Atlassian API token (id.atlassian.com, Security, API tokens). Jira authenticates with your account
   email plus the token.

   ```
   symphonist secret set jira-<id>
   ```

3. Bitbucket: create a Bitbucket API token with these scopes: repositories read/write, pull requests read/write,
   pipelines read.

   ```
   symphonist secret set bitbucket-<id>
   ```

   The names `jira-<id>` and `bitbucket-<id>` are whatever you put in `tracker.tokenSecret` and `repo.tokenSecret` of the
   project file; `claude-code-token` is fixed.

4. GitHub (instead of, or next to, Jira and Bitbucket): create one GitHub token and store it. One token and one
   `tokenSecret` can serve all three GitHub slots (the Projects board, the repository and Actions).

   ```
   symphonist secret set github-<id>
   ```

   - Personal access token (classic): scopes `repo` and `project`. This works for everything, including boards owned by a
     user account, and is the simplest choice.
   - Fine-grained personal access token: works for boards owned by an organization. Set the resource owner to the
     organization and grant these repository permissions on the repo: Issues read/write, Pull requests read/write,
     Contents read/write (merging a pull request needs Contents write), Actions read, plus the organization permission
     Projects read/write. GitHub's docs list "access Projects owned by a user account" as a fine-grained token gap, so a
     board under a personal account needs a classic token.

   GitHub's docs on these gaps say they are not permanent, so re-check them if you want a fine-grained token for a
   personal board.

5. Project: add it on the dashboard's setup screen (see [Adding a project](#adding-a-project)), which also stores its
   secrets. Or, by hand, copy `samples/projects/orders.example.yaml` (Jira and Bitbucket) or
   `samples/projects/orders-github.example.yaml` (all GitHub) to `~/.symphonist/projects/<name>.yaml` and edit it. The
   example documents every field. The host reads `*.yaml` / `*.yml` from the `projects` folder of `$SYMPHONIST_HOME`
   (default `~/.symphonist`). An invalid file is logged and skipped, as are all files declaring the same project `id`;
   other projects still load. After editing a file by hand, run `symphonist projects reload` (or use Reload projects on
   the board); the setup screen reloads by itself. A ticket whose stage you removed is blocked until you send it back
   to an existing stage. Docker images are prepared when the host starts, so after adding a container project or
   changing its `agent` image or packages by hand, run the container check on the project's setup screen (or restart
   the host) before its first agent run. If the host could not prepare images at startup (Docker was down, or no
   project used containers yet), such a project's runs are refused with "agent image not prepared" until its check
   passes.

   Each of `tracker`, `repo` and `build` picks its provider with `type`, and you can mix them: for example a Jira
   tracker with a GitHub repository, or a GitHub Projects board with a Bitbucket repository. The one rule is that the
   build provider is tied to the repo host: `build.type: github-actions` needs `repo.type: github`, and
   `bitbucket-pipelines` needs `repo.type: bitbucket`. Types: tracker `jira` | `github-projects`; repo `bitbucket` |
   `github`; build `bitbucket-pipelines` | `github-actions`.

6. The CLI is packaged as a .NET tool named `symphonist`. Either run it from source,
   `dotnet run --project src/Symphonist.Cli -- <command>`, or pack and install it:

   ```
   dotnet pack src/Symphonist.Cli -c Release -o artifacts
   dotnet tool install -g Symphonist.Cli --add-source artifacts
   ```

### Headless machines and Linux

Setting `SYMPHONIST_SECRET_<NAME>` overrides the credential store (name in upper case, `-` becomes `_`), for example
`SYMPHONIST_SECRET_CLAUDE_CODE_TOKEN` or `SYMPHONIST_SECRET_JIRA_ACME`. When set, the OS store is not touched. On Linux
the credential store needs Git Credential Manager's `GCM_CREDENTIAL_STORE` configured (`secretservice`, `gpg`, ...); on a
headless Linux machine use the environment overrides instead. The overrides are read by the host, so set them in the
environment that starts it. (`symphonist secret set` writes to the store `SYMPHONIST_SECRET_STORE` picks.)

`SYMPHONIST_SECRET_STORE` picks where secrets are kept, for the host and the CLI alike:

- unset or `os`: the OS credential store (default).
- `file`: `secrets.json` in `SYMPHONIST_HOME`, for machines without a usable credential store. It is readable by your
  user only: mode 0600 on macOS and Linux, encrypted for your Windows account (DPAPI) on Windows. Anyone who can log in
  as you can read it.
- `memory`: kept by the running process only and lost when it stops; nothing is written. For development and manual
  testing, together with a throwaway `SYMPHONIST_HOME`, so trying the setup screen never touches your real secrets.

## Starting the host

```
dotnet run --project src/Symphonist.Host
```

During development the Aspire AppHost (`src/Symphonist.AppHost`) starts it as well.

- The API listens on `127.0.0.1` only, port `5199` by default; override with `SYMPHONIST_PORT`. The CLI reads the same
  variable, so set it for both if you change it. The host refuses to serve if it ends up bound to anything else.
- Every API call needs the token in `~/.symphonist/api-token` (created on first start); the CLI reads it for you.
- Desktop notifications are on by default on Windows; see "Desktop notifications" below for turning them off.
- State (ticket database, work copies, API token, `paused.json`) lives in `SYMPHONIST_HOME`, default `~/.symphonist`.
- If any project uses container isolation, the host first removes agent containers left over from a previous run and
  makes sure the agent image (and any `agent.packages` image) exists. The first image build can take several minutes
  and delays startup (and the first poll) until it finishes. If Docker is not running, the host still starts, but agent
  stages of container projects fail with a clear error until you start Docker and run the container check on the
  project's setup screen (no restart needed; restarting the host also works).
- The container check (setup screen, optional) checks that Docker answers, builds the agent image (and any
  `agent.packages` image) when missing or stale, then lets an agent build and test the repository in a throwaway clone
  (`work/<id>/_container-check`, deleted before and after; nothing is committed or pushed), with the project's normal
  container sandbox and `allowedDomains`. A passing Docker step switches container runs back on, and a ready image
  clears that image's startup error, so no restart is needed. It can be cancelled, and a failing check does not stop
  you saving. Host-isolated projects skip it. Its log is written to `logs/<id>/_onboarding/container-check-<time>.json`.

## Adding a project

The dashboard's setup screen adds a project, or edits one, without touching YAML:

1. Run `symphonist dashboard` and choose **Add project** on the board. To edit an existing project, open its board and
   choose **Setup**.
2. Pick the tracker, repository and build providers. The fields shown are the ones that provider's section of the
   project file takes; required ones say "(required)". For Jira, entering a project key fills a blank JQL with
   `project = KEY AND assignee = currentUser() AND labels = Claude AND statusCategory != Done`.
3. Fill in the connections, and enter each secret in its password field with **Save secret**. The value goes straight to
   the OS credential store; it is never shown again, kept by the page or written to the project file. The default
   names follow the project id (`jira-<id>`, `bitbucket-<id>`, `github-<id>`). GitHub token scopes are as in
   [One-time setup](#one-time-setup) step 4.
4. **Check connections**. Each check says Passed, Failed or Skipped, with the reason. Nothing is written to the tracker or
   the repository.
5. **Propose mapping and risk areas**. An agent reads the tracker's statuses and a read-only clone of the repository and
   proposes the statuses for each stage, extra stages and risk areas, each with its reason.
6. Review the proposal, edit it, and **Apply proposal**. Applying changes only the form: stages it changes show
   "(changed)", proposed stages are added only if you tick them, and a status your tracker does not have says so next to
   the field.
7. Optionally **Run container check** (container projects): it builds the agent image and lets an agent build and test
   the repository in a throwaway clone.
8. **Save project**. Nothing reaches the projects folder or the orchestrator before this. The file is checked by the
   same loader the host uses before it is written, a new project is saved as `<id>.yaml`, and the projects reload.

Editing an existing project rewrites only the sections you changed: comments and formatting elsewhere in the file stay,
comments inside a changed section are lost, and the previous file is kept as `<file>.bak`. A save is refused (and
nothing written) when the file changed on disk since you opened the screen, or when the pipeline drops a stage that
active tickets sit at. The project id cannot be changed.

## Daily use

```
symphonist status [--project <id>]          tickets, those waiting on you first
symphonist show <project> <key>             one ticket and its event history
symphonist approve <project> <key>          approve the stage the ticket is waiting on
symphonist send-back <project> <key> -m "feedback" [--to <stage>]
symphonist unblock <project> <key>          retry a blocked ticket
symphonist forget <project> <key>           drop a ticket from Symphonist only (Jira, Bitbucket and the workspace
                                            are untouched; if it still matches the query it is picked up again as new)
symphonist release <project> [--yes]        Release now: ship the tickets waiting at the release stage as one release
symphonist limits                           list providers paused by a usage limit
symphonist limits resume <key>              let a paused provider start jobs again now
symphonist poll                             ask the host to poll now
symphonist projects                         list projects (and any problem stopping one)
symphonist projects pause <id>              stop working on a project (kept in paused.json)
symphonist projects resume <id>
symphonist projects reload                  reload the project files after editing them by hand (lists skipped files)
symphonist dashboard [--no-browser]         open the dashboard in your browser
```

Exit codes: `0` success, `1` the host rejected the request, bad usage or another error (one-line message), `2` the host
is not running.

### Dashboard

```
symphonist dashboard [--no-browser]
```

The host serves a live pipeline board at `http://127.0.0.1:5199/` (the same listener as the API). It shows every ticket
as a card in its stage column, with the ones waiting on you highlighted and labelled, any problem that stops a project
(for example a missing secret) as a banner, Pause/Resume per project, "Poll now", and a detail page per ticket where you
can approve, send back, or unblock. It updates live; if the host stops, the page says it is reconnecting and reloads
when the host is back.

- `symphonist dashboard` asks the host for a sign-in link and opens it in your default browser. The link is single-use
  and valid for 2 minutes; the sign-in then lasts 30 days in that browser.
- `--no-browser` prints the link instead. It only works for a browser on the same machine, because the host listens on
  loopback only.
- The dashboard needs its static assets (`blazor.web.js`), so run the host with `dotnet run` / the launch profile
  (Development) or from a `dotnet publish` output. An unpublished build run in Production returns 500 for
  `blazor.web.js`.

### Releases

Tickets that reach a `release` stage wait there instead of each opening their own pull request. **Release now**
(the button on the board, or `symphonist release <project>`) ships all of them together as one release.

- Release now lists the tickets that will ship, and under "what else ships" any other tickets already merged to
  `mainBranch` (and whether they finish with this release) and any commits from outside Symphonist. Confirm to
  continue; `--yes` on the CLI skips the question.
- Symphonist freezes a copy of `mainBranch` as `symphonist/release-{n}` and opens one pull request from it into the
  production branch. Later merges to `mainBranch` do not change what ships.
- A release always waits for one more approval from you, even when the release stage has `gate: none`. Approve it from
  the dashboard, the CLI, or the PR.
- Sending a release back cancels it: the PR is closed and the tickets go back to waiting at the release stage.
- Moving a tracker card never approves a release. Use Symphonist or the PR.
- A PR merged outside Symphonist completes the release; one closed outside Symphonist cancels it.
- Release PRs opened per ticket before release batches existed finish the old way.

The same thing is available over the API: `GET /api/projects/{id}/release` previews the release and
`POST /api/projects/{id}/release` starts it.

### Usage limits

When Claude Code or Codex says you have used up your plan, Symphonist stops starting jobs for that provider and sign-in
and keeps everything else running. Pauses are per provider and sign-in: `claude-code`, `codex:chatgpt`, and
`codex:api-key:{secret}` for each Codex API key.

- A pause ends at the reset time the provider gave. Without one, Symphonist tries again every 30 minutes.
- A plain rate limit pauses for 5 minutes.
- A pause never costs a ticket an attempt; the job simply runs later.
- The board shows a badge for each paused provider with a Resume button.

```
symphonist limits                  list paused providers and when they resume
symphonist limits resume <key>     let that provider start jobs again now
```

The API has `GET /api/limits` and `POST /api/limits/resume`.

### Touch stats

How often does a ticket need you? The strip at the top of the board and the Stats page (`/stats`) count touches:
sending a ticket back, unblocking it, and approving a round the risk reviewer called high risk. The page shows eight
weekly buckets; weeks start on Monday in your local time.

### Desktop notifications

On Windows, Symphonist shows a notification when a ticket is waiting on you, a ticket is blocked, a release is ready,
and when a provider is paused by or resumed from a usage limit. Notifications that arrive close together are folded
into one over 30 seconds, and clicking one opens the dashboard. Set `SYMPHONIST_NOTIFICATIONS=off` to turn them off.
On macOS and Linux this does nothing yet.

### Reopened tickets

If QA moves the card of a Done ticket to the entry status of a stage, Symphonist restarts the ticket at that stage on
a fresh branch from `mainBranch`.

### Working from Jira instead

The pipeline in the project file maps stages to Jira statuses, so you can drive everything from the board. On a GitHub
Projects board the same applies with the board's Status column: move the card to another column instead of changing a
Jira status:

- `entryStatuses`: a ticket first seen in one of these statuses starts at that stage.
- `onEnterStatus`: Symphonist moves the card to this status when the stage starts.
- `onGateStatus`: Symphonist moves the card here when the stage is waiting for you.
- `approvedWhenStatus`: you approve the stage by moving the card to this status (same as `symphonist approve`).
- Moving a card backwards, to an earlier stage's status, sends the ticket back to that stage. Moving it back to the
  current stage's `onEnterStatus` while that stage waits for you (for example In Review -> In Progress at the implement
  gate) reruns the current stage. Your latest comment that is not one of Symphonist's own (those end with
  `— posted by Symphonist`) is passed to the agent as feedback.
- A move made while a stage is still pending or running (an approval or a backwards move) is remembered and applied
  when the stage finishes; the event log notes `will apply when the stage finishes`. The status a card has when
  Symphonist first picks it up is never treated as an approval.
- Reopening a card after its ticket is `Done` is noted in the event log; Symphonist does not reopen done tickets.

### Answering Symphonist from Jira

Start a Jira comment (or a GitHub issue comment) with `@claude` while a ticket waits at a gate (or is blocked), and
that stage reruns with the
comment (minus the marker) as feedback, for example to answer triage's questions. Several such comments made before the
rerun go together into one rerun. A comment made while the stage runs applies when it reaches the gate. Other
comments are ignored, and a card move made in the same poll takes priority. On GitHub an `@claude` comment also
notifies the GitHub user `claude` and triggers the Claude GitHub App if you have it installed on the repository.
Symphonist reads only the first 20 assignees and 50 labels of an issue, so keep the assignee and label within those.

The marker is `@claude` unless the project sets `commentMarker` (an `@` and a name of letters, digits, `-` and `_`, at
most 39 characters, matched without regard to case). Quote it in the YAML, because a bare `@` starts a reserved YAML
indicator. The same marker drives Jira, GitHub issue and PR comments, and it is also on the project setup screen:

```yaml
commentMarker: "@symphonist"
```

Symphonist breaks the marker (and `@claude`) in the comments it posts, so its own text never reads as a request.

### Answering Symphonist on the PR

At the implement stage you can also steer the fix from the pull request. A PR comment that starts with the comment marker (`@claude`, or the project's `commentMarker`) reruns
implement on the same PR and branch, with the comment (minus the marker) as feedback. Symphonist replies to each
comment it addressed.

- Only your own comments count by default (the PR host user that Symphonist's token belongs to). To act on teammates'
  comments too, set `prComments.fromOthers: true`. Those comments then direct an agent that pushes under your
  account, so enable it only for a team you trust.
- Inline code comments count too, with the lines around them passed to the agent.
- Each comment triggers at most one run. Several made together, or together with an `@claude` tracker comment, go into
  one run. A comment made while implement runs applies when it reaches the gate. Symphonist's own comments are signed
  and never trigger anything.
- A comment made while the stage is approved and waiting to merge applies if the merge is held and the gate comes
  back. Once the PR has merged, a comment that arrived too late gets a "merged first" reply asking you to open a
  follow-up ticket.
- A comment from you reruns the gate round like an `@claude` tracker comment. A teammate's comment (with `fromOthers`)
  reruns it without deciding the gate.
- Upgrading: on the first poll after you upgrade to a version with PR comments, `@claude` PR comments made since the
  implement stage started, on tickets already waiting at the implement gate, are acted on.

```yaml
prComments:
  fromOthers: false   # the default
```

### Risk reviewer

A second agent can judge each gate, rate it `low` or `high` risk and give its reasons. It is off by default, so nothing
runs and nothing extra is spent until you opt in.

```yaml
riskAreas:
  - name: pricing
    description: Anything that changes how order prices, discounts or tax are computed.
    paths: ["src/Pricing/**"]
review:
  mode: shadow            # off (default) | shadow | active
```

- `riskAreas`: parts of the code that always need you. A `description` tells the reviewer what to look for, and `paths`
  are globs. At implement and release gates, a change that touches a matching path makes the verdict `high`, whatever
  the reviewer says. Triage and plan gates have no diff, so there only the `description` applies.
- `review.mode`: `off` (the default) runs no reviewer, and `auto` gates behave like `human`. `shadow` reviews every gate
  and shows the verdict, but approves nothing, so you can see how often you agree first. `active` also lets `auto` gates
  approve themselves.
- `gate: auto` on a triage, plan, implement or release stage approves it when the verdict is `low`. It needs
  `mode: active`. `human` gates are still reviewed in `active` mode, so the agreement numbers keep growing.
- The release gate is production auto-approval: `release: auto` ships to production without you, `release: human` (the
  default in the sample) turns it off. It is off unless you set it.
- Before an auto-approval Symphonist comments on the ticket, signed as usual: `Auto-approved {stage}: low risk.`, what
  was checked, and why it passed. If the comment can't be posted the gate is not approved and waits for you.
- Anything else waits on you: a `high` verdict, a reviewer that failed or timed out, or one that was unsure. The
  reviewer must return `high` when unsure. A change to agent configuration (`CLAUDE.md`, `AGENTS.md`, `.mcp.json`,
  anything under `.claude/`) is always `high`, and so is a change that adds or changes a symlink.
- An auto-approval covers only the reviewed commit. If the branch moved after the review, or the pull request would
  merge anything else (for example a fork's pull request with the same branch names), the merge is held and the gate is
  reviewed again. After two holds at a stage it waits for you instead.
- The reviewer can only read. In container mode the workspace is mounted read-only. In host mode it gets only `Read`,
  `Grep` and `Glob`, and any other tool in `review.agent.allowedTools` is refused. In both modes `Write`, `Edit`,
  `NotebookEdit`, `Bash`, `WebFetch` and `WebSearch` are denied, and the workspace's own Claude Code settings, hooks,
  `CLAUDE.md` / `AGENTS.md` and MCP servers are not loaded (only your user settings are).
- Review runs count toward `MaxConcurrentAgentRuns` and toward a ticket's cost.
- `symphonist show <project> <key>` prints the latest review, `symphonist status` has a `RISK` column, and
  `symphonist projects` and the dashboard show how often you agreed with the reviewer per stage, how many low-risk
  verdicts you sent back, and how many gates were auto-approved. Look at these in `shadow` before turning on `active`.

### Evidence on high-risk PRs

Before a gated implement round waits on you, an agent can run the app from the pushed branch and report what it saw, so
you review a result instead of a diff alone. It runs once per pushed head, only once the round has a pull request, and
only when it is due:

- `mode: high-risk` (the default): with the risk reviewer on, when its verdict is `high` (a change that touches a
  `riskAreas` path is always `high`); a `low` verdict gathers nothing. With the reviewer off, when the change touches a
  `riskAreas` path; without `riskAreas` it never runs.
- `mode: always`: every gated implement round. `mode: off`: never.

The report has three parts: a regression analysis (what the change could break, especially areas it was not meant to
touch), test coverage of the affected areas with the result of the test suite, and a walkthrough of the app's major
features with screenshots. It also says which of its steps were guesses rather than taken from your config. It appears as a signed comment on the PR, as a line on the ticket in the tracker, and on the dashboard, where
the card and ticket detail show the states in words (Passed, Failed, Not run, Guessed) and the screenshots. A card
whose evidence is due shows "Gathering evidence" and still counts as waiting on you.

Evidence is advice for you. It never approves, blocks or fails a stage, and it never commits or pushes; it works in a
throwaway copy of the branch. If you decide the gate (approve, send back, or move the card) while it runs, it is
cancelled and nothing is posted.

```yaml
evidence:
  mode: high-risk                  # off | high-risk (default) | always
  # prompt: prompts/evidence.md    # optional prompt override, relative to the project file
  run:                             # how to start the app; every field is optional
    setup: [npm ci]                # at most 10 commands, run in order
    start: npm start
    url: http://localhost:3000
    ready: /health                 # what to wait for before using the app
    notes: Sign in as demo@example.com
  features:                        # at most 20 things to exercise, in your own words
    - { name: Checkout, steps: Add an item to the basket and pay with the test card }
  agent: { timeoutMinutes: 30 }   # only timeoutMinutes and model (default: agent.model) here; the rest comes from `agent`
```

Onboarding can propose `run` and `features` for you.

### Agent providers

Any job (a stage, the risk reviewer, evidence) can run on Claude Code or on OpenAI Codex. `agent.type` is
`claude-code` (the default) or `codex`, and the stage kinds, the reviewer and evidence can each override it:

```yaml
agent:
  type: claude-code               # claude-code | codex
  codex:                          # only read when a job uses Codex
    auth: api-key                 # api-key | chatgpt
    apiKeySecret: openai-api-key  # secret name; the default, and only with auth: api-key
pipeline:
  - { id: plan, kind: plan, gate: human, agent: { type: codex, timeoutMinutes: 30 } }   # type, model, timeoutMinutes
review:
  mode: shadow
  agent: { type: codex }
evidence:
  mode: high-risk
  agent: { type: claude-code }
```

An override that switches provider without naming a `model` uses that provider's default model, not the project's
`agent.model`.

- Codex runs only in containers for now: `agent.isolation: host` together with Codex is a load error.
- `auth: api-key`: store the key in the OS credential store, the same way as the Claude token, under the
  `apiKeySecret` name (default `openai-api-key`):

  ```
  symphonist secret set openai-api-key
  ```
- `auth: chatgpt`: run `codex login` once on this machine. Symphonist copies `auth.json` into each run and writes a
  refreshed login back afterwards (only if it is the same account). `CODEX_HOME` is honoured.
- A run only receives the credential of its own provider.
- No dollar cost is shown for Codex runs.
- The default agent image is `symphonist/agent:2`, which has both CLIs. A custom `agent.image` must contain the CLIs
  the project uses; the setup check verifies this. Images built `FROM` the old `symphonist/agent-claude:1` keep working
  for Claude-only projects; rebuild them on `symphonist/agent:2` to use Codex.
- The container firewall allows each provider's own domains automatically (Codex: `api.openai.com`; with
  `auth: chatgpt` also `auth.openai.com` and `chatgpt.com`), on top of `agent.allowedDomains`.

### Sandbox

- `container` (default): the agent runs in Docker with the workspace mounted, `.git` mounted read-only, and a network
  allowlist (Claude Code's own hosts plus `agent.allowedDomains`; HTTPS/HTTP on TCP 443/80 only). Jira, Bitbucket and git
  credentials stay on the host; the only credential a container receives is the one for its own provider (the Claude token, or the Codex sign-in). Symphonist itself commits,
  pushes and opens PRs from the host using your git credentials. If the origin URL of your checkout embeds credentials
  (`https://user:token@...`), Symphonist strips them from agent workspaces (and masks them in errors) and logs a
  warning; pushes then authenticate through your git credential helper, so make sure one is configured. Git never
  prompts (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`): missing credentials fail the stage with git's error.
- Extra tools for the agent (for example the `graphify` CLI): list them under `agent.packages` in the project file,
  as `apt`, `pip` and/or `npm` packages. At startup the host builds an image `symphonist/agent-packages:<hash>` on top
  of the agent image (`agent.image` or the default) with them installed, and agent and reviewer runs of that project use
  it. Installing happens at build time with normal network access; the agent still runs behind the allowlist, so a tool
  that calls out at run time needs its hosts in `agent.allowedDomains`. pip packages go into a virtualenv on `PATH`
  (python3 is installed for you). Items must be plain package names with an optional version (`graphifyy==0.8.33`,
  `@scope/tool@1.2.0`); anything else is rejected when the project file loads. The image is rebuilt when the package
  list or the base image changes. A failed build fails that project's agent stages with the build error.

  ```yaml
  agent:
    packages:
      pip: [graphifyy]
  ```
- `host`: the agent runs directly on your machine with no sandbox, restricted only by `agent.allowedTools`, which is
  therefore required. Use it only for projects you trust.

## First live run checklist

Do this once against a throwaway ticket (the real client Jira and Bitbucket, or a sandbox Jira project and Bitbucket
repo with a trivial `bitbucket-pipelines.yml`; or a throwaway GitHub Projects board and repo with a trivial workflow).

1. Complete the setup above, start the host, and confirm `symphonist projects` lists the project.
2. Pick a low-risk ticket: label `Claude`, assigned to you, status Backlog. Run `symphonist poll`; it appears in
   `symphonist status` at the triage stage.
3. Walk it through every stage. Approve with the CLI (`symphonist approve ...`) at least once and by moving the Jira card
   to the stage's `approvedWhenStatus` at least once. Use `symphonist send-back <project> <key> -m "..."` once and watch
   the stage rerun with your feedback.
4. Expected result: the ticket reaches `Done`; Jira shows the triage comment, the plan links, the PR link and the release
   PR link; Bitbucket shows the feature PR merged to `main` and the release PR merged to `production`;
   `symphonist show` lists every transition.
5. Note anything that needed a manual fix (for example in `docs/superpowers/notes/`) as input for the next plan.

## Troubleshooting

- Ticket is `Blocked` (a stage failed `maxAttemptsPerStage` times, or the ticket no longer matches the JQL): run
  `symphonist show <project> <key>` for the reason and events, fix the cause, then `symphonist unblock <project> <key>`.
- Build or Deploy is `Blocked` with `build failed` or `no pipeline ran for <commit>`: if the pipeline failed for a
  passing reason (flaky test, runner outage, pipeline not triggered), re-run it in Bitbucket, then
  `symphonist unblock <project> <key>`; the stage watches the same commit again with a fresh timeout. If the code needs
  a fix, either land the fix on `main` yourself and `symphonist send-back <project> <key> -m "fixed on main" --to release`
  (a new release PR is opened), or `symphonist send-back <project> <key> -m "<what broke>" --to implement` to let the
  agent fix it.
- Event `already in production`: `production` already contained `main` when the release stage ran (for example it was
  released together with another ticket), so no release PR was opened. Approve the release gate as usual; deploy then
  watches the pipeline of the current `production` head.
- Event `no transition to '<status>' in tracker`: the Jira workflow has no transition from the card's current status to
  that status. Symphonist records it and carries on; fix the status names in the project file or the Jira workflow.
- GitHub: an issue is `Blocked` with `no longer matches tracker query` when it was closed, unassigned from the token's
  user, lost the label, or moved to a column outside `statuses`. Build or Deploy `Blocked` with `build failed` means a
  workflow run on the commit failed; re-run it in GitHub Actions, then `symphonist unblock <project> <key>`. Runs
  triggered by issue comments, schedules, manual dispatches and other non-code events are ignored.
- Docker not running: container stages fail with a Docker error. Start Docker, then run the container check on the
  project's setup screen; when it passes, container runs work again without a restart. (Availability is otherwise
  checked only at startup, so restarting the host also works.) Run the check once per container project whose image
  was not prepared.
- `Symphonist host is not running on 127.0.0.1:<port>` (exit code 2): start the host, and check that `SYMPHONIST_PORT`
  matches for host and CLI. The same message appears if `~/.symphonist/api-token` does not exist yet.
- A project is missing from `symphonist projects`: run `symphonist projects reload`; it names each skipped file and
  why (the board shows the same as banners, and the host log as `Skipping project file ...`).
- A project's tickets never move and `symphonist projects` shows a `PROBLEM` (for example `secret 'jira-acme' not set;
  run: symphonist secret set jira-acme`, or `tracker query failed: ...`): fix the cause (store the secret, check the
  token or JQL). The problem clears on the next poll that succeeds; run `symphonist poll` to retry now.

## Third-party notices

Secret storage uses Devlooped.CredentialManager, which is licensed under the Open Source Maintenance Fee EULA (OSMF): a
fee applies for use in revenue-generating activity. Check that licence before using Symphonist commercially.
