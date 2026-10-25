# GitHub Integrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** GitHub Projects boards, GitHub pull requests and GitHub Actions become supported tracker, repo and build
providers, mixable per project with Jira / Bitbucket / Bitbucket Pipelines, all behind a compiled-in provider registry.

**Architecture:**
- **Core** (`Symphonist.Core/Integrations/Providers`) defines the provider interfaces, the `ProviderRegistry`, and
  `ProviderSection` (one YAML section handed to a provider with strict binding). Config base records in
  `ProjectConfig.cs` keep only shared fields.
- **Integrations** holds every provider: its config subtype, its parser, its client and its links. `BuiltInProviders`
  wires them up. `ProjectConfigLoader`, `IntegrationFactory`, `TicketLinks`, `BoardQuery` and `Program.cs` dispatch
  through the registry.
- The GitHub tracker uses GraphQL (Projects v2). Pull requests and Actions use REST. All three share `GitHubApi`
  plumbing.

**Tech Stack:** .NET 10, YamlDotNet 18, System.Text.Json, `IHttpClientFactory` with the standard resilience handler,
xUnit, `StubHttpHandler` + JSON fixtures.

**Spec:** `docs/superpowers/specs/2026-10-24-github-integrations-design.md`

## Global Constraints

- Existing Jira / Bitbucket / Bitbucket Pipelines YAML loads unchanged; `samples/projects/orders.example.yaml` and
  `tests/Symphonist.Core.Tests/Config/orders.yaml` are not edited except for comments.
- `ITracker` and `ISourceControl` signatures do not change. `IBuildWatcher.GetStatusAsync` gains `BuildPurpose`.
- Type names in YAML: `jira`, `github-projects` (tracker); `bitbucket`, `github` (repo); `bitbucket-pipelines`,
  `github-actions` (build).
- github.com only: REST `https://api.github.com/`, GraphQL `https://api.github.com/graphql`, web `https://github.com/`.
- GitHub request headers: `Authorization: Bearer <token>`, `Accept: application/vnd.github+json`,
  `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: Symphonist`.
- Loader errors keep the existing style: `missing required 'repo.owner'`, `unsupported tracker.type 'x'`; new:
  `unknown field 'tracker.foo'`, `build.type 'github-actions' needs repo.type 'github'`.
- Missing secret message stays `secret '<name>' not set; run: symphonist secret set <name>`.
- New HTTP clients get the same safe-method-only retry handler as today (`Program.cs`).
- Every task leaves `dotnet test Symphonist.slnx` green.

## Review Focus

1. **GraphQL failures inside a 200.** GitHub answers bad scopes, unknown project or owner with HTTP 200 and an
   `errors` array. That must throw (project problem), never come back as "no tickets" → Task 5 test
   `GraphQl_errors_in_200_throw`.
2. **Rate limits read as a merge refusal.** A 403 with `x-ratelimit-remaining: 0`, or a 429, must throw, not turn into
   `MergeResult(false, ...)` or `NoChangesToPullException` → Task 3 test `Rate_limit_throws_instead_of_refusing`.
3. **Boards and threads over one page.** More than 100 items, more than 100 comments, or more than 100 workflow runs
   must all be read, since a missing item gets its ticket Blocked → Task 5 `Query_reads_all_item_pages`, Task 5
   `Comments_read_all_pages`, Task 4 `Runs_read_all_pages`.
4. **Fork pull request with the same branch name.** It must never be treated as ours → Task 3
   `Find_ignores_fork_with_same_branch`.
5. **Typo in an existing project file after the refactor.** An unknown key inside `tracker:` / `repo:` / `build:` must
   still fail with the field named, not be silently ignored → Task 2 `Unknown_field_in_provider_section_is_rejected`.

---

### Task 1: Provider abstractions and strict section binding (Core, additive)

**Files:**
- Create: `src/Symphonist.Core/Integrations/Providers/ProviderSection.cs`
- Create: `src/Symphonist.Core/Integrations/Providers/IProviders.cs`
- Create: `src/Symphonist.Core/Integrations/Providers/ProviderRegistry.cs`
- Test: `tests/Symphonist.Core.Tests/Integrations/ProviderSectionTests.cs`, `ProviderRegistryTests.cs`

