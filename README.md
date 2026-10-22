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
- A Jira Cloud account and a Bitbucket Cloud account for the project you want to automate.

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

4. Project file: copy `samples/projects/orders.example.yaml` to `~/.symphonist/projects/<name>.yaml` and edit it. The
   example documents every field. The host reads `*.yaml` / `*.yml` from the `projects` folder of `$SYMPHONIST_HOME`
   (default `~/.symphonist`). An invalid file is logged and skipped, as are all files declaring the same project `id`;
   other projects still load. Projects are loaded when the host starts, so restart it after editing.

5. The CLI is packaged as a .NET tool named `symphonist`. Either run it from source,
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
  makes sure the agent image exists. The first image build can take several minutes and delays startup (and the first
  poll) until it finishes. If Docker is not running, the host still starts, but agent stages of container projects fail
  with a clear error until you start Docker and restart the host.

## Daily use

```
symphonist status [--project <id>]          tickets, those waiting on you first
symphonist show <project> <key>             one ticket and its event history
symphonist approve <project> <key>          approve the stage the ticket is waiting on
symphonist send-back <project> <key> -m "feedback" [--to <stage>]
symphonist unblock <project> <key>          retry a blocked ticket
symphonist poll                             ask the host to poll now
symphonist projects                         list projects
symphonist projects pause <id>              stop working on a project (kept in paused.json)
symphonist projects resume <id>
```

Exit codes: `0` success, `1` the host rejected the request (or bad usage), `2` the host is not running.

### Working from Jira instead

The pipeline in the project file maps stages to Jira statuses, so you can drive everything from the board:

- `entryStatuses`: a ticket first seen in one of these statuses starts at that stage.
- `onEnterStatus`: Symphonist moves the card to this status when the stage starts.
- `onGateStatus`: Symphonist moves the card here when the stage is waiting for you.
- `approvedWhenStatus`: you approve the stage by moving the card to this status (same as `symphonist approve`).
- Moving a card backwards, to an earlier stage's status, sends the ticket back to that stage. Your latest comment that
  is not one of Symphonist's own (those end with `— posted by Symphonist`) is passed to the agent as feedback. A
  backwards move while a stage is pending or running is ignored and noted in the event log.

### Sandbox

- `container` (default): the agent runs in Docker with the workspace mounted, `.git` mounted read-only, and a network
  allowlist (Claude Code's own hosts plus `agent.allowedDomains`; HTTPS/HTTP on TCP 443/80 only). Jira, Bitbucket and git
  credentials stay on the host; the only secret the container receives is the Claude token. Symphonist itself commits,
  pushes and opens PRs from the host using your git credentials.
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
- Event `no transition to '<status>' in tracker`: the Jira workflow has no transition from the card's current status to
  that status. Symphonist records it and carries on; fix the status names in the project file or the Jira workflow.
- Docker not running: container stages fail with a Docker error. Start Docker and restart the host (availability is
  checked at startup).
- `Symphonist host is not running on 127.0.0.1:<port>` (exit code 2): start the host, and check that `SYMPHONIST_PORT`
  matches for host and CLI. The same message appears if `~/.symphonist/api-token` does not exist yet.
- A project is missing from `symphonist projects`: check the host log for `Skipping project file ...`.

## Third-party notices

Secret storage uses Devlooped.CredentialManager, which is licensed under the Open Source Maintenance Fee EULA (OSMF): a
fee applies for use in revenue-generating activity. Check that licence before using Symphonist commercially.
