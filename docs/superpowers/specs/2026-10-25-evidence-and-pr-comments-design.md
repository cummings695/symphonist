# Evidence on high-risk PRs and the PR comment loop — design

Date: 2026-10-25
Status: draft, awaiting approval

Plan 5 of the roadmap (`docs/superpowers/plans/2026-10-21-symphonist-walking-skeleton.md`), features 3 and 4 of the
original design (`2026-10-21-symphonist-design.md`, section 8). "Save the lesson" on a send-back, also listed under
plan 5, is **tabled** until there are real send-backs to learn from (see `notes/feature-backlog.md`).

## Intent

Cut the time the developer spends at the implement gate.

- **Evidence.** Before a high-risk PR waits on the developer, a fresh agent checks the change the way the developer
  does today: it analyses what the change could regress (especially areas it was not meant to touch), checks that the
  affected areas are well tested, and runs the app through its major features end to end. It reports what it found on
  the PR and on the dashboard card, so the developer starts from evidence instead of from nothing.
- **PR comment loop.** The developer reviews the PR where they normally do (Bitbucket or GitHub). A review comment
  tagged `@claude` gets a fix run that pushes changes to the same PR and replies to the comment. The developer does not
  have to touch the ticket.

Symphonist is generic: nothing here may assume a particular project, stack or app. Each project's way of running its
app and its major features comes from project config, proposed at onboarding, with the agent filling in what is missing.

## Goals

- An evidence run happens before the developer decides a high-risk implement gate. It reports three parts:
  1. **Regression analysis.** What the diff touches beyond what the ticket meant to change (shared code, callers,
     config, schema, public contracts), and for each area why it is or is not at risk.
  2. **Test coverage of affected areas.** Each changed or affected area mapped to the tests that cover it, gaps named,
     plus the full test suite result.
  3. **App walkthrough.** The app started and its major features walked end to end, each step's result reported, with
     screenshots where the app has a UI.
- Where "how to run the app" and "major features" come from combines three sources:
  - onboarding proposes them, the developer confirms or edits, and they are saved in project config;
  - every evidence run walks the configured features and also adds walkthroughs relevant to the diff, labelled
    "added for this change";
  - with no config, the agent works it out from the repo and flags it plainly as guessed, offering its guess for the
    developer to save.
- A PR comment starting with `@claude` (same rule as the tracker) on an open implement PR reruns implement with the
  comment(s) as feedback, pushes to the same branch, and replies to each comment.
- By default only the developer's own tagged comments trigger a run; a per-project setting allows anyone's.
- Works with Bitbucket Cloud and GitHub repos alike.
- Existing project files keep loading unchanged; the new config sections are optional.

## Non-goals

- "Save the lesson" (tabled).
- Evidence or PR comment handling on the **release** PR (main → production). Implement PRs only for now.
- Remote review environments (e.g. throwaway Azure deployments). Evidence is local, inside the agent sandbox.
- Starting extra services next to the app (no Docker-in-Docker, no compose). The app must run with what the agent
  image and project config provide; otherwise the walkthrough reports that it could not run.
- Uploading screenshots to the PR host. Screenshots are stored locally and shown on the dashboard.
- Webhooks. PR comments are found by polling, like tracker comments.
- Resolving PR comment threads, or reacting to PR approvals / "changes requested" states.

## Anti-goals

- Evidence that approves, or blocks, anything. It only informs the developer's decision.
- The agent that wrote the code grading its own work: evidence is a separate, fresh run.
- Evidence that hides bad news: a failed walkthrough step, a failing test, or "could not start the app" goes at the top
  of the report, never buried.
- Pretending a guess is confirmed: guessed run instructions or features are always labelled as guessed.
- A PR comment loop anyone can steer by default. Teammates' comments direct an agent that pushes under the developer's
  account, so that is opt-in per project.
- Retry storms: one evidence run per gate round; each PR comment triggers at most one fix run.
- Evidence changing the branch: it never commits or pushes.

## Constraints (from the current code)