**Interfaces:**
- Produces (namespace `Symphonist.Core.Integrations.Providers`):

```csharp
public enum BuildPurpose { Build, Deploy }            // in Symphonist.Core.Integrations (IBuildWatcher.cs), used from Task 2

public class ProviderSection
{
    public ProviderSection(string path, IReadOnlyDictionary<string, object?> values, string configDirectory,
        IReadOnlyCollection<string> sharedKeys);
    public string Path { get; }                      // "tracker" | "repo" | "build"
    public string Type { get; }                      // required; "missing required 'tracker.type'"
    public string ConfigDirectory { get; }
    /// Strict: every key not in sharedKeys must be a camelCase property of T, else "unknown field '{Path}.{key}'".
    public T Bind<T>() where T : new();
    public string Required(string? value, string field);   // "missing required '{Path}.{field}'"
}
public sealed class TrackerSection(...) : ProviderSection   // + string TokenSecret (required)
public sealed class RepoSection(...) : ProviderSection      // + TokenSecret, LocalPath (absolute, resolved against
                                                            //   ConfigDirectory), MainBranch ("main"), ProductionBranch ("production")
public sealed class BuildSection(...) : ProviderSection     // + int TimeoutMinutes (default 45; non-integer →
                                                            //   "invalid 'build.timeoutMinutes'")

public interface ITrackerProvider
{
    string Type { get; }
    string DisplayName { get; }                      // "Jira", "GitHub" — dashboard source badge
    string HttpClientName { get; }
    TrackerConfig Parse(TrackerSection section);
    ITracker Create(ProjectConfig project, HttpClient http, string token);
    Uri? IssueUrl(ProjectConfig project, string key);
}
public interface IRepoProvider
{
    string Type { get; }
    string HttpClientName { get; }
    RepoConfig Parse(RepoSection section);
    ISourceControl Create(ProjectConfig project, HttpClient http, string token);
    Uri PullRequestUrl(ProjectConfig project, long id);
}
public interface IBuildProvider
{
    string Type { get; }
    string RepoType { get; }
    BuildConfig Parse(BuildSection section);
    IBuildWatcher Create(ProjectConfig project, HttpClient http, string repoToken);   // http = repo provider's client
}
public sealed class ProviderRegistry(IEnumerable<ITrackerProvider> trackers, IEnumerable<IRepoProvider> repos,
    IEnumerable<IBuildProvider> builds)
{
    public ITrackerProvider Tracker(string type);    // unknown → ProjectConfigException("unsupported tracker.type '{type}' (supported: a, b)")
    public IRepoProvider Repo(string type);          // "unsupported repo.type ..."
    public IBuildProvider Build(string type);        // "unsupported build.type ..."
    public IReadOnlyList<string> HttpClientNames { get; }   // distinct, ordinal order
}
```

`ProviderSection.Bind<T>` serializes the non-shared keys back to YAML, then deserializes them into `T` with a
camelCase deserializer. It checks the keys against `T`'s properties first so the error names the field. The
constructors take the raw dictionary; the subclasses read and validate their shared keys in their constructors. The
type and shared-key lists are fixed per subclass: tracker `type, tokenSecret`; repo
`type, tokenSecret, localPath, mainBranch, productionBranch`; build `type, timeoutMinutes`.

- [ ] **Step 1: Write failing tests**

