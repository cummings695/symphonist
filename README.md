# Symphonist

A local, cross-platform (.NET 10) orchestrator for agentic development. It watches Jira tickets for your projects, runs
Claude Code on them stage by stage (triage, plan, implement, build, release, deploy), pauses at the gates you configure
for your approval, and drives the Jira card and the Bitbucket pull requests along the way. A host process does the work;
the `symphonist` CLI talks to it over a local API.

## Prerequisites

- .NET 10 SDK.
- A Docker-compatible engine, running (needed for the default `container` isolation).
- The `claude` CLI, logged in (used once to create a long-lived token).
- git, with credentials for Bitbucket already working on this machine. Symphonist pushes from the host with your git
  credentials; they are never given to the agent.
- A Jira Cloud account and a Bitbucket Cloud account for the project you want to automate, or a GitHub account (github.com)
  if the project uses GitHub Projects, a GitHub repository and GitHub Actions. The slots can be mixed, see step 4.

## One-time setup

Secrets live in the OS credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service).
`symphonist secret set <name>` prompts for the value without echo (or reads one line from stdin when piped); the value
is never taken from arguments.

1. Claude token (container mode):

   ```
   claude setup-token
   symphonist secret set claude-code-token
   ```

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

5. Project file: copy `samples/projects/orders.example.yaml` (Jira and Bitbucket) or
   `samples/projects/orders-github.example.yaml` (all GitHub) to `~/.symphonist/projects/<name>.yaml` and edit it. The
   example documents every field. The host reads `*.yaml` / `*.yml` from the `projects` folder of `$SYMPHONIST_HOME`
   (default `~/.symphonist`). An invalid file is logged and skipped, as are all files declaring the same project `id`;
   other projects still load. Projects are loaded when the host starts, so restart it after editing.

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
environment that starts it. (`symphonist secret set` always writes to the OS store.)

## Starting the host

```
dotnet run --project src/Symphonist.Host
```

During development the Aspire AppHost (`src/Symphonist.AppHost`) starts it as well.

- The API listens on `127.0.0.1` only, port `5199` by default; override with `SYMPHONIST_PORT`. The CLI reads the same
  variable, so set it for both if you change it. The host refuses to serve if it ends up bound to anything else.
- Every API call needs the token in `~/.symphonist/api-token` (created on first start); the CLI reads it for you.
- State (ticket database, work copies, API token, `paused.json`) lives in `SYMPHONIST_HOME`, default `~/.symphonist`.
- If any project uses container isolation, the host first removes agent containers left over from a previous run and
  makes sure the agent image (and any `agent.packages` image) exists. The first image build can take several minutes
  and delays startup (and the first poll) until it finishes. If Docker is not running, the host still starts, but agent
  stages of container projects fail with a clear error until you start Docker and restart the host.

## Daily use

```
symphonist status [--project <id>]          tickets, those waiting on you first
symphonist show <project> <key>             one ticket and its event history
symphonist approve <project> <key>          approve the stage the ticket is waiting on
symphonist send-back <project> <key> -m "feedback" [--to <stage>]
symphonist unblock <project> <key>          retry a blocked ticket
symphonist forget <project> <key>           drop a ticket from Symphonist only (Jira, Bitbucket and the workspace
                                            are untouched; if it still matches the query it is picked up again as new)
symphonist poll                             ask the host to poll now
symphonist projects                         list projects (and any problem stopping one)
symphonist projects pause <id>              stop working on a project (kept in paused.json)
symphonist projects resume <id>
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

### Working from Jira instead

The pipeline in the project file maps stages to Jira statuses, so you can drive everything from the board:

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

Start a Jira comment with `@claude` while a ticket waits at a gate (or is blocked), and that stage reruns with the
comment (minus the marker) as feedback, for example to answer triage's questions. Several such comments made before the
rerun go together into one rerun. A comment made while the stage runs applies when it reaches the gate. Other
comments are ignored, and a card move made in the same poll takes priority.

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

### Sandbox

- `container` (default): the agent runs in Docker with the workspace mounted, `.git` mounted read-only, and a network
  allowlist (Claude Code's own hosts plus `agent.allowedDomains`; HTTPS/HTTP on TCP 443/80 only). Jira, Bitbucket and git
  credentials stay on the host; the only secret the container receives is the Claude token. Symphonist itself commits,
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
repo with a trivial `bitbucket-pipelines.yml`).

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
- Docker not running: container stages fail with a Docker error. Start Docker and restart the host (availability is
  checked at startup).
- `Symphonist host is not running on 127.0.0.1:<port>` (exit code 2): start the host, and check that `SYMPHONIST_PORT`
  matches for host and CLI. The same message appears if `~/.symphonist/api-token` does not exist yet.
- A project is missing from `symphonist projects`: check the host log for `Skipping project file ...`.
- A project's tickets never move and `symphonist projects` shows a `PROBLEM` (for example `secret 'jira-acme' not set;
  run: symphonist secret set jira-acme`, or `tracker query failed: ...`): fix the cause (store the secret, check the
  token or JQL). The problem clears on the next poll that succeeds; run `symphonist poll` to retry now.

## Third-party notices

Secret storage uses Devlooped.CredentialManager, which is licensed under the Open Source Maintenance Fee EULA (OSMF): a
fee applies for use in revenue-generating activity. Check that licence before using Symphonist commercially.
