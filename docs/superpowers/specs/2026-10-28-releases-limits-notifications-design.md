# Release batches, usage-limit pause, touch stats and Windows notifications (plan 7)

Status: draft for the developer's approval. Builds on `2026-10-21-symphonist-design.md` (sections 5.6, 8 and the
decision log) and plan 6 (swappable agent providers).

## Intent

Symphonist exists so the developer stops babysitting agents. Four gaps work against that today:

- **Releases ship more than they say.** Each ticket opens its own release PR from the main branch to production. The
  main branch already holds every merged ticket, so ticket A's release PR silently ships B and C too.
- **A usage limit blocks everything.** When Claude Code or Codex hits its usage or rate limit, the run counts as an
  ordinary failure. Every running ticket burns its attempts and lands in Blocked, and the developer must unblock each
  one after the limit resets.
- **The developer can't see whether they are needed less over time.** Approvals and send-backs are recorded, but
  nothing shows how often the developer stepped in, per stage, or the trend.
- **Symphonist is silent.** A gate waiting on the developer, or a blocked ticket, is only visible if the dashboard is
  open.

The developer runs Symphonist on Windows during development, so desktop notifications start with Windows.

## Release processes differ per project (the developer's framing)

Symphonist must fit different team processes, and these will be tweaked over time. Two real examples:

- **Project A.** Ticket branches merge into a development branch. A release is one PR from development to main; main's
  pipeline then builds and pauses at deployment steps (frontend, APIs) that the developer approves by hand, outside
  Symphonist. For Symphonist, a ticket is done when the release PR has completed.
- **Project B.** The developer branches from development, opens a PR, teammates approve it, it merges, and the
  pipeline builds and deploys on its own. QA owns everything after that. Symphonist's job ends when the PR merges; the
  ticket only comes back if QA reopens it with defects.

In Symphonist's terms: project A's development branch is `repo.mainBranch` and its main is `repo.productionBranch`;
its pipeline has a `release` stage and no `deploy` stage. Project B's pipeline has neither.

## Goals

1. **Release batches.** A release is a project-level event covering every ticket that is ready, not a per-ticket PR.
2. **Release now, proposed by Symphonist.** The developer starts a release with a button (dashboard) or command (CLI).
   Symphonist proposes one by notifying when tickets start waiting.
3. **Release completion finishes the tickets.** When the release PR completes (merged by Symphonist on approval, or
   merged outside Symphonist), every ticket in the batch moves on: to Done, or to a `deploy` stage if the project has
   one.
4. **Usage limits pause instead of block.** A recognised usage/rate-limit error pauses that provider's work and costs
   no attempt. Work resumes on its own.
5. **Touch stats.** Per project and stage: how often the developer stepped in, approvals by the developer versus
   automatic, and the trend.
6. **Windows notifications** for four events, folded so they never flood.
7. **Project B keeps working.** A pipeline with no release and no deploy stage finishes a ticket when its PR merges,
   and a ticket QA reopens is picked up again.

## Non-goals (this plan)

- Scheduled release windows ("Tuesdays 10:00"). Release is manual, with Symphonist proposing; a schedule can come
  later on the same batch model.
- Approving or watching deployment steps in the build system (project A's frontend/API approvals stay outside
  Symphonist).
- The daily "waiting on you" digest, and per-ticket cost and wall-clock limits. These are deferred to a later plan.
- Notifications on macOS and Linux (the abstraction is built; only Windows gets an implementation now), and email,
  Slack or Teams.
