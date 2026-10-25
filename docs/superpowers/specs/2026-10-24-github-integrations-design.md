# GitHub integrations (tracker, source control, build/deploy) — design

Date: 2026-10-24
Status: draft, awaiting approval

## Intent

Symphonist today only works with Jira (tracker), Bitbucket Cloud (pull requests) and Bitbucket Pipelines (build and
deploy status). The original design (`2026-10-21-symphonist-design.md`) always meant GitHub to be supported. This adds
GitHub for all three slots:

- **Planning:** a GitHub Projects (v2) board, whose Status column plays the role of Jira statuses.
- **Source control:** GitHub pull requests.
- **Build/deploy:** GitHub Actions workflow runs.

Providers must mix per project, e.g. Jira + GitHub repo + Actions, or a GitHub board + Bitbucket repo + Pipelines.

To get there, provider-specific code moves behind a **compiled-in provider registry**: each provider owns its YAML
section, its config type, its HTTP client and its web links, and core stops knowing about Jira or Bitbucket fields. This
also makes the next providers on the roadmap (Azure DevOps Boards, Azure Repos) cheaper to add.

## Goals

- `tracker.type: github-projects`, `repo.type: github`, `build.type: github-actions`, each usable alone or together.
- Mix and match: any tracker with any repo; a build provider is paired with its own repo host.
- Same pipeline behaviour as with Jira/Bitbucket: stage statuses, gates approved by moving the card, send-back by moving
  it back, `@claude` comments, signed Symphonist comments, release PR from main to production, deploy watched on the
  production merge commit.
- Existing project files keep loading unchanged (Jira / Bitbucket / Bitbucket Pipelines YAML is identical).
- Provider registry compiled into Symphonist; adding a provider means adding classes and one registration line.

## Non-goals

- Loading providers from external assemblies or NuGet (no plugin SDK, no assembly loading).
- GitHub Enterprise Server (github.com only; a configurable API base URL can come later).
- Plain GitHub Issues without a Projects board, or tracking stage with labels/milestones.
- Project draft items (cards without an issue behind them): skipped.
- Webhooks: polling stays as it is.
- Deploys triggered other ways (`workflow_dispatch`, tags, Environments with approval). Deploy = a workflow run on the
  production branch's merge commit, as the user's projects do today.

## Anti-goals

- A GitHub project that silently does nothing: a misconfigured board (wrong owner, number, missing Status option, no
  access) must surface as a load error or a clear Blocked reason, never as "no tickets".
- Forcing a merge past branch protection. A refused merge waits, exactly like a Bitbucket refusal.
- Breaking existing Jira/Bitbucket users while refactoring onto the registry.
- Treating a fork's pull request as ours (same fail-closed rule as Bitbucket).

## Constraints (from the current code)

- Integration seams already exist in `Symphonist.Core/Integrations`: `ITracker`, `ISourceControl`, `IBuildWatcher`,
  bundled as `ProjectIntegrations` by `IIntegrationFactory`. `ITracker` and `ISourceControl` stay as they are;
  `IBuildWatcher` gains which stage it is watching for (see Build/deploy).
- A new ticket whose status matches no `entryStatuses` goes to the first stage (`PipelineEngine.Create`), and an active
  ticket missing from a query result is Blocked with "no longer matches tracker query" (`Orchestrator.SyncAsync`). So
  the tracker's selection decides both what gets picked up and what gets blocked; Jira users control it through JQL.
- Hard-wired places to replace: `ProjectConfigLoader` (`Supported(... "jira")`, `"bitbucket"`, fixed `TrackerDto` /
  `RepoDto`), `IntegrationFactory` (always Jira + Bitbucket), `TicketLinks` (type switches), `Program.cs` (named HTTP
  clients list), `Orchestrator` (`p.Repo.FullName` in the fork check).
