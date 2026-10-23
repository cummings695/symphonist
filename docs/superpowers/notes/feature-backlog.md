# Feature backlog

Requested features not yet assigned to a plan.

## `@claude` comment processing (requested 2026-10-23)

While a ticket waits at a gate (`AwaitingApproval`), a new tracker comment from the developer that starts with `@claude` sends the ticket back to rerun that stage, with the comment (marker stripped) as `{feedback}`. Example: triage posts open questions; the developer answers in a Jira comment beginning `@claude`; triage reruns with the answers and posts a new triage comment.

- Only the developer's own comments (`IsMine`), never Symphonist-signed ones, and only comments newer than `WaitingSince`.
- Comments without the marker stay inert, so notes to colleagues never trigger a rerun.
- Each comment triggers at most one rerun (remember the last processed comment id/timestamp per ticket).
- Works for every gate, including triage (which today has no Jira-only way to rerun: it has no `onEnterStatus` and no earlier stage).
- Approval still follows the gate's policy; deciding "the answers are sufficient, move on" automatically belongs with the risk reviewer (plan 3).