- Counting "manual fixes" (the developer's own commits on an agent branch) as touches. Symphonist can't detect them
  reliably yet.
- A "ticket finished" or "release merged" notification (the developer chose not to have these).

## Anti-goals

- A release PR that ships tickets it doesn't name.
- One usage limit turning into a wall of Blocked tickets, or a retry storm against a limited provider.
- Notification floods: one notification per event per ticket in a burst, or repeats for the same state.
- Release logic shaped around one team's process so another team's flow can't be expressed.
- Windows-only code in the core: the notifier sits behind a small abstraction like the secret store.

## Approaches considered

**Notifications** (the developer picked option 1):

1. **PowerShell script launched through the existing process runner.** Shows a standard Windows notification under the
   name "Symphonist"; a click opens the dashboard URL. No change to how Symphonist is built; macOS and Linux later
   become two more small scripts. About half a second of PowerShell start-up per notification, which folding keeps
   rare. *Chosen: fewest moving parts, matches the spec's "small per-platform abstraction".*
2. In-process with Microsoft's notification library. Richer, but it only builds for a Windows-specific target while
   Symphonist ships one cross-platform build, forcing awkward runtime loading.
3. A separate Windows-only helper program. Clean code, but a second program to build, ship and keep in step.

**Releases:** one sensible way. A per-ticket release can't avoid one PR shipping another ticket's work, so the release
becomes a project-level batch.

**Usage limits and touch stats:** one sensible way each (below).

## Design

### 1. Release batches

- A ticket that reaches a `release` stage no longer opens a PR. It waits in **ready to release** (moving its tracker
  card to the stage's `onEnterStatus` as today).
- **Release now** (dashboard banner, CLI command, API endpoint) creates a **release batch** for the project: every
  ticket ready at that moment, and one PR from `repo.mainBranch` to `repo.productionBranch`. The PR title and body
  name every ticket in the batch.
- **What else ships (the developer's decision).** The PR carries everything on `repo.mainBranch` that production lacks,
  not only ready tickets: tickets merged but not yet ready (still in build or UAT, or Blocked) and commits made outside
  Symphonist. Before creating the batch, Release now shows what else would ship ("also includes ORD-9, still in build,
  plus 2 commits from outside Symphonist") and asks the developer to confirm. The PR body lists them under "also
  shipped". Tracked tickets whose merge is in the PR join the batch and finish with it (they skip their remaining
  stages up to and including release). If nothing is ready but `repo.mainBranch` is ahead of production, Release now is
  still offered, with the same confirmation.
- One open batch per project. While its PR is open, Release now is unavailable and newly ready tickets wait for the
  next batch.
- The batch has one gate, decided once for the whole batch, not per ticket. It follows the `release` stage's gate
  settings (`human`, `auto`, review mode), except that **a release always needs one more approval after Release now**
  (the developer's decision, cautious for now): a `release` stage with gate `none` behaves like `human` for batches.
  Approving merges the PR (as release approval does today, including the existing "the reviewed branch moved" holds).
  The risk reviewer, when on, judges the batch PR once.
- **Sending back the batch** (builder's call) closes the release PR and returns its tickets to ready to release, with
  the developer's comment recorded on the batch. One ticket can't be pulled out of a batch, because its code is already
  on `repo.mainBranch`; a ticket with a defect is sent back to implement on its own, and its fix ships in a later batch.
- **Tracker card moves never approve a batch** (builder's call): only Symphonist (dashboard, CLI, reviewer on an
  auto gate) or merging the PR in the repo host does. Batch tickets' cards move to the stage's `onGateStatus` when the
  batch waits on its gate. If one ticket's card moves back to an earlier stage while its batch is open, the ticket stays
  in the batch, the batch gate shows a warning naming it, and after the merge that ticket goes to the earlier stage
  instead of advancing.
- Merged outside Symphonist: if the release PR is merged in the repo host by someone else, Symphonist detects it and
  treats the batch as complete, as if approved and merged.
- On completion, every ticket in the batch advances from the release stage: to the next stage (`deploy` watches the
  batch's merge commit, as today per ticket) or to Done.
- If the release PR is closed without merging, the batch is cancelled and its tickets go back to ready to release.
- The board shows a banner per project with ready tickets ("3 tickets ready to release" plus a **Release now** button),
  and the open batch with its PR link while one exists. The batch's tickets show it on their cards and detail page.

### 2. Usage limits

- Each provider runner recognises its own CLI's usage-limit and rate-limit errors and returns a distinct "usage limit"
  result carrying the reset time when the error gives one. The recognised texts live in one place per provider, so they
  are easy to update when a CLI changes its wording.
- A usage-limit result costs no attempt. The ticket goes back to waiting (Pending), not Blocked. An interrupted run
  starts again the way a retried run does today.
- The pause covers every job for that provider: stages, PR-comment fixes, the risk reviewer and evidence runs (builder's
  call). An auto gate whose reviewer is paused keeps waiting for its verdict; the developer can still approve it by
  hand, and the pause notification already tells them.
- The orchestrator pauses that **provider** (keyed by provider and the credential it runs with; see the builder's
  calls below): no new job for it starts, in any project. Jobs for other providers carry on, so a Claude limit doesn't
  stop Codex work.
- **Resuming.** With a known reset time, the pause lifts at that time. Without one, Symphonist lets one queued job
  through every 30 minutes; if that hits the limit again, the pause continues, still without costing an attempt. The
  developer can also resume by hand (dashboard and CLI).
- The pause survives a host restart.
- The board shows "Paused: Claude usage limit, resets 14:00" on each affected project. This is distinct from the
  developer's own project pause.

### 3. Touch stats

- **A touch** is the developer stepping in: a send-back (from a gate, from Approved or from Blocked), an unblock, or
  overruling the reviewer (approving a round the reviewer called high risk, or sending back one it called low).
- **Approvals** are shown separately as "approved by you / auto-approved", per stage.
- Source data: the existing gate-decision records (project, stage, automatic or not) and reviewer verdicts. Two gaps
  are closed: unblocks and send-backs from Approved or Blocked are recorded as structured records, not only as event
  text. A release batch decision counts once, against the release stage.
- **Board strip** under each project heading: touches this week versus last week, split into send-backs / unblocks /
  overrules, and "auto-approved N of M gates". Hovering shows per-stage numbers. The strip links to the stats page.
- **Stats page** (`/stats`, filterable by project): one row per stage with send-backs, unblocks, overrules,
  approved-by-you / auto, and an 8-week trend in weekly buckets (weeks start Monday, local time).
- Stats count from when the records exist; no backfill from event text.

### 4. Windows notifications

- Events (the developer's choice):
  1. a gate starts waiting on the developer (the board's existing "waiting on you": not while the reviewer still owes a
     verdict on an auto gate);
  2. a ticket goes Blocked;
  3. a project's ready-to-release group goes from empty to non-empty (not once per ticket);
  4. a provider is paused on a usage limit, and when it resumes.
- **Folding.** Events are collected briefly; several within the window become one notification ("3 tickets need you:
  2 waiting on you, 1 blocked"). One event alone gets its own detailed notification.
- **Clicking** opens the dashboard on the ticket (single ticket event) or the board (folded or project event) in the
  default browser.
- Notifications carry project name, ticket key and a one-line reason only, never agent output beyond that line or any
  secret. Windows' own Do Not Disturb and Focus settings silence them as usual.
- **Mechanism:** a notifier abstraction in the host with a Windows implementation that launches a bundled PowerShell
  script through the existing process runner (argument list, no shell string). The script shows the notification under
  an app name of "Symphonist", registered for the current user. On macOS and Linux the notifier does nothing and logs
  once that notifications aren't available yet.
- On by default on Windows, with one host-level switch to turn them off.

### 5. Project B and reopened tickets

- A pipeline without `release` and `deploy` stages finishes a ticket when its PR merges. This is believed to work
  today; the builder confirms it with a test and fixes it if not.
- A ticket Symphonist has finished (Done) that comes back into the tracker query with a status matching a stage's
  `entryStatuses` (QA reopening it with defects) starts again at that stage, keeping its history. Today the
  orchestrator deliberately ignores Done tickets ("does not reopen done tickets"); this changes that. On reopening
  (builder's call): a fresh branch from `repo.mainBranch`, attempts reset, its old PR, release and deploy references
  cleared, and earlier stages do not run again. A Done ticket whose status matches no `entryStatuses` stays Done.

## Constraints

- .NET 10; Windows, macOS and Linux all build and run. Processes launch with argument lists through `ProcessSpec`;
  paths are built with `Path.Combine`.
- No Windows-only APIs in the core; the notifier lives behind an abstraction.
- Existing project files keep loading. The meaning of a `release` stage changes (batch instead of per-ticket PR); no
  new required fields.
- State (batches, pauses, structured touch records) is persisted in the existing SQLite store with migrations, so it
  survives restarts.
- Codex reports no dollar cost; nothing in this plan depends on cost.

## Builder's calls (made in this design, revisit freely)

- The usage-limit pause is keyed by provider and credential (for example Claude with a given token, Codex with API key
  versus ChatGPT sign-in), since each credential has its own limit.
- Probe interval without a reset time: 30 minutes.
- Folding window: collect for about 30 seconds after the first event, then send one notification.
- A clicked notification opens the plain dashboard URL. If the browser isn't signed in, the existing sign-in behaviour
  applies; no sign-in code goes into a notification.
- Tickets already mid-release under the old per-ticket model when this ships: the builder picks a safe migration
  (for example, let an open per-ticket release PR finish the old way).
- Batch send-back, tracker card moves during a batch, reopened tickets and the usage-limit pause's reach: as written in
  their sections above, marked "builder's call".

## Left to the builder

- Exact data model for batches, pauses and touch records, and how batch gates reuse the existing gate, review and
  merge machinery.
- The exact CLI command names and API routes for releasing, listing batches and resuming a limit pause.
- The exact error texts each CLI uses for usage and rate limits (verified against the pinned Codex version and current
  Claude Code).
- Notification XML, app-name registration details, and the host-level off switch's form, consistent with existing host
  settings.
- Dashboard layout details within the strip-plus-page and banner choices above.