- The implement gate is a round keyed by `Ticket.WaitingSince`. `PipelineEngine.StageSucceeded` opens it and sets
  `ReviewDue` when `ProjectConfig.Reviews(stage)`; `Orchestrator.AdvanceAsync` runs `GateReviewer` for a round with
  `ReviewDue`, and `CompleteReviewAsync` applies the verdict only to the same round (`PipelineEngine.ReviewCompleted`).
  Evidence follows the same pattern.
- `GateReviewer` already pins the reviewed change to commits (main...pushed ticket branch), lists changed files and
  forces a high verdict when a risk-area path is touched (`RiskAreaMatcher`). The evidence trigger and the evidence
  run's diff reuse that.
- The reviewer runs read-only (no Bash). Evidence cannot: it builds, runs tests and starts the app. It runs like the
  onboarding container check (`ContainerCheck`, `container-check.md`): in a throwaway clone of the pushed commit, with
  the project's `agent` settings (isolation, image, `allowedDomains`, packages), never committing or pushing.
- The default agent image already has the .NET SDK, Node and Playwright Chromium (`Containers/agent/Dockerfile`), so
  web UIs can be screenshotted without extra setup.
- Tracker `@claude` handling lives in `Orchestrator.HandleClaudeCommentsAsync`: only `IsMine`, unsigned comments newer
  than the later of `ClaudeCommentsHandledAt` and `StageStartedAt`, while the ticket is `AwaitingApproval` or
  `Blocked`, sent back with `PipelineEngine.SendBack` to the same stage. The PR loop mirrors it.
- `ISourceControl` has no PR comment methods yet; both `BitbucketSourceControl` and `GitHubSourceControl` need them.
- Symphonist's own comments are signed (`TrackerComments.Sign` / `IsSymphonist`); the same footer applies on the PR.
- Agent-, PR- and repo-derived text is untrusted and goes into prompts fenced (`UntrustedFence`); the dashboard renders
  it as text.
- `MaxConcurrentAgentRuns` limits agent runs; evidence counts as one.
- Config is YAML loaded by `ProjectConfigLoader`, written back by `ProjectYamlWriter` (onboarding setup screen).

## Approaches considered

**Evidence**

1. **Pre-gate run modelled on the reviewer — chosen.** An `EvidenceDue` flag next to `ReviewDue`; an `EvidenceRunner`
   next to `GateReviewer`; result applied to the same round only. No pipeline config changes; existing projects get it.
   Cost: one more thing the gate round tracks.
2. New `evidence` stage kind between implement and build. Rejected: the PR gate is on implement, so evidence would run
   after approval unless every pipeline moved its gate, breaking existing configs.
3. Fold it into the implement prompt. Rejected: the author grades its own work, which defeats the regression check.

**PR comment loop**

1. **Reuse send-back, with its own prompt — chosen.** Poll PR comments in the tick like tracker comments; send
   implement back to itself with the comments as feedback, rendered with a dedicated `pr-comments` prompt whose output
   carries one reply per comment. Rounds, retries, review and evidence re-runs all work as they do today.
2. Separate fix run outside the stage machine. Rejected: the gate would show code nobody judged, and retries and
   guardrails would be built twice.

**Onboarding:** the existing proposer gains an `evidence` section next to risk areas. One sensible way.

## Design

### Configuration

New optional sections (camelCase like the rest):

```yaml
evidence:
  mode: high-risk            # off | high-risk (default) | always
  run:                       # how to run the app; all optional
    setup: ["dotnet build"]  # commands run once before starting the app
    start: "dotnet run --project src/Web"
    url: "http://localhost:5000"   # where the app answers once started (web apps)
    ready: "/health"         # optional path or log line meaning "started"
    notes: "Uses SQLite in a temp folder; no external services."
  features:                  # major features to walk end to end
    - name: "Create and complete an order"
      steps: "Create a customer, create an order for them, move it through every status to Completed."
  # agent: { timeoutMinutes: 30, model: "" }   # overrides; inherits the rest from agent

prComments:
  fromOthers: false          # default false: only your own @claude PR comments trigger a fix run
```

- `evidence.mode`: `off` never runs evidence; `high-risk` runs it when the round is high risk (below); `always` runs it
  for every implement round that waits on the developer.
