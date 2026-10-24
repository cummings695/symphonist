# Risk reviewer (plan 3): deferred items

Date: 2026-10-24
Source: per-task and final reviews of `docs/superpowers/plans/2026-10-24-symphonist-risk-reviewer.md`.

Review findings not fixed in plan 3. The first section matters only once a project sets `review.mode: active`; reviews are off by default.

## Fix before enabling `mode: active`

- **`.claude` committed as a symlink bypasses the agent-config guard.** `GateReviewer.IsAgentConfiguration` matches `.claude` only as a parent directory. A PR that adds a symlink named `.claude` pointing at `tools/cfg/`, plus `tools/cfg/settings.json`, is not flagged. Fix: also match when the last path segment is `.claude` (case-insensitive), and force high on any added/changed symlink (mode `120000`).
- **A committed `CLAUDE.md` symlink to an unguarded file** lets later edits to the target steer agent runs. Needs symlink-target awareness.
- **Check-then-merge gap.** Bitbucket's merge call takes no expected source commit, so a push in the moment between the last PR read and the merge call could be merged.

## Security, broader than the reviewer

- **Writing (non-review) agent runs in host mode still load the workspace's project settings:** `.claude/settings.json` hooks, `CLAUDE.md`, `.mcp.json`. Only read-only runs get `--setting-sources user --strict-mcp-config`. Consider the same for all host runs.
- **Host-mode implement agents can rewrite `.git/config`** (remote URL or refspec). This is a host-mode trust issue that predates plan 3.
- **The release reviewer reads files from the ticket-branch tree, not main.**
- **An overridden review prompt can omit `{{review.fence_id}}`** and still pass validation.
- **Path validation gaps:** `IsSafeRepoPath` does not reject U+2028/U+2029 or bidi controls, and `review.history` puts the merge commit in unfenced.
- **`IsValidBranchName` accepts `HEAD`.** This fails safe for implement.
- **Branch-name validation exists only in the reviewer,** not on the implement/push path.

## Correctness and robustness

- **`PullRequestInfo.WouldMerge` accepts the reverse prefix direction.** Safe today because approved heads are full SHAs; require `commit.Length == 40`, or a single direction with a 12-character floor.
- **The Bitbucket source-repository filter (`q`) is case-sensitive.** A mixed-case `workspace`/`slug` in config finds no PR and opens duplicates. Lowercase the full name in the query.
- **The third-merge-hold message says "keeps moving"** even when holds were caused by PR checks (fork/unknown source, lookup failure).
- **A second auto-approval comment is possible** if save/apply throws after the comment posts (rare).
- **A persistent failure while storing a review result reruns the review every tick** (one event, repeated cost).
- **A stalled host merge alternates two events every 10 minutes.** Human-approved 202 merges also wait 10 minutes; this is untested.
- **A release PR that reads "open" for more than 10 minutes after it landed gets held** (fails safe).
- **Shadow/human-gate verdicts don't record the reviewed SHA,** so a "Low risk" verdict may describe an older head.
- **The run record is lost if writing the log throws.**
- **The diff is read fully into memory before truncation.** The changed-file list and ticket comments are not capped.
- **Gate-review and agreement queries load all rows in memory.** There is no uniqueness constraint on review/decision rows.
- **`Block` clearing `WaitingSince` changes the `LatestFeedback` baseline** for a ticket blocked at a gate and then sent back.
- **A second verdict for the same round overwrites `Risk`** and records another event.
- **The board's `StillWaiting` keys off `WaitingOnYou`,** which gives wrong wording in a narrow send-back-to-auto-gate case.
- **`review.agent.allowedTools: []` silently falls back to Read/Grep/Glob,** and `AllowedDomains` is validated twice.
- **`plan.md` drops the multi-line risk-areas list mid-sentence.**
- **`StatusTable` splits a rendered table on newlines.** This is fragile if a cell ever contains one.

## Tests

- **Coverage gaps:**
  - an end-to-end send-back → rewait → stale-verdict sequence;
  - a Docker-gated test that writes to a read-only workspace;
  - the detail page keeping Approve while Reviewing;
  - the human-approved 202 wait.
- **Weak or misleading test inputs:**
  - several tests bundle cases;
  - the `Precise` test value is whole-ms;
  - a test depends on project order.
- **Process:** several tasks did not capture a RED run.
