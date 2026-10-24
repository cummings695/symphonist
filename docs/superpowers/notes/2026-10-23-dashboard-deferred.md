# Dashboard: deferred items

Date: 2026-10-23
Source: per-task and final reviews of plan 2 (`docs/superpowers/plans/2026-10-22-symphonist-dashboard.md`).

Review findings judged minor or deferred, and **not** fixed in plan 2. Items fixed in the final fix wave are omitted.

## Worth doing soon

- **Approve on a re-run gate.** The dashboard's approve checks the stage id the developer saw, not which run of it. If a ticket is sent back to the same gate stage and waits again, a stale card approves the new output. Close with a `WaitingSince` (or version) token.
- **Tracker status never retried.** After "tracker sync skipped" or "tracker transition … failed", the Jira status is not retried once the cause is fixed (`LastSourceStatus` stays old). Pre-existing plan-1 behaviour; the card stays in its old Jira status until moved by hand.
- **Pause write failure.** `ProjectRegistry.SetPaused` changes the in-memory set before writing `paused.json`; a failed write leaves the board and file disagreeing until restart (the board now shows the error).
- **Disconnect beacon gets 400.** Closed tabs keep their circuit (and a notifier subscription) until the retention timeout (~3 min). Harmless for one user; find the cause.

## Security (accepted residual risk)

- The `symphonist.dashboard` cookie is not port-scoped: every `127.0.0.1` port the browser visits receives it. Accepted for cookie auth over plain-http loopback; revisit with a server-side, revocable session list (e.g. `symphonist dashboard sign-out`).
- The `/_blazor/initializers` exemption (same-origin GET without `Origin`) could additionally require `Sec-Fetch-Site` absent or `same-origin`.

## UI

- Status line shows a refresh error in place of a command outcome; command messages never expire.
- ~~If the first board load fails, the header (and "Poll now") is hidden.~~ Fixed by the dashboard redesign: the title bar (with "Poll now") and the page heading are always shown.
- After a "rejected" reconnect the page reloads immediately, so its text only flashes. (The "failed"-state Reload button was checked in a browser on 2026-10-24: it reloads into the restarted host, still signed in.)
- No `#blazor-error-ui` element, so a dead circuit shows nothing.
- A missing ticket briefly shows the generic not-found page before the per-ticket message.
- The detail page's "before" snapshot comes from the last loaded model, so the "…but a follow-up step failed" wording can be wrong if the ticket moved elsewhere before the click (Board has the same limit).
- Times show in the host's local time zone (fine while the host is always local).
- One unreproduced 500 for `blazor.web.js` right after a build while the host was starting.

## Code

- `BoardQuery`: the Done column rebuilds a `(ProjectId, Key)` dictionary per call (carry `UpdatedAt` with the card); `FindIndex` is recomputed inline.
- `TicketLinks.Issue` drops a path on the Jira site if it has no trailing slash.
- ~~`TicketCard.Meta()` is enumerated twice per render.~~ Fixed by the dashboard redesign (the card no longer has `Meta()`).
- `Orchestrator`: setting and clearing a problem are written differently; `ProblemsChanged` is raised inside the tick lock, so a future subscriber that throws would abort a tick.
- `OrchestratorService` swallows exceptions during shutdown without logging.
- `BoardNotifier`'s timer is never disposed (it is a singleton).

## Tests

- Untested: the 30 s re-render, elapsed formatting, "+N older", 140-char truncation, cost format, in-flight button disabling, the KeyNotFound path, the "send back failed" branch.
- Test gaps: non-Jira `Issue` and `Source`, filtered Done column, unknown-column ordering, `HiddenCount` of 0 for non-Done columns.
- `Poll_requests_are_coalesced` asserts 1–4 queries because two projects share one fake tracker.
- `FakeTicketStore`: `AgentRunsAsync` relies on stable sort instead of `Id`; `FindAsync` takes its lock twice.
- Unauthenticated `/_blazor/negotiate` is asserted as 302 or 401.
- Several tasks did not capture a RED run.
