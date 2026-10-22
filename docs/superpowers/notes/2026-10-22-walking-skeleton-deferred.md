# Walking skeleton: deferred items

Date: 2026-10-22
Source: per-task and final reviews of plan 1 (`docs/superpowers/plans/2026-10-21-symphonist-walking-skeleton.md`).

These are review findings that were judged minor, or deferred to later plans, and were **not** fixed in plan 1. Items fixed in the final fix wave were removed from this list. Grouped by area; within each area, items most likely to affect real use come first.

## Verify during the first live run

- Jira search may truncate `comment.comments` on chatty tickets, so the latest send-back feedback could be missed (`JiraTracker`). Fetch `/issue/{key}/comment` when `total > returned` if this shows up.
- Confirm Jira preserves the "— posted by Symphonist" footer on comments; feedback filtering depends on it.
- Workspaces created before the credential-stripping fix keep any credentialed origin URL (none exist yet; delete `~/.symphonist/work` if in doubt).

## Pipeline and orchestrator

- First implement run: an agent that changed nothing still passes the "made changes" check because the plan commits are ahead of main, producing a docs-only PR. Compare against HEAD recorded at stage start instead of `origin/main`.
- Implement rerun with an existing PR doesn't check whether that PR was declined or merged by hand; it pushes and comments "PR updated", then ends Blocked with a clear error at post-approval.
- An agent stage that is Pending (waiting for a slot) with a deferred send-back still runs in full before the send-back applies (wasted run, correct result).
- The "skip transition when target equals LastSourceStatus" shortcut can go stale if the card moved while tracker queries were failing.
- `Block` keeps a stale `WaitingSince`, so feedback for a ticket blocked while awaiting approval uses an older baseline.
- `PipelineEngine.Create` bypasses `EnterStage`: a ticket whose source status matches a Done-kind stage's entry statuses would sit Pending instead of Done.
- Untested: OnEnterStatus-only send-back branch, closest-wins between several matching earlier stages, Unblock on a non-Blocked ticket, advancing past the last stage, SendBack/Unblock while a run is in flight.
- `Orchestrator.Dispose` doesn't dispose its semaphore/CTS; the FindAsync fallback at pickup is redundant (the lock is what prevents duplicates).
- `PipelineEngine.IndexOf` duplicates `ProjectConfig` stage lookup.

## Sandbox and agent runs

- `ProvidesIsolation` is true for any image, including a custom `agent.image` without the firewall entrypoint. Document that a custom image means "you own the sandbox", or require images built `FROM` the default.
- No `--pids-limit` / `--memory` on agent containers; a runaway agent can exhaust host resources.
- Supply chain: `@anthropic-ai/claude-code` npm install and the base image digest are unpinned; Playwright installs via `npx -y` as root.
- `allowedDomains` permits only TCP 80/443 (Ruling H). Document on `AgentSettings.AllowedDomains` and the `DockerLauncher` summary; custom-port registries and SSH are rejected.
- DNS exfiltration through the allowed resolver remains possible (inherent to allowing DNS); allowed IPs are shared CDN IPs; IPs are resolved once at container start.
- The OAuth token is visible via `docker inspect` while the container exists (inherent to `-e`).
- Base domain list is duplicated in `init-firewall.sh` and `DockerLauncher.cs` with no test keeping them in sync.
- The firewall self-check relies on resolving example.com; probe a fixed IP instead.
- A `CleanupAsync` exception replaces the `OperationCanceledException` in `ClaudeCodeRunner`.
- If `<ws>/.git` is missing, Docker creates an empty `.git` on the host via the read-only mount.
- A host uid of 0 (e.g. a root CI container) fails every run with exit 71; undocumented.
- uid/gid remapping has only been exercised on Windows, not on a Linux/macOS host.
- Leftover-container cleanup at startup removes every `symphonist=agent` container, including another Symphonist instance's (different `SYMPHONIST_HOME`) or concurrently running Docker tests. Add an instance label.
- Host mode on Windows: `ProcessStartInfo` resolves `claude.exe` only, so an npm-installed `claude.cmd` fails. Document the native installer or probe `.cmd`.
- `ClaudeCodeRunner`: non-zero exit with non-JSON stdout and empty stderr loses the exit code; empty `allowedTools` passes `--allowedTools ""`; a comma inside a tool pattern splits it.

