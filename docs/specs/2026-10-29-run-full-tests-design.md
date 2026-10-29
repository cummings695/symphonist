# symphonist-1: Run the full test suite and get it green

Ticket: https://github.com/cummings695/symphonist/issues/1 (no description; interpreted from the triage comment).

## Goal
`dotnet test Symphonist.slnx` passes on Linux, Windows and macOS, using the same filter CI uses (`Category!=Docker&Category!=Live`).

## Current state (from triage, not yet re-verified)
821 passed, 76 failed, 1 skipped on net10.0 / WSL.

1. **Symphonist.Host.Tests (~75 failures)**: `TicketsApiTests`, `DashboardAuthTests`, `EvidenceFilesTests`. Every failure throws `ObjectDisposedException` from `WebApplicationFactory.CreateClient/StartServer`. Root cause is unknown.
2. **Symphonist.Core.Tests (1 failure)**: `ProjectYamlWriterTests.Update_rewrites_only_changed_sections` asserts `"  - name: auth\n"`.

## Findings from reading the code
- **Core.Tests failure is a test bug.** The test reads `samples/projects/orders.example.yaml` without normalizing line endings (`ProjectYamlWriterTests.cs:278`). The sample files in this working tree contain CRLF (133 lines in `orders.example.yaml`), and `.gitattributes` only pins `*.sh` and `Dockerfile` to LF. `ProjectYamlDocument` deliberately detects CRLF input and emits CRLF (`ProjectYamlDocument.cs:36`, `WithNewLines`), which is correct behavior for editing a user's file. The sibling test at line 207 already calls `.ReplaceLineEndings("\n")`. Only the checkout's line endings differ.
- **Host.Tests: three test classes each build their own `WebApplicationFactory<Program>`** and dispose it in `Dispose()`. All are in the non-parallel `SymphonistHomeCollection`. The `Program` host is built with process-global state: the `SYMPHONIST_HOME` env var, set and cleared by `TestHome`. Hypotheses to test, in order:
  - a. The host or a hosted service reads `SYMPHONIST_HOME` lazily, so a later test's `TestHome.Dispose` (clearing the variable or deleting the directory) breaks a still-live host.
  - b. A static or singleton in `Program` (for example a shared service provider or `Console`/`Host` lifetime) is disposed with the first factory and reused.
  - c. Tests in other collections (bUnit/component tests that also touch `TestHome`) run in parallel with the collection and clear the env var.
  The triage reports that the first failure cascades. Reproduce first, then pick the fix.

## Scope
In: fix both failures at the root cause, verify CI parity, keep warnings-as-errors clean.
Out: new features, refactoring unrelated tests, changing the writer's CRLF-preserving behavior.

## Decisions
- Treat the CRLF output as correct writer behavior and fix the test (normalize the input with `ReplaceLineEndings("\n")`, matching line 207). Optionally add `*.yaml text eol=lf` for `samples/` to `.gitattributes` so checkouts are deterministic. A further optional test would assert that CRLF input yields CRLF output.
- Fix the Host.Tests problem in the fixture or host code, never by skipping or serializing away individual tests.

## Open questions
- Is the ticket "run and report" or "run and fix"? Assumed fix, as the triage recommends.
- Do the failures reproduce in CI (ubuntu, windows, macos)? Unknown. Check the latest CI run for the default branch.
- Is the Host.Tests cause really in the fixtures, or is it in `Program` (production code)? Decide after reproduction.

## Risk areas
None are configured for this project, so the change touches none.

## Acceptance criteria
- `dotnet test Symphonist.slnx --filter "Category!=Docker&Category!=Live"` reports 0 failures on Linux.
- Each Host.Tests class passes in isolation and in the full run.
- The Core.Tests test passes with both LF and CRLF checkouts of the samples.