- Core config fields used outside providers: `Repo.LocalPath`, `Repo.MainBranch`, `Repo.ProductionBranch`,
  `Repo.FullName`, `Build.TimeoutMinutes`, `Tracker`/`Repo` `TokenSecret` (factory), `Type`.
- YamlDotNet with camelCase naming; the loader is strict (unknown properties fail) and gives field-level messages like
  `tracker.jql is required`. Providers must keep that quality.
- Ticket keys become branch names (`symphonist/{key}`), DB keys (unique per project) and appear in branch file URLs, so
  keys must be branch- and URL-safe.
- HTTP clients for writes use a resilience handler that retries safe methods only (`Program.cs`). New clients need the
  same. GitHub GraphQL is always `POST`, so GraphQL calls are not retried by that handler; that is accepted.
- Tests use `StubHttpHandler` plus JSON fixtures in `tests/Symphonist.Integrations.Tests/Fixtures`.

## Approaches considered

- **A. Typed config per provider.** Abstract `TrackerConfig`/`RepoConfig` with Jira/GitHub subtypes; loader and factory
  switch on `type`. Compiler-checked, moderate churn; core still knows every provider.
- **B. Flat records with nullable fields.** Least churn; every provider reads `!` fields and it degrades with each new
  provider.
- **C. Provider registry (chosen).** Each provider registers its YAML parser, config type, client factory and links;
  core only knows `type` plus shared fields. Most extensible, a bit more machinery. Chosen by the user; a third provider
  (Azure DevOps) is on the roadmap. Within C, providers are compiled in only (user's call), so no SDK or plugin loading.

## Design

### Provider registry

Three provider kinds, defined in `Symphonist.Core` (names are suggestions):

```csharp
public interface ITrackerProvider
{
    string Type { get; }                                   // "jira", "github-projects"
    TrackerConfig Parse(ProviderSection section);          // throws ProjectConfigException with field paths
    // The whole project: the GitHub tracker needs the pipeline's statuses and the repo for its defaults.
    ITracker Create(ProjectConfig project, string token);
    Uri? IssueUrl(ProjectConfig project, string key);
}

public interface IRepoProvider
{
    string Type { get; }                                   // "bitbucket", "github"
    RepoConfig Parse(ProviderSection section);
    ISourceControl Create(ProjectConfig project, string token);
    Uri? PullRequestUrl(ProjectConfig project, long id);
}

public interface IBuildProvider
{
    string Type { get; }                                   // "bitbucket-pipelines", "github-actions"
    string RepoType { get; }                               // the repo type it works with
    BuildConfig Parse(ProviderSection section);
    IBuildWatcher Create(ProjectConfig project, string repoToken);
}

public sealed class ProviderRegistry(IEnumerable<ITrackerProvider>, IEnumerable<IRepoProvider>, IEnumerable<IBuildProvider>)
```

- `ProviderSection` wraps one YAML mapping (`tracker:`, `repo:`, `build:`) plus its field-path prefix and the project
  file's directory, with a way to deserialize it strictly into a provider DTO. How the loader hands a sub-mapping to the
  provider (YamlDotNet representation model, or deserializing to a node and re-deserializing) is the builder's call; the
  requirement is strict unknown-field errors and messages naming the full path (`repo.owner is required`).
- Config base records in core carry only shared fields:
  - `TrackerConfig(Type, TokenSecret)`.
  - `RepoConfig(Type, LocalPath, MainBranch, ProductionBranch, TokenSecret)` with abstract `FullName`
    ("owner/repo", "workspace/slug").
  - `BuildConfig(Type, TimeoutMinutes)`.
  Shared fields (`tokenSecret`, `localPath`, `mainBranch`, `productionBranch`, `timeoutMinutes`) are parsed by core
  with today's defaults, so every provider handles them the same; the provider parses the rest. Provider subtypes
  (`JiraTrackerConfig`, `GitHubProjectTrackerConfig`, `BitbucketRepoConfig`, `GitHubRepoConfig`,
  `GitHubActionsBuildConfig`, ...) live in `Symphonist.Integrations` next to their provider.
- `mergeStrategy` becomes provider-owned (values differ per host).
- Loader: `ProjectConfigLoader.Load(path, registry)` / `LoadAll(dir, registry)`. Unknown `type` → error listing the
  registered types. Build provider whose `RepoType` differs from `repo.type` → error
  (`build.type github-actions needs repo.type github`).
- Each provider declares its named HTTP client; `Program.cs` registers clients for all registered providers instead of
  the fixed list, with the same safe-method-only retry handler.
- `IntegrationFactory` resolves providers by type, reads each secret on every call (as now), and caches per project per
  slot keyed on config object + token, so a changed tracker token rebuilds only the tracker. When tracker and repo name
  the same `tokenSecret` it is read once.
- `TicketLinks` asks the providers for `IssueUrl` / `PullRequestUrl`.
- `Orchestrator`'s fork check keeps using `p.Repo.FullName` (now abstract on the base).
- Registration: one place in `Symphonist.Integrations` (e.g. `AddSymphonistProviders(this IServiceCollection)`) adds
  Jira, Bitbucket, Bitbucket Pipelines and the three GitHub providers. The CLI and tests that load config build the same
  registry.

### Jira and Bitbucket on the registry

Pure move: `JiraTracker`, `BitbucketSourceControl`, `BitbucketPipelinesWatcher` keep their behaviour, read their
provider config subtype, and their YAML fields and messages stay the same. Existing tests and
`samples/projects/orders.example.yaml` must load and pass unchanged in meaning.

### GitHub auth

- One token works for all three GitHub slots; projects using GitHub everywhere set the same `tokenSecret`.
- Bearer auth (`Authorization: Bearer <token>`), `X-GitHub-Api-Version` header, `User-Agent: Symphonist`.
- Required access: issues read/write and pull requests read/write, contents read, actions read, Projects read/write.
  Fine-grained tokens have gaps for user-owned Projects; the README must state which token type works for user-owned vs
  org-owned boards (builder to verify against GitHub docs and note the result). A classic token with `repo` + `project`
  scopes is the fallback that always works.

### Tracker: `github-projects`

```yaml
tracker:
  type: github-projects
  owner: acme                 # org or user that owns the Project
  project: 7                  # Project number (from the board URL)
  tokenSecret: github-acme
  label: Claude               # issues must carry this label
  # assignee: me              # default "me" = the token's user; the only value supported now
  # statusField: Status       # single-select field used as status; default "Status"
  # statuses: [Backlog, Planned, In Progress, In Review]   # columns to pick up; default below
  # repositories: [acme/orders]  # issue repos to pick up; default below
```

- **API:** GraphQL (`https://api.github.com/graphql`); Projects v2 has no REST API for items. Owner is resolved as an
  organization first, then a user. Project not found or no access → error at first query (Blocked reason / log), naming
  owner and number.
- **Query:** page through all project items (100 per page). Keep items that pass every filter below. All pages or
  nothing (throw on any failure, matching `ITracker.QueryAsync`'s contract). The filters play the role of the user's JQL
  in Jira, including `statusCategory != Done`:
  - content is an **open** `Issue` (draft issues and pull request items skipped; closed issues skipped);
  - assigned to the viewer and carrying `label`;
  - **Status column** (user-confirmed default): the item's Status is one the pipeline names (`entryStatuses`,
    `onEnterStatus`, `onGateStatus`, `approvedWhenStatus` across all stages), except the `done` stage's
    `onEnterStatus`. An empty Status is skipped. `statuses:` replaces this default list. Consequence, same as Jira
    today: an active ticket whose card moves to a column outside the list, or whose issue is closed, drops out of the
    results and is Blocked ("no longer matches tracker query"); done tickets are not active, so moving to Done is fine;
  - **Repository** (user-confirmed default): the issue's repository is in `repositories` (`owner/repo`, compared
    ignoring case). Default when `repositories` is not set: `[repo.FullName]` when `repo.type` is `github`; no
    repository filter otherwise (e.g. a Bitbucket repo). This keeps issues from other codebases on an org board out of
    this project's work copy.