## Git workspace and processes

- A relative origin URL in the developer's checkout (e.g. `../remote.git`) is copied verbatim and resolves against the wrong directory.
- `project.Id` / ticket key go straight into `Path.Combine`; values with `..`, an absolute path, or a leading `-` aren't rejected.
- `ProcessRunner`: normal-exit pipe reads are unbounded (a grandchild holding the pipes hangs the call); `Kill` only catches `InvalidOperationException`; a token cancelled before start still launches the process.
- Stray `<ws>.tmp-*` directories from a crash mid-prepare are never cleaned up; a corrupted existing workspace has no recovery path.
- Test gaps: non-ASCII UTF-8 round trip, grandchild kill, git timeout branch. `Norm` in tests lowercases on all OSes. The Sleeper copy hardcodes `bin/$(Configuration)/net10.0`.

## Jira and Bitbucket clients

- A 2xx with a non-JSON body (proxy/login page) throws `JsonException` instead of `HttpRequestException` (both clients). An empty 200 from Bitbucket merge should be treated as success with a null hash (it self-heals next tick).
- Jira: no guard against a repeated `nextPageToken` (infinite loop); a malformed comment `created` NREs; `BaseAddress ??=` drops any path segment on `Site`.
- Bitbucket: branch names aren't escaped inside the `q` filter (`"`); `BranchFileUrl` doesn't escape path segments; Pipelines status looks only at the newest 50 pipelines.
- Coverage gaps: `/myself` retry and concurrent first calls, ADF lists/mentions, Pipelines HTTP errors, blank commit hash.

## Host and API

- Rotated secrets are never re-read until restart, and cached `HttpClient`s bypass the factory's handler rotation (DNS changes never picked up). Include secrets in the cache key or rebuild per tick.
- First image build blocks the API (and host-isolated projects) for up to 45 minutes at startup (documented in README). Consider building in the background and gating only container runs.
- Every `KeyNotFoundException` maps to 404; use a dedicated not-found exception.
- `/api/poll` fires an uncoalesced `Task.Run` per request (serialized by the gate, but queues up).
- `TicketDetailDto` repeats all `TicketDto` fields.
- No regression tests for `/API/...` casing, HEAD/OPTIONS on `/api`, or a Host header outside `AllowedHosts`.
- `RemoveAllResilienceHandlers` is an experimental API (warning suppressed locally).
- The Unix-only token-file permission test has run in a Linux container but not in CI yet.

## Config and prompts

- The strict placeholder regex lets typos such as `{{Ticket.Key}}` or `{{ ticket.key }}` pass validation and stay literal in the prompt.
- `PromptLibrary` doesn't catch `ArgumentException`/`NotSupportedException` for a bad override path.
- `build.type` is required but not restricted to a supported value.
- `ProjectConfig.NextStage` allocates a list per call.
- Sample config: implement and release gates have no `approvedWhenStatus`, so those gates are CLI-only in the sample; the sample test asserts little.
- Test gaps: missing required field, `Stage("unknown")`, `.yml` extension, exact `localPath` resolution; prompt tests depend on `orders.yaml`.

## CLI

- The interactive no-echo `secret set` path (`ReadHidden`) is untested and has no Ctrl+C/EOF handling.
- The `--to` help text ("default: the stage before approval") isn't verified against the orchestrator.
- The "Option '-m' is required" test depends on alias order.

## Storage

- Two concurrent inserts with the same (ProjectId, Key) and different Ids throw `DbUpdateException` (prevented today by the orchestrator lock).
- `SaveAsync` is last-writer-wins on every column; events and agent runs have no foreign key to tickets.

## Repo hygiene

- `TestResults/` is not in `.gitignore`.
- `ci.yml` has no `permissions: contents: read` block.
- Tests that set the process-wide `SYMPHONIST_HOME` must stay in the shared xunit collection.
- Several tasks skipped a captured RED step (tests and code written together); BOM churn in a few csproj files.

## Open decision

- Devlooped.CredentialManager's OSMF EULA charges revenue-generating users a fee. Keep it, or replace `CredentialManagerSecretStore` with per-OS implementations behind `ISecretStore`.