```csharp
// ProviderSectionTests
[Fact] Bind_maps_camelCase_keys()            // {type: x, owner: acme, project: "7"} → Dto { Owner="acme", Project=7 }
[Fact] Bind_rejects_unknown_key()            // {type: x, ownr: acme} → ProjectConfigException "unknown field 'tracker.ownr'"
[Fact] Bind_ignores_shared_keys()            // TrackerSection with tokenSecret: Bind<Dto>() does not throw
[Fact] Missing_type_is_reported()            // "missing required 'repo.type'"
[Fact] Repo_section_defaults_and_resolves_paths()  // MainBranch "main", ProductionBranch "production",
                                                   // localPath "src/x" → Path.GetFullPath("src/x", dir)
[Fact] Build_section_timeout_defaults_to_45()
[Fact] Bind_maps_lists()                     // workflows: [ci.yml, deploy.yml] → List<string> of 2
// ProviderRegistryTests (with tiny fake providers declared in the test file)
[Fact] Unknown_tracker_type_lists_supported() // Assert.Equal("unsupported tracker.type 'x' (supported: fake-a, fake-b)", ex.Message)
[Fact] Http_client_names_are_distinct()
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter "ProviderSection|ProviderRegistry"`. Expected: build failure (types missing).
- [ ] **Step 3: Implement the files above.**
- [ ] **Step 4: Run the same command.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(core): provider registry and strict provider sections`

### Task 2: Move config, loader, factory, links and build watcher onto the registry (Jira/Bitbucket as providers)

The one big refactor: the build only goes green once every piece moves. Behaviour stays the same.

**Files:**
- Modify: `src/Symphonist.Core/Config/ProjectConfig.cs` (base records)
- Modify: `src/Symphonist.Core/Config/ProjectConfigLoader.cs` (`Load(path, registry)`, `LoadAll(dir, registry)`; `TrackerDto`/`RepoDto`/`BuildDto` become `Dictionary<string, object?>`)
- Modify: `src/Symphonist.Core/Integrations/IBuildWatcher.cs` (add `BuildPurpose`)
- Create: `src/Symphonist.Integrations/Jira/JiraProvider.cs` (+ `JiraTrackerConfig`)
- Create: `src/Symphonist.Integrations/Bitbucket/BitbucketProvider.cs` (+ `BitbucketRepoConfig`), `BitbucketPipelinesProvider.cs` (+ `BitbucketPipelinesConfig`)
- Create: `src/Symphonist.Integrations/BuiltInProviders.cs`
- Modify: `JiraTracker.cs`, `BitbucketApi.cs`, `BitbucketSourceControl.cs`, `BitbucketPipelinesWatcher.cs` (take subtype configs), `IntegrationFactory.cs`
- Modify: `src/Symphonist.Host/TicketLinks.cs`, `Dashboard/BoardQuery.cs` (`SourceName`), `Orchestration/ProjectRegistry.cs`, `Orchestration/StageExecutor.cs` (pass purpose), `Program.cs` (clients from registry)
- Modify tests: `tests/Symphonist.Core.Tests/Symphonist.Core.Tests.csproj` (add ProjectReference to `src/Symphonist.Integrations`), every `ProjectConfigLoader.Load` caller in Core.Tests, `IntegrationFactoryTests.cs`, `GitWorkspaceTests.cs`, `StageExecutorTests.cs`, `GateReviewerTests.cs`, `GateReviewerGitTests.cs`, `TicketsApiTests.cs` (client names), `Fakes/FakeBuildWatcher.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:

```csharp
// Symphonist.Core.Config
public abstract record TrackerConfig(string Type, string TokenSecret);
public abstract record RepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch, string TokenSecret)
{ public abstract string FullName { get; } }
public abstract record BuildConfig(string Type, int TimeoutMinutes);
public static ProjectConfig ProjectConfigLoader.Load(string yamlPath, ProviderRegistry providers);
public static IReadOnlyList<ProjectConfig> ProjectConfigLoader.LoadAll(string dir, ProviderRegistry providers);
// Symphonist.Core.Integrations
Task<BuildStatus> IBuildWatcher.GetStatusAsync(string commitHash, BuildPurpose purpose, CancellationToken ct);
// Symphonist.Integrations
public sealed record JiraTrackerConfig(string Type, string TokenSecret, Uri Site, string Email, string Jql) : TrackerConfig(Type, TokenSecret);
public sealed record BitbucketRepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch,
    string TokenSecret, string Workspace, string Slug, string Email, string MergeStrategy) : RepoConfig(...)
{ public override string FullName => $"{Workspace}/{Slug}"; }
public sealed record BitbucketPipelinesConfig(string Type, int TimeoutMinutes) : BuildConfig(Type, TimeoutMinutes);
public static class BuiltInProviders { public static ProviderRegistry Registry { get; } }
public sealed class IntegrationFactory(IHttpClientFactory http, ISecretStore secrets, ProviderRegistry providers) : IIntegrationFactory;
```

