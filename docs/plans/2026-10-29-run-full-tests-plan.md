# symphonist-1 implementation plan: get the full test suite green

Spec: `docs/specs/2026-10-29-run-full-tests-design.md`. Risk areas touched: none (none configured).

1. **Baseline.** Run `dotnet test Symphonist.slnx --filter "Category!=Docker&Category!=Live"` and save the failing test names. Check the latest CI run on `development` for the same failures.
2. **Core.Tests fix** (small, independent).
   - In `tests/Symphonist.Core.Tests/Onboarding/ProjectYamlWriterTests.cs:278`, change to `File.ReadAllText(...).ReplaceLineEndings("\n")`, as at line 207.
   - Add `samples/**/*.yaml text eol=lf` to `.gitattributes`, so CRLF checkouts stop varying by machine.
   - Optional: add a test that CRLF input produces CRLF output and still contains `"  - name: auth\r\n"`.
   - Run `dotnet test tests/Symphonist.Core.Tests`.
3. **Host.Tests diagnosis.**
   - Run each of `TicketsApiTests`, `DashboardAuthTests` and `EvidenceFilesTests` alone with `--filter "FullyQualifiedName~<Class>"`. Note which test fails first and with what stack.
   - Check the spec's hypotheses: how `Program` and its services resolve `SYMPHONIST_HOME` (`SymphonistPaths.Resolve`), whether any static or singleton is shared across hosts, and whether non-collection test classes use `TestHome` or change the env var in parallel.
   - Run with `-- xUnit.ParallelizeTestCollections=false` to separate a parallelism problem from a lifetime problem.
4. **Host.Tests fix.** Apply the smallest root-cause fix:
   - If it is an env var or path race, put the offending classes in `SymphonistHomeCollection`, or inject the home path through the factory (`UseSetting` or a service override) instead of the process-global variable.
   - If it is shared state in `Program`, make it per-host.
   - If it is dispose ordering in the fixtures, dispose the client, then the factory, then `home`, and make sure no background service outlives the factory.
   Don't use `[Skip]`.
5. **Verify.**
   - Run the full suite 3 times to catch flakiness.
   - Confirm that `UnixFactAttribute` tests still skip or run per OS.
   - Confirm `.github/workflows/ci.yml` (`dotnet test --filter "Category!=Docker&Category!=Live"`) matches the command used.
   - Push and confirm the ubuntu, windows and macos CI jobs are green.
6. **Report.** Summarize the root cause, the changes and the final test counts in the PR.
