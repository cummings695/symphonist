# Backlog batch (plan 3a): deferred items

Date: 2026-10-24
Source: per-task and final reviews of `docs/superpowers/plans/2026-10-23-symphonist-backlog-batch.md`.

Review findings judged minor and **not** fixed in plan 3a.

## `@claude` comments

- The baseline compares host time (`StageStartedAt`) with Jira's comment `Created`. With clock skew, a reply posted seconds after a rerun starts can be silently dropped. If Jira runs ahead of the host, a comment already used as status-move feedback can rerun once more. Fix: keep the baseline in tracker time (e.g. the newest comment's `Created` at stage entry).
- A `@claude` comment made while a ticket is `Approved` (between stages) is silently dropped, because the next stage resets the baseline. Record an "ignored" event.
- The marker requires whitespace after `@claude`, so `@claude:` and `@claude,` are ignored. A Jira mention of a user named "Claude …" leaks the surname into the feedback. Accept `:`/`,` as separators.
- A status-move send-back passes the raw comment body, marker included, as feedback.

## Tracker retry

- Failure-event dedupe for retries is in memory, so after a host restart a still-pending retry can add one more failure event.
- Retries never stop (one HTTP call per tick while the card is unmoved). There is no cap.
- A transition that threw but actually applied in Jira (e.g. a response timeout) is read on the next tick as a developer move.
- An agent run keeps the integrations from the tick that started it. If a secret is fixed during a long run, the completion's move fails once and is then retried with the new clients.

## Other

- `forget` followed by re-pickup can collide with the existing workspace branch `symphonist/{key}`.
- Truncated Jira comment threads are re-fetched every tick.
- The credential store is read on every `IntegrationFactory.Create` (per project per tick, per applied command move). Measure if it ever matters.
- Tests:
  - No test of more than one comment page (more than 100 comments).
  - The `forget` re-pickup test leaves a blocked agent run unreleased.
- Phone-width count alignment uses a `1.05rem` magic number mirroring the chevron width (use a CSS variable). Double space in some class lists.