- Provider `Type` / `HttpClientName` / `DisplayName`: Jira `jira` / `jira` / `Jira`; Bitbucket `bitbucket` / `bitbucket`;
  Pipelines `bitbucket-pipelines` with `RepoType` `bitbucket`. Jira/Bitbucket field names, defaults
  (`mergeStrategy: merge_commit`) and required-field messages stay exactly as today.
- Loader: after tracker, repo and build are parsed, if `providers.Build(build.Type).RepoType != repo.Type`, throw
  `build.type '{b}' needs repo.type '{r}'`.
- Factory: cache per project id per slot. Each slot holds the config reference it was built from and its token, and is
  rebuilt only when either changes. Secrets are read on every call as today; when tracker and repo name the same
  secret it is read once. The build watcher uses the repo token and the repo provider's HTTP client name.
- `TicketLinks.Issue/PullRequest` and `BoardQuery.SourceName` go through `BuiltInProviders.Registry`; the latter uses
  `DisplayName`.
- `StageExecutor.WatchBuildAsync` passes `BuildPurpose.Build` for `StageKind.Build` and `BuildPurpose.Deploy` for
  `StageKind.Deploy`. `FakeBuildWatcher` records `(commit, purpose)` in `Calls`; update its users.

- [ ] **Step 1: Write the new failing tests**

```csharp
// ProjectConfigLoaderTests
[Fact] Unknown_field_in_provider_section_is_rejected() // add "  jqll: x" under tracker → Contains("unknown field 'tracker.jqll'")
[Fact] Build_type_must_match_repo_type()                // repo bitbucket + build github-actions with a test registry
                                                        // that has a fake github-actions provider → "build.type 'github-actions' needs repo.type 'github'"
[Fact] Orders_fixture_maps_to_jira_and_bitbucket_subtypes() // IsType<JiraTrackerConfig>, IsType<BitbucketRepoConfig>, FullName "acme/orders"
// IntegrationFactoryTests
[Fact] Changed_tracker_secret_rebuilds_only_the_tracker()   // Assert.NotSame(io1.Tracker, io2.Tracker); Assert.Same(io1.SourceControl, io2.SourceControl)
[Fact] Shared_secret_is_read_once()                          // counting ISecretStore; tracker & repo tokenSecret both "shared" → Get called once per Create
// StageExecutorTests
[Fact] Deploy_stage_watches_with_deploy_purpose()            // builds.Calls contains (releaseMerge, BuildPurpose.Deploy)
```

- [ ] **Step 2: Run** `dotnet test Symphonist.slnx`. Expected: compile failures.
- [ ] **Step 3: Implement the changes listed above.** Update existing tests mechanically (subtype constructors,
  `Load(path, BuiltInProviders.Registry)`). Their assertions stay the same.
- [ ] **Step 4: Run** `dotnet test Symphonist.slnx`. Expected: all PASS, including `SampleConfigTests` and
  `Rejects_unsupported_types` (its `Contains` assertions still hold).
- [ ] **Step 5: Commit** `refactor: Jira and Bitbucket as registered providers`

### Task 3: `GitHubApi` and the `github` repo provider

**Files:**
- Create: `src/Symphonist.Integrations/GitHub/GitHubApi.cs`, `GitHubRepoProvider.cs` (+ `GitHubRepoConfig`), `GitHubSourceControl.cs`
- Modify: `src/Symphonist.Integrations/BuiltInProviders.cs`
- Test: `tests/Symphonist.Integrations.Tests/GitHub/GitHubSourceControlTests.cs`, `GitHubRepoProviderTests.cs`
- Fixtures: `gh-pr-create.json`, `gh-pr-no-commits.json` (422), `gh-pr-list.json`, `gh-pr-list-fork.json`, `gh-pr-merged.json`, `gh-pr-closed.json`, `gh-merge-ok.json`, `gh-merge-405.json`, `gh-compare.json`, `gh-branch.json`

**Interfaces:**
- Consumes: Task 1, Task 2 (`RepoConfig`, `BuiltInProviders`).
- Produces:

