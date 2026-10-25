# Onboarding (plan 4): deferred items

Date: 2026-10-25
Source: per-task and final reviews of `docs/superpowers/plans/2026-10-25-symphonist-onboarding.md`.

Review findings not fixed in plan 4.

## Worth doing soon

- **Live tests touch the real OS credential store.** The `Category=Live` secret-store test writes a temporary `symphonist-test-<guid>` entry and removes it on every unfiltered full-suite run. Make Live tests opt-in, for example with an environment variable.
- **The file secret store can lose a write.** `SYMPHONIST_SECRET_STORE=file` locks only within one process, so a host and a CLI `secret set` writing at the same moment can lose one secret.
- **Block scalars work only in descriptions and `jql`.** A hand-written `name: >` (or any other single-line field written as a block) still blocks checks and saves for that file.
- **The CLI can unblock a removed-stage ticket.** `symphonist unblock` sends it to Pending and the orchestrator blocks it again. The dashboard hides Unblock in this case.
- **Two browser tabs on the same project share one setup form.** This was accepted for a single-user host.
- **A failed container-check image step disables that project's image** until a later pass or a restart. This matches startup behaviour; the README should mention it.

## From the per-task reviews

- **Discovery:**
  - Token scopes aren't verified.
  - The GitHub ticket count is approximate and its paging has no upper bound.
  - Actions workflows are read only from the first 100.
  - Pipelines discovery doesn't check the production-branch trigger.
  - The Jira `projectKey` isn't checked against the JQL.
  - `GitWorkspace.RemoveAsync` follows symlinks in `SetAttributes`.
  - Every failure of `remote get-url` reads as "not a checkout".
  - The doc comment on the discovery exceptions is wrong.
  - The Bitbucket branch-with-slash escaping is untested.
- **YAML writer:**
  - Copying unmodelled entries keeps whitespace-only lines raw, which can change a block scalar indented 5 or more columns.
  - Comments above unmodelled keys are dropped.
  - `|+` and `>+` lose their trailing blank lines.
  - Unmodelled keys that aren't strings are dropped.
  - Rewritten sections normalise paths and add a trailing slash to the Jira site.
- **Form:**
  - `ChangeRepoType` can leave an incompatible build in place.
  - The number check accepts `" 7"` and `"+7"`.
  - Field-binding tests don't compare list values.
- **Reload and save:**
  - The new file's name relies on the loader's id regex; assert that `config.Id` equals `edited.Id`.
  - `ExistingFile` isn't checked for a `.yaml` extension or for ownership.
  - The registry's fields are swapped separately, not in one step.
  - `ProjectsChanged` subscribers run synchronously.
  - The tick request is inconsistent.
  - The id collision check ignores `.yml` files and `LoadErrors`.
  - There's no write-failure test.
- **Proposal:**
  - U+2028 in a proposed description blocks the save (with a clear message).
  - The duplicate-status note is misleading.
  - Several stages can share entry statuses after Apply.
  - Status names over 100 characters can't match.
  - Several wrong-kind branches of the parser are untested.
- **Container check and setup session:**
  - Exceptions other than cancellation still propagate after a cancel.
  - Raw log sizes are uncapped.
  - `ApplyPendingAdoption` relies on Dictionary key order.
  - `Connections` is set before `CheckedTracker`.
  - `SetSecret` doesn't check the caller's session.
  - There's no test for a `tokenSecret` change.
- **Setup screen:**
  - A typed secret value stays in circuit memory until Save secret; clear it on dispose.
  - Default secret names refresh on change, not as you type.
  - There's no test for the catch-all action path.
  - Ticking Accept doesn't count as activity.
  - The credential store is read on every progress event.
