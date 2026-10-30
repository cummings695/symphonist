# Evidence and PR comment loop (plan 5): deferred items

Date: 2026-10-25
Source: per-task and final reviews of plan 5 (`docs/superpowers/plans/2026-10-25-evidence-and-pr-comments.md`).

Review findings judged minor and **not** fixed in plan 5. The final review triaged every item below as fine to defer.

## Evidence

- Repeated agent runs while the PR host is down: dedupe only counts posted evidence, so a head whose PR comment keeps failing is gathered again each round.
- Headline markdown (headings, images, links) in agent text is not neutralised in the PR comment. Image URLs in PR comments can bypass `allowedDomains`; the same class exists for PR descriptions and `comment_markdown`.
- `EvidenceComment.Format` cuts headline lines at 200 chars while the parser keeps 500.
- Failing walkthrough steps are listed before passing ones even when nothing is truncated, so step order is not strictly chronological.
- `EvidenceRecord.Report` re-parses JSON on each access (once per record per render).
- `ITicketStore.EvidenceIssuesAsync` loads every evidence row for every board load.
- Spec and plan paths in the evidence prompt are fenced but not `IsSafeRepoPath`-checked.
- Screenshot copy and serve check for links before opening (check-then-open); matters only if an agent process outlives the run (host mode) or something else writes the data folder.
- Screenshot names are matched case-sensitively (a mismatched reference just drops the screenshot).
- `evidence.agent.timeoutMinutes` is not validated > 0 (same as `review.agent`).
- If review is switched on in the middle of a round that opened with review off and no risk areas, that round owes no evidence.
- Evidence and screenshot files are not deleted when a ticket is forgotten (logs aren't either).

## PR comment loop

- A PR listing error in the same tick as a tracker `@claude` comment loses the unread PR comments (an event is shown).
- Clock skew, or second-precision host timestamps, can drop a comment posted right after a send-back.
- Merged-first replies are capped at 20 while `PrCommentsHandledAt` moves past all of them.
- Merged-first replies are posted before the merge commit is saved; a crash in between repeats them.
- Only `Ticket.PullRequestId` is checked, not that the PR is still open (same for tracker `@claude` at implement).
- One PR comment listing call per gated implement ticket per tick.
- `@claude` escaping also hits longer tokens (`@claudette`, `x@claude.ai`).
- The ticket detail page does not show the PR comments a fix run is addressing.
- "PR updated" and "Addressed in <head>" are posted even when the fix run changed nothing.

## Setup and config

- A hand-written tab in `evidence.features[].steps` or `run.notes` loads, but a later screen edit refuses to save until it is removed.
- Duplicate feature names are not flagged by the loader.
- The per-feature proposal reason is dropped by the parser (only the proposal-level reason is shown).
- `?evidenceFrom=` prefill only runs on first open of the setup page, and its error shows the bounded exception message.

## Tests

- Only the approve path is tested for cancelling evidence directly (the PR comment path is also covered).
- No orchestrator test for evidence after a PR fix run (new head → new evidence).
- Push-before-reply ordering, the 7-char head cut and `ReadCodeContext` link/huge-line cases are untested.
- `ProjectYamlWriterTests.Update_rewrites_only_changed_sections` fails in a fresh `core.autocrlf=true` checkout (CRLF sample); predates plan 5.
- `OrchestratorTests.Claude_comment_at_gate_reruns_stage_with_feedback` fails when run alone (asserts before the yielded run starts); passes in the full suite; predates plan 5.
- Evidence limits tests lack setup-count, notes and steps-length cases; screenshot route tests lack `CON.png`, trailing-dot and `C:` cases.