```csharp
public sealed record GitHubRepoConfig(string Type, string LocalPath, string MainBranch, string ProductionBranch,
    string TokenSecret, string Owner, string Repo, string MergeStrategy) : RepoConfig(...)
{ public override string FullName => $"{Owner}/{Repo}"; }
internal sealed class GitHubApi(HttpClient http, string token)
{
    public Task<(HttpStatusCode Status, string Text)> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct);
    public Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct); // non-2xx throws
    public Task<JsonDocument> GraphQlAsync(string query, object variables, CancellationToken ct);           // used by Task 5
    public static HttpRequestException Failure(HttpMethod method, string path, HttpStatusCode status, string text);
}
public sealed class GitHubSourceControl(HttpClient http, GitHubRepoConfig config, string token) : ISourceControl;
public static Uri GitHubSourceControl.PullRequestUrl(GitHubRepoConfig repo, long id);   // https://github.com/{owner}/{repo}/pull/{id}
```

- Provider: `Type` `github`, `HttpClientName` `github`. YAML keys `owner`, `repo` (required) and `mergeStrategy`
  (`merge` default, `squash`, `rebase`; anything else → `unknown repo.mergeStrategy '{v}'`).
- `SendRawAsync` throws `Failure` itself on 429, or on 403 with header `x-ratelimit-remaining: 0`. All headers come
  from Global Constraints. The base address is `https://api.github.com/` when unset. Failure messages start
  `GitHub {method} {path} failed:`.
- Endpoint behaviour is in the spec ("Source control: `github`"). The no-commits match: status 422 and an `errors[]`
  item whose `message` starts with `No commits between` → `NoChangesToPullException(message)`.
  `BranchContainsAsync(branch, other)` = `GET compare/{branch}...{other}`, `ahead_by == 0`.

- [ ] **Step 1: Write failing tests** (StubHttpHandler, path prefixes under `/repos/acme/orders/`)

```csharp
[Fact] Open_posts_head_and_base_and_maps_pr()       // body contains "\"head\":\"symphonist/orders-1\"", "\"base\":\"main\""; Id 12; State Open; SourceRepository "acme/orders"
[Fact] Open_with_no_commits_throws_NoChangesToPull()
[Fact] Find_returns_own_open_pr()                   // query has "head=acme%3Asymphonist%2Forders-1" (or unescaped equivalent) and "base=main"
[Fact] Find_ignores_fork_with_same_branch()         // head.repo.full_name "evil/orders" → null
[Fact] Get_maps_merged_and_closed()                 // merged_at set → Merged + MergeCommitHash; closed without → Declined, MergeCommitHash null
[Fact] Merge_sends_method_and_returns_sha()         // body contains "\"merge_method\":\"squash\"" when configured
[Fact] Merge_405_is_a_refusal_with_message()        // Merged false, Error == fixture "message"
[Fact] Rate_limit_throws_instead_of_refusing()      // PUT merge → 403 + x-ratelimit-remaining: 0 → HttpRequestException (StubHttpHandler gains optional response headers)
[Fact] Branch_contains_when_ahead_by_zero()         // and false when ahead_by 3
[Fact] Branch_head_reads_commit_sha()
[Fact] Sends_bearer_and_api_version_headers()
[Fact] Branch_file_url_points_at_blob()             // https://github.com/acme/orders/blob/symphonist/orders-1/docs/spec.md
// GitHubRepoProviderTests
[Fact] Parses_owner_repo_and_default_merge()        // MergeStrategy "merge"; FullName "acme/orders"
[Fact] Rejects_unknown_merge_strategy()
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter GitHub`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement**, register in `BuiltInProviders`, and extend `StubHttpHandler.On` with an optional
  `IReadOnlyDictionary<string,string>? headers`.
- [ ] **Step 4: Run** the same filter, then `dotnet test Symphonist.slnx`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(github): pull requests via the github repo provider`

### Task 4: `github-actions` build provider

**Files:**
- Create: `src/Symphonist.Integrations/GitHub/GitHubActionsProvider.cs` (+ `GitHubActionsConfig`), `GitHubActionsWatcher.cs`
- Modify: `BuiltInProviders.cs`
- Test: `tests/Symphonist.Integrations.Tests/GitHub/GitHubActionsWatcherTests.cs`
- Fixtures: `gh-runs-*.json` (built inline in tests if shorter)

**Interfaces:**
- Consumes: `GitHubApi`, `GitHubRepoConfig` (Task 3), `BuildPurpose` (Task 2).
- Produces:

```csharp
public sealed record GitHubActionsConfig(string Type, int TimeoutMinutes,
    IReadOnlyList<string> BuildWorkflows, IReadOnlyList<string> DeployWorkflows) : BuildConfig(Type, TimeoutMinutes);