- `run` and `features` are trusted (the developer wrote or confirmed them) but every field is optional.
- Builder's calls are marked "builder's call" in this document; the developer can overrule any of them.
- `evidence.agent` overrides `timeoutMinutes` (default 30) and `model`; isolation, image, `allowedDomains`, packages and
  host-mode `allowedTools` come from `agent`. Host isolation needs Bash in `agent.allowedTools` for evidence to build or
  run anything; without it evidence reports that it could not run commands.
- Loader validation follows existing rules (unknown keys and bad values are load errors with clear messages).

### When evidence runs

- Implement stage only, gate `human` or `auto`, `evidence.mode` not `off`, a PR exists.
- A round is **high risk** when the reviewer's verdict for the round is high, or (with review off) the change between
  main and the pushed ticket branch touches a risk-area path. Computing the path match does not need an agent.
- Order within a round:
  1. Review first, when one is owed. A low verdict that auto-approves ends the round; no evidence.
  2. Then evidence, when due. With `always`, every round that still waits on the developer.
- While evidence is due or running, the card shows "Gathering evidence" and still counts as waiting on the developer.
  They may decide at any time. Deciding the round (approve, send back, block, a PR comment fix) **cancels** a running
  evidence run (builder's call: otherwise the merge or rerun would wait up to the evidence timeout, since a ticket has
  one in-flight task); the throwaway clone and partial screenshots are cleaned up and an event records the cancel.
  A result that still arrives for a decided round is recorded as an event and not posted.