- **Status validation:** on every query, the tracker checks that every status the pipeline names (all of the fields
  above, including the done status) exists as an option of `statusField`, and that the field exists and is
  single-select. A miss fails the query with a message naming the missing option(s) and the board, so a typo such as
  "In Progres" shows up as a project problem instead of a silent "no transition".
- **Ticket mapping:**
  - Key: `{repo}-{number}` using the issue's repository name, e.g. `orders-123`. Branch- and URL-safe, Jira-like.
    Two board items mapping to the same key (same repo name, different owners) are both left out with a warning log.
  - Title, body (Markdown, as-is), Status = the item's value of `statusField` (empty when unset), web URL = the issue
    URL.
  - Comments: issue comments with author login, `IsMine` = author is the viewer, created, body. Paginate fully (Jira
    gets all comments today).
- **Transition:** set the item's `statusField` to the single-select option whose name equals the target status
  (case-insensitive). No such option → `false` (same as Jira's "no transition"). The tracker remembers key → project item
  id / issue id from the latest query; on a miss it re-queries once.
- **Comment:** add an issue comment with the Markdown as-is (GitHub renders Markdown; no ADF step).
- Symphonist does not close issues; the board's own automation (e.g. "item closed → Done") is the developer's
  business.
- `IssueUrl` is derived without an API call: `https://github.com/{owner}/{repo}/issues/{number}` from the key, with
  `owner` taken from the `repositories` entry whose repo name matches (the default list included), else the tracker's
  `owner`.

### Source control: `github`

```yaml
repo:
  type: github
  owner: acme
  repo: orders
  localPath: C:/src/acme-orders
  mainBranch: main
  productionBranch: production
  tokenSecret: github-acme
  # mergeStrategy: merge      # merge (default) | squash | rebase
```

REST, `https://api.github.com/repos/{owner}/{repo}/...`. `FullName` = `owner/repo`.

- **Open PR:** `POST /pulls` with head = branch, base = destination. A 422 whose errors say there are no commits between
  the branches → `NoChangesToPullException` (match GitHub's message exactly, as Bitbucket does). Other failures throw.
- **Find open PR:** `GET /pulls?state=open&head={owner}:{branch}&base={dest}`, then also check the head repo full name
  equals `FullName` (never a fork's).
- **Get PR:** `GET /pulls/{n}`. State: open → Open; closed with `merged_at` → Merged; closed otherwise → Declined.
  `MergeCommitHash` = `merge_commit_sha` only when merged. `SourceRepository` = `head.repo.full_name`,
  `SourceCommit` = `head.sha`.
- **Merge:** `PUT /pulls/{n}/merge` with `merge_method` from `mergeStrategy`. 200 + `merged: true` → merged with `sha`.
  405 (not mergeable, e.g. branch protection), 409 (head changed) and other 4xx → `Merged=false` with GitHub's message.
  5xx → throw. GitHub merges synchronously, so `Accepted` is not used.
- **Branch contains:** `GET /compare/{branch}...{other}`; contained when `ahead_by == 0` (other has nothing branch
  lacks). Builder to confirm the direction against the Bitbucket semantics in `ISourceControl.BranchContainsAsync`.
- **Branch head:** `GET /branches/{branch}` → `commit.sha`.
- **Links:** PR `https://github.com/{owner}/{repo}/pull/{n}`; branch file
  `https://github.com/{owner}/{repo}/blob/{branch}/{path}`.

### Build/deploy: `github-actions`

```yaml
build:
  type: github-actions
  # timeoutMinutes: 45
  # buildWorkflows: [ci.yml]          # optional: workflow files that count for the build stage (main merge commit)
  # deployWorkflows: [deploy.yml]     # optional: workflow files that count for the deploy stage (production merge)
```

- Requires `repo.type: github`; uses the repo's owner/repo and token.
- `IBuildWatcher.GetStatusAsync` gains the stage it serves (build or deploy; e.g. a `StageKind` or small enum
  argument). Bitbucket Pipelines ignores it. `StageExecutor` passes it.
- `GET /actions/runs?head_sha={sha}` (all pages). Per workflow, use its latest run (a re-run replaces the earlier
  result).
- **No list for the stage:** all runs for the commit count. None → `NotFound` (the existing timeout then fails the
  stage); any run not `completed` (queued, in_progress, waiting, pending, requested) → `Running`; any completed run with
  conclusion other than success / skipped / neutral → `Failed`; otherwise `Succeeded`.
- **List for the stage:** only runs of the listed workflow files count, and **each listed workflow must have a run**:
  a listed workflow with no run yet counts as not started, so the result is `Running` while other runs exist and
  `NotFound` while none do (timeout applies either way). This guards deploy workflows chained after CI
  (`workflow_run`), whose run does not exist when CI finishes. Any failure among the listed → `Failed`.
- The same watcher serves the build stage (main merge commit) and the deploy stage (production merge commit), as
  today. A push-triggered deploy workflow on the production branch is picked up automatically.
- The commit hash passed in is the full merge SHA from GitHub; a short hash is not expected but should still match
  (prefix), as in the Bitbucket watcher.

### Docs and samples

- README: GitHub setup (token type and scopes, `symphonist secret set github-<id>`), and a note that providers can be
  mixed, with the build provider tied to the repo host.
- `samples/projects/` gets a GitHub example (all-GitHub), and the Jira example notes the alternatives. Sample-config
  tests cover both.
- Pipeline statuses in the GitHub sample use typical board columns (Backlog, Planned, In Progress, In Review, Done).

### Testing

- Loader: registry dispatch, unknown type lists registered types, strict unknown fields per provider, field-path
  messages, build/repo type mismatch, existing Jira/Bitbucket files unchanged.
- Factory: per-slot creation and caching, shared token read once, missing secret message unchanged.
- GitHub providers: stub-handler tests with JSON fixtures (GraphQL item pages, comments pages, status update, PR create /
  no-commits 422 / list with fork / merged / 405 refusal, compare, branch, workflow runs mixes).
- Host: `TicketLinks` for GitHub tickets and PRs; existing orchestration tests keep passing with fakes.

## Decisions made in the conversation

- Planning on GitHub = a Projects board's Status column (user).
- Selection = assigned to me + a label, same as Jira (user).
- Deploy = workflow run on push to the production branch (user).
- Providers mix per project (user).
- Approach C, provider registry, compiled in only (user).
- Board selection: open issues in any column the pipeline names except Done; empty Status skipped; `statuses:`
  overrides (user, "pretty much" on the proposal).
- Repository filter defaulting to the configured GitHub repo; none for a Bitbucket repo (user).

## Calls made by the designer (check these)

- Draft items skipped.
- Key format `{repo}-{number}`; collisions left out with a warning.
- Build watcher counts all workflow runs for the commit unless `buildWorkflows` / `deployWorkflows` lists them; listed
  workflows must each have a run.
- Status options validated on every query; a missing option fails the query loudly.
- Providers' `Create` take the whole `ProjectConfig`.
- github.com only; Enterprise Server out of scope.
- `mergeStrategy` default `merge` for GitHub; provider-owned values.
- Owner resolved as org, then user, rather than an `ownerType` field.
- `statusField` configurable, default `Status`; `assignee` fixed to the token's user for now.

## Left to the builder

- Exact names of the registry types and how a YAML sub-mapping reaches a provider, within the strictness requirement.
- GraphQL query shapes and page sizes.
- Exact GitHub error strings to match (no-commits 422), verified against the API.
- Token type guidance for user- vs org-owned Projects, verified against GitHub docs.
- Order of work, as long as Jira/Bitbucket stay green throughout.