public sealed class GitHubActionsWatcher(HttpClient http, GitHubRepoConfig repo, GitHubActionsConfig config, string token) : IBuildWatcher;
```

- Provider `Type` `github-actions`, `RepoType` `github`. YAML keys `buildWorkflows`, `deployWorkflows` (optional lists of
  workflow file names; empty items rejected as in `Items`).
- `GET actions/runs?head_sha={sha}&per_page=100&page={n}` until a page has fewer than 100 runs. A run's workflow file
  is `Path.GetFileName(run.path)`. Per workflow file, keep the run with the latest `created_at`.
- Classification follows the spec ("Build/deploy: `github-actions`"): the list for the purpose (or none = all runs),
  listed-but-missing workflows count as not started, and success / skipped / neutral count as passed.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] No_runs_is_NotFound()
[Fact] Any_incomplete_run_is_Running()               // statuses queued, in_progress, waiting
[Fact] Any_failed_conclusion_is_Failed()             // failure, cancelled, timed_out, action_required, startup_failure
[Fact] Success_skipped_neutral_is_Succeeded()
[Fact] Latest_run_per_workflow_wins()                // older failed + newer success of ci.yml → Succeeded
[Fact] Listed_workflow_without_run_keeps_Running()   // deployWorkflows [deploy.yml], only ci.yml succeeded → Running
[Fact] Listed_workflow_without_any_run_is_NotFound() // deployWorkflows [deploy.yml], no runs at all → NotFound
[Fact] Purpose_selects_list()                        // buildWorkflows [ci.yml], deployWorkflows [deploy.yml]; deploy.yml failed:
                                                     //   Build → Succeeded, Deploy → Failed
[Fact] Unlisted_runs_are_ignored()                   // codeql.yml failed, buildWorkflows [ci.yml] succeeded → Succeeded
[Fact] Runs_read_all_pages()                         // page 1 has 100 successes, page 2 has one failure → Failed
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter GitHubActions`. Expected: FAIL.
- [ ] **Step 3: Implement and register.**
- [ ] **Step 4: Run** the filter, then the full solution. Expected: PASS.
- [ ] **Step 5: Commit** `feat(github): Actions build/deploy watcher`

### Task 5: `github-projects` tracker — selection, mapping, validation

**Files:**
- Create: `src/Symphonist.Integrations/GitHub/GitHubProjectsProvider.cs` (+ `GitHubProjectsTrackerConfig`), `GitHubProjectsTracker.cs`, `GitHubProjectsQueries.cs` (GraphQL text constants)
- Modify: `BuiltInProviders.cs`
- Test: `tests/Symphonist.Integrations.Tests/GitHub/GitHubProjectsTrackerTests.cs`, `GitHubProjectsProviderTests.cs`
- Fixtures: `gh-viewer.json`, `gh-project.json` (project id, Status field + options), `gh-items-page1.json`, `gh-items-page2.json`, `gh-comments-page1.json`, `gh-comments-page2.json`, `gh-graphql-error.json`

**Interfaces:**
- Consumes: `GitHubApi.GraphQlAsync` (Task 3), `ProjectConfig`.
- Produces:

```csharp
public sealed record GitHubProjectsTrackerConfig(string Type, string TokenSecret, string Owner, int Project,
    string Label, string StatusField, IReadOnlyList<string>? Statuses, IReadOnlyList<string>? Repositories) : TrackerConfig(Type, TokenSecret);
public sealed class GitHubProjectsProvider : ITrackerProvider   // Type "github-projects", DisplayName "GitHub", HttpClientName "github"
{
    public static IReadOnlyList<string> PickupStatuses(ProjectConfig p);   // config Statuses ?? all pipeline-named statuses minus the done stage's onEnterStatus
    public static IReadOnlyList<string> RequiredStatuses(ProjectConfig p); // all pipeline-named statuses incl. done
    public static IReadOnlyList<string>? Repositories(ProjectConfig p);    // config Repositories ?? ([Repo.FullName] when Repo is GitHubRepoConfig) ?? null
}
public sealed class GitHubProjectsTracker(HttpClient http, GitHubProjectsTrackerConfig config, string token,
    IReadOnlyList<string> pickupStatuses, IReadOnlyList<string> requiredStatuses, IReadOnlyList<string>? repositories) : ITracker;
```