- One evidence run per round, and at most one per pushed head commit (builder's call): evidence results are keyed by
  round and by commit. A new round whose head was already evidenced (e.g. a fix run that changed nothing, or a merge
  hold with no new commits) reuses that result on the card and posts nothing new. A send-back or fix run with new
  commits gets fresh evidence if still high risk. The reviewer's own behaviour on reruns is unchanged.
- Evidence failure (agent error, timeout, invalid output) never blocks the ticket: it is reported as "Evidence could
  not be gathered: <reason>" in the same places and does not retry within the round.
- Restart: like `ReviewDue`, an `EvidenceDue` round interrupted by shutdown runs again on the next start.

### The evidence run

- Workspace: a throwaway clone at the pushed ticket branch head (the commit the round is about), pinned; recorded with
  the result. Changes made during the run are discarded.
- Prompt `evidence.md` (embedded default, overridable like other prompts). It receives: ticket title, description and
  link; the diff range, changed files and patch (fenced, untrusted, same limits as the reviewer); the risk areas; the
  configured `run` and `features` (trusted). The container check's commands are not stored today and are not an
  input (builder's call); the agent finds build and test commands itself, as the container check does.
- The agent must:
  1. Analyse regressions: which areas outside the ticket's intent the diff can affect, and why each is or is not at
     risk.
  2. Build and run the full test suite; map affected areas to the tests covering them; name gaps. It may write
     throwaway tests to probe a gap but they are not kept.
  3. Start the app using `run` (or its own finding when absent), walk every configured feature, then add features
     relevant to the diff; capture screenshots for UI steps into a known output folder.
- Structured output (schema owned by Symphonist):
  - `headline`: one to three short lines of the most important findings, bad news first.
  - `regressions[]`: `area`, `intended` (bool), `risk` (`low`/`high`), `reason`.
  - `coverage[]`: `area`, `tests[]`, `gap` (text or empty).
  - `tests`: `command`, `passed`, `summary` (counts).
  - `walkthrough`: `ran` (bool), `why_not` (when not), `run_source` (`config` / `guessed`), and `features[]`: `name`,
    `source` (`config` / `added` / `guessed`), `steps[]` (`action`, `result`, `passed`, `screenshot` file name).
  - `proposed_config`: `run` and `features` the agent used, when any part was guessed.
- Screenshots: copied out of the workspace into Symphonist's data folder per ticket and round, size- and
  count-limited, only image files; referenced by name. Other files are not copied out.

### Where evidence shows up

- **PR comment** (signed): headline, then the three parts as short sections, with failures first in each, and a line
  "Screenshots: on the Symphonist dashboard" when there are any. Guessed run instructions or features are flagged at
  the top.
- **Tracker** (signed): one line, "Evidence posted on the PR", with the headline.
- **Dashboard card detail:** an Evidence section per round: the same content, screenshots inline, states in words
  ("Passed", "Failed", "Not run", "Guessed"). When `proposed_config` is present, a "Use for this project" action opens
  the project setup screen with the evidence fields filled from it for the developer to edit and save.
- **Board card:** "Gathering evidence" while it runs; a small "Evidence: issues found" marker when the headline reports
  a failure or high-risk regression.
- Evidence results are stored (a table per round, like gate reviews) so history survives restarts.

### Onboarding

- The onboarding proposer also proposes `evidence.run` and `evidence.features` from the repo, with a reason each, under
  the existing limits style (a handful of features, bounded text).
- The setup screen shows them next to risk areas, editable; saving writes them with `ProjectYamlWriter`.
- The container check is unchanged.

### PR comment loop

- `ISourceControl` gains:
  - list comments on a PR: id, author (whether it is the token's user), body, created time, file path and line for
    inline comments, parent id for replies;
  - reply to a comment (threaded where the host supports it; GitHub top-level PR comments get a new top-level comment
    quoting the original).
  Bitbucket: PR comments endpoint (inline comments carry path/line, replies use `parent`). GitHub: both issue comments
  and pull review comments (review comments reply via the replies endpoint). Fork PRs stay ignored as today.
- Each tick, for tickets at the implement stage with an open PR, in `AwaitingApproval` or `Blocked` (the same states as
  the tracker loop): fetch PR comments newer than the later of `PrCommentsHandledAt` and `StageStartedAt`; keep unsigned ones
  starting with `@claude` from the developer (or anyone, with `prComments.fromOthers`).
  Comments posted while the stage is pending or running wait for the gate, exactly as on the tracker.
- After approval (`Approved`, merging) comments wait too (builder's call: the host may already be merging). If the
  merge is held, the round reopens and they apply then. If the PR merges, each waiting tagged comment gets one signed
  reply: "Merged before this could be addressed; open a follow-up ticket if it still needs a change." Nothing reruns.
- All such comments found in one tick go into one send-back of implement to itself (`PipelineEngine.SendBack`), with
  `PrCommentsHandledAt` set to the newest. The feedback records that it came from PR comments and carries, per
  comment: id, author, body (marker removed), path/line and the surrounding code for inline comments.
- The rerun uses prompt `pr-comments.md` instead of `implement.md` (fenced comments; for others' comments the prompt
  says so). Output: the usual stage output plus `replies[]` (`comment_id`, `reply`). After commit and push, Symphonist
  posts each reply (signed) to its comment, and the usual "PR updated" tracker comment. A comment without a reply gets
  "Addressed in <commit>" by default; a run that made no changes still replies (the agent's reason) and does not fail
  just for having no diff.
- The new gate round then gets its review and evidence per config, as for any rerun.
- Gate decision (builder's call): a fix run from the developer's own PR comments records the round as sent back, the
  same as a tracker `@claude` comment does today, so it counts in reviewer agreement. One triggered only by others'
  comments (`fromOthers`) records no developer decision, so teammates' nits do not skew the reviewer's numbers; the
  round still ends and the event names who asked.
- Tracker `@claude` and PR `@claude` comments found in the same tick are combined into one run (tracker feedback plus
  PR comments), so the two never race.
- Failure: a failed fix run follows the stage's retry rules; replies are posted only after a successful push.

## Left to the builder

- Persistence schema (evidence table, new ticket fields `EvidenceDue`, `PrCommentsHandledAt`, feedback source).
- Exact prompt wording for `evidence.md` and `pr-comments.md`, and the exact output schemas, within the fields above.
- Screenshot limits (count, size), the output folder convention, and how the dashboard serves them (sign-in required,
  same-origin, as for other dashboard content).
- Size limits on comment bodies, code context and the evidence report; PR comment formatting per host (Markdown both).
- How the onboarding proposal schema is extended, within the existing proposal/parse pattern.
- CLI: whether `symphonist show` prints the latest evidence headline.