- YAML: `owner`, `project` (int), `label` required; `assignee` optional and only `me` accepted
  (`unsupported tracker.assignee '{v}'`); `statusField` default `Status`; `statuses`, `repositories` optional lists.
- "Pipeline-named statuses" = `entryStatuses`, `onEnterStatus`, `onGateStatus`, `approvedWhenStatus` of every stage,
  non-null, distinct ignoring case.
- `GraphQlAsync` throws when the HTTP status is non-2xx or when `errors` is a non-empty array; the message joins
  `errors[].message`.
- Query flow (verify field names against GitHub's GraphQL schema while implementing):
  1. `viewer { login }`, cached.
  2. `repositoryOwner(login: $owner) { ... on ProjectV2Owner { projectV2(number: $n) { id field(name: $f) { ... on ProjectV2SingleSelectField { id options { id name } } } } } }`.
     A null owner or project → throw `GitHub project {owner}/{n} not found or not accessible`. Field missing or not
     single-select → throw `GitHub project {owner}/{n} has no single-select field '{f}'`. Required statuses missing from
     the options → throw `GitHub project {owner}/{n} field '{f}' has no option(s): In Progres`.
  3. Items, 100 per page, all pages, through `node(id: $project) { ... on ProjectV2 { items(...) } }`: item id,
     `fieldValueByName(name: $f)` single-select `name`, and content issue `id number title body url state repository
     { name nameWithOwner } assignees(first: 20) labels(first: 50)`.
  4. Filter per spec (open Issue, viewer assigned, label matched ignoring case, Status in pickup list ignoring case,
     repository in list ignoring case when the list is non-null).
  5. Comments per kept issue, 100 per page, all pages.
- Mapping: key `{repository.name}-{number}`. Duplicate keys → drop all of them and log a warning (the tracker takes an
  optional `ILogger`; `NullLogger` by default). Description = `body ?? ""`, `IsMine` = author login equals the viewer
  (case-insensitive), deleted author → `"ghost"`, comments ordered by `createdAt`.
- The tracker stores, per key, `(ItemId, IssueId)`, plus the project id, field id and option ids from the latest
  query, for Task 6.

- [ ] **Step 1: Write failing tests**

```csharp
// GitHubProjectsProviderTests (pure)
[Fact] Pickup_defaults_to_pipeline_statuses_without_done() // orders pipeline → [Backlog, Planned, In Progress, In Review]
[Fact] Pickup_uses_configured_statuses()
[Fact] Required_includes_done()
[Fact] Repositories_default_to_github_repo()               // ["acme/orders"]
[Fact] Repositories_null_for_bitbucket_repo()
[Fact] Rejects_assignee_other_than_me()
// GitHubProjectsTrackerTests (StubHttpHandler, POST /graphql routed by bodyContains)
[Fact] Query_keeps_open_assigned_labelled_issues_in_pickup_statuses() // fixture mixes: draft, PR item, closed issue, other assignee,
                                                                       // no label, Status Done, empty Status, other repo → only one ticket "orders-12"
[Fact] Query_maps_ticket_fields()                // Title, Description, Status "Planned", WebUrl issue url
[Fact] Query_reads_all_item_pages()
[Fact] Comments_read_all_pages()                 // 101 comments; IsMine true for viewer
[Fact] Duplicate_keys_are_dropped()              // acme/orders#5 and other/orders#5, repositories null → neither returned
[Fact] Missing_status_option_throws_naming_it()  // pipeline has "In Progres" → message contains "In Progres"
[Fact] Unknown_project_throws()
[Fact] GraphQl_errors_in_200_throw()
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Integrations.Tests --filter GitHubProjects`. Expected: FAIL.
- [ ] **Step 3: Implement and register.**
- [ ] **Step 4: Run** the filter, then the full solution. Expected: PASS.
- [ ] **Step 5: Commit** `feat(github): Projects board tracker query`

### Task 6: `github-projects` tracker — transition, comment, issue links

**Files:**
- Modify: `GitHubProjectsTracker.cs`, `GitHubProjectsProvider.cs` (`IssueUrl`), `GitHubProjectsQueries.cs`
- Test: `GitHubProjectsTrackerTests.cs`, `tests/Symphonist.Host.Tests/...` (`TicketLinks` test, in the existing dashboard/API test file that covers links)

**Interfaces:**
- Consumes: the state Task 5 stores.
- Produces: `TransitionAsync`, `CommentAsync`, and `IssueUrl(p, key)` =
  `https://github.com/{owner}/{repo}/issues/{number}`, where `key` is split at its last `-`, owner comes from the
  `Repositories(p)` entry whose name matches (ignoring case), else the tracker's `Owner`. A key that does not parse →
  null.

- Transition: find the option by name (ignoring case). None → `false`. Otherwise run the mutation
  `updateProjectV2ItemFieldValue(input: { projectId, itemId, fieldId, value: { singleSelectOptionId } })` and return
  `true`. A key not in the stored map → re-run the query once; still unknown → `InvalidOperationException("ticket
  {key} is not on GitHub project {owner}/{n}")`.
- Comment: mutation `addComment(input: { subjectId: issueId, body })` with the Markdown as-is. Unknown key is handled
  as in transition.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] Transition_sets_single_select_option()     // request body contains the item id, field id and option id of "In Review"
[Fact] Transition_to_unknown_option_returns_false()
[Fact] Transition_before_query_queries_first()
[Fact] Comment_posts_markdown_to_issue()           // body contains "addComment" and the issue node id
[Fact] Issue_url_uses_repository_owner()           // Repositories ["acme/orders"], key "orders-12" → https://github.com/acme/orders/issues/12
// Host
[Fact] Ticket_links_for_github_project()          // TicketLinks.Issue + PullRequest for a github-projects + github project
```

- [ ] **Step 2: Run** `dotnet test Symphonist.slnx --filter "GitHubProjects|Ticket_links"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full solution. Expected: PASS.
- [ ] **Step 5: Commit** `feat(github): board status transitions, comments and issue links`

### Task 7: Sample, README, mixed-provider load test

**Files:**
- Create: `samples/projects/orders-github.example.yaml` (all-GitHub, every new field documented in the sample's
  comment style; pipeline statuses Backlog, Planned, In Progress, In Review, Done)
- Modify: `samples/projects/orders.example.yaml` (comments only: list the other types next to `jira`, `bitbucket`,
  `bitbucket-pipelines`)
- Modify: `tests/Symphonist.Core.Tests/Symphonist.Core.Tests.csproj` (copy the new sample to `Config/`)
- Modify: `tests/Symphonist.Core.Tests/Config/SampleConfigTests.cs`
- Modify: `README.md` (prerequisites mention GitHub; a GitHub step in one-time setup with the token: classic token
  with `repo` + `project` scopes, and the fine-grained permissions with the user-owned-project caveat, verified
  against GitHub docs; one `tokenSecret` can serve all three GitHub slots; mixing rules incl. build tied to repo host)

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] GitHub_example_project_loads()   // IsType<GitHubProjectsTrackerConfig>, IsType<GitHubRepoConfig>, IsType<GitHubActionsConfig>;
                                        // tracker & repo TokenSecret equal; Pipeline[^1].Kind == Done
[Fact] Jira_tracker_with_github_repo_loads()  // orders.example.yaml with repo/build sections swapped for GitHub ones (string replace) → loads
```

- [ ] **Step 2: Run** `dotnet test tests/Symphonist.Core.Tests --filter SampleConfig`. Expected: FAIL (file missing).
- [ ] **Step 3: Write the sample and the README section.**
- [ ] **Step 4: Run** `dotnet test Symphonist.slnx`. Expected: PASS.
- [ ] **Step 5: Commit** `docs: GitHub setup, sample project and mixing rules`
