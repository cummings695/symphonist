# Dashboard Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Symphonist dashboard an app frame (slate title bar, project sidebar, shared status line, light/dark toggle) and restyle the board and ticket detail pages, without changing what any command does.

**Architecture:** `MainLayout` (interactive, persists across navigation) owns the frame. It loads a small shell model (`BoardQuery.LoadShellAsync`) and refreshes it on `BoardNotifier.Changed`. Pages talk to the frame through a scoped `DashboardChrome` service: status messages, per-source refresh errors, the page label for the tab title, and the current project for the sidebar highlight. Styling stays hand-written CSS on reworked tokens in `wwwroot/app.css` plus component `.razor.css` files.

**Tech Stack:** .NET 10, Blazor InteractiveServer, bUnit 2.12 (strict JS interop), xUnit 2.9.

**Spec:** `docs/superpowers/specs/2026-10-24-dashboard-redesign-design.md`. Mockups: `.superpowers/brainstorm/675-1792855284/content/layout-merged.html` (board, light and dark) and `detail-page.html` (ticket page). Open them in a browser; they are the visual reference for every CSS step.

## Global Constraints

- Ochre means "waiting on you" and nothing else. Teal accent `#0f766e` light / `#4fd1c0` dark is for links, keys, the active sidebar row, done stepper steps, and the title-bar edge `#14b8a6`. Red is for blocked only.
- Title bar background `#1e2733` light / `#0b0f14` dark, with a 3px `#14b8a6` bottom edge. Page background `#f6f7f9`; surfaces white.
- Radii: lanes ~14px, cards and panels ~12px, buttons ~8px.
- Every status, risk and count is also spelled out in text (or in a visually-hidden label next to it).
- Every colour is a `:root` token with a dark value. Dark tokens live under `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {…} }` and again under `:root[data-theme="dark"] {…}`.
- No CSS framework, no JS build step, no icon fonts, no webfonts, no inline `<script>`. Icons are inline SVG or CSS shapes.
- Phone breakpoint stays `40rem`.
- Keep every existing `data-*` test hook, unless a task says it is removed.
- Ticket and project text is never rendered as markup.
- Run tests with `dotnet test tests/Symphonist.Host.Tests` (filter with `--filter "FullyQualifiedName~<Class>"`).

## Review Focus

1. **Long names.** A 60-character project name in the sidebar, or a long unbroken ticket title or key, must truncate (sidebar) or wrap (cards, breadcrumb, `<h1>`) and never widen the page. Test in Task 3 and Task 7. Manual check in Task 8.
2. **Blocked browser storage.** With `localStorage` throwing, the theme toggle must still render and must not throw into the circuit. Test in Task 4.
3. **Ticket in an unknown project or stage.** The detail page shows no stepper, and the breadcrumb falls back to the project id. Test in Task 7.
4. **No projects configured.** The sidebar shows an empty note, the title-bar counts read 0, and the board keeps its setup hint. Test in Task 3.
5. **Shell refresh fails while a page is fine.** The layout's error shows in the status line and clears on the next good load. It must not wipe the page or the sidebar. Test in Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Symphonist.Host/Dashboard/BoardModels.cs` (modify) | `ShellProject`, `ShellModel`, `PipelineStage`; new fields on `BoardCard`, `BoardColumn`, `TicketDetailModel` |
| `src/Symphonist.Host/Dashboard/BoardQuery.cs` (modify) | `LoadShellAsync`, stage numbers, column kinds, detail pipeline |
| `src/Symphonist.Host/Dashboard/DashboardChrome.cs` (create) | scoped page↔frame state: messages, errors, page label, current project |
| `src/Symphonist.Host/Dashboard/DashboardSetup.cs` (modify) | register `DashboardChrome` as scoped |
| `src/Symphonist.Host/Components/Layout/MainLayout.razor` + `.razor.css` (modify/create) | title bar, status line, tab title, phone menu, theme toggle |
| `src/Symphonist.Host/Components/Shared/ProjectSidebar.razor` + `.razor.css` (create) | the project list |
| `src/Symphonist.Host/Components/Shared/ProjectHeading.razor` + `.razor.css` (rename from `ProjectBar`) | filtered-view project heading |
| `src/Symphonist.Host/Components/Shared/TicketCard.razor` + `.razor.css` (modify) | restyled card with stage progress |
| `src/Symphonist.Host/Components/Shared/StageStepper.razor` + `.razor.css` (create) | the detail page's pipeline stepper |
| `src/Symphonist.Host/Components/Pages/Board.razor`, `TicketDetail.razor` + `.razor.css` (modify) | page content only |
| `src/Symphonist.Host/wwwroot/app.css` (modify) | tokens, shared controls, board lanes |
| `src/Symphonist.Host/wwwroot/js/theme-init.js`, `js/theme.js` (create) | apply the stored theme before paint; read and set the theme |
| `src/Symphonist.Host/Components/App.razor` (modify) | load `theme-init.js` in `<head>` |
| `tests/Symphonist.Host.Tests/Dashboard/DashboardChromeTests.cs`, `MainLayoutTests.cs` (create) | new tests |

---

### Task 1: Data for the frame, the cards and the stepper

**Files:**
- Modify: `src/Symphonist.Host/Dashboard/BoardModels.cs`, `src/Symphonist.Host/Dashboard/BoardQuery.cs`
- Test: `tests/Symphonist.Host.Tests/Dashboard/BoardQueryTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ShellProject(string Id, string Name, bool Paused, string? Problem, int WaitingOnYou, int Active);` (`Active` counts the project's tickets not in a Done-kind stage)
  - `public sealed record ShellModel(IReadOnlyList<ShellProject> Projects, BoardCounts Totals);`
  - `public sealed record PipelineStage(string Id, StageKind Kind, GatePolicy Gate);`
  - `Task<ShellModel> BoardQuery.LoadShellAsync(CancellationToken ct)`: every configured project ordered by id (as in `LoadBoardAsync`), and totals across all tickets regardless of any filter.
  - `BoardCard` gains trailing `int? StageNumber = null, int? StageCount = null`. Values are 1-based within the card's own project pipeline. Both are null for an unknown project or stage and for a Done-kind stage.
  - `BoardColumn` gains trailing `StageKind? Kind = null`. It is set for every stage column (filtered and all-projects) and is null for the unknown column.
  - `TicketDetailModel` gains trailing `string ProjectName, IReadOnlyList<PipelineStage> Pipeline`. `ProjectName` is the configured name, or the project id when unknown. `Pipeline` is empty when the project is unknown.

- [ ] **Step 1: Write the failing tests** in `BoardQueryTests` (the fixture has `orders` and `billing`, each with pipeline triage, plan, implement, done):
  - `Shell_lists_every_project_with_its_counts`: seed `ORD-1` plan AwaitingApproval, `ORD-2` implement Running, `ORD-3` done Done, `BIL-1` triage Blocked. Assert `Projects.Select(p => p.Id)` equals `["billing", "orders"]`; orders `WaitingOnYou == 1`, `Active == 2`; billing `WaitingOnYou == 0`, `Active == 1`; `Totals == new BoardCounts(1, 1, 1)`.
  - `Shell_reports_pause_and_problem`: same arrangement as `Project_problem_and_pause_are_reported`, through `LoadShellAsync`.
  - `Card_carries_its_stage_position`: on `LoadBoardAsync(null)`, ORD-1 at plan → `(2, 4)`, ORD-2 at implement → `(3, 4)`, a Done ticket at done → `(null, null)`, a ticket at stage `"nope"` → `(null, null)`.
  - `Columns_carry_their_stage_kind`: filtered `"orders"` → kinds `[Triage, Plan, Implement, Done]`; all-projects → each column's `Kind.ToString() == Id`; the unknown column's `Kind` is null.
  - `Detail_carries_the_project_pipeline`: `Pipeline.Select(s => s.Id)` equals `["triage", "plan", "implement", "done"]`, and `ProjectName == "Project orders"` (the `TestHome` name format, see `Pause_that_cannot_be_saved_shows_the_error`). For a ticket of a removed project, `Pipeline` is empty and `ProjectName` is the id.

- [ ] **Step 2: Run them and confirm they fail to compile** (missing members).

Run: `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~BoardQueryTests"`

- [ ] **Step 3: Implement.** In `ToCard`, compute the stage index once from `p.Pipeline`. `LoadShellAsync` lists all tickets (`store.ListAsync(null, ct)`), builds cards with `ToCard(t, project, new Dictionary<Guid, decimal>())` so `WaitingOnYou` keeps its reviewer rule, and groups them by project.

- [ ] **Step 4: Run the whole `BoardQueryTests` class and confirm it passes**, old tests included.

- [ ] **Step 5: Commit** — `feat(dashboard): shell model, stage positions and detail pipeline`

---

### Task 2: `DashboardChrome`

**Files:**
- Create: `src/Symphonist.Host/Dashboard/DashboardChrome.cs`
- Modify: `src/Symphonist.Host/Dashboard/DashboardSetup.cs` (`services.AddScoped<DashboardChrome>();` next to `BoardQuery`)
- Test: `tests/Symphonist.Host.Tests/Dashboard/DashboardChromeTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum StatusSource { Shell, Board, Ticket }

/// One per circuit: what pages tell the frame (status line, tab title, highlighted project).
public sealed class DashboardChrome
{
    public event Action? Changed;               // raised after every state change
    public string? PageLabel { get; }           // e.g. the ticket key; null on the board
    public string? CurrentProject { get; }      // project id to highlight; null = All projects
    public string? Message { get; }             // first non-null error in Shell, Board, Ticket order, else the command message
    public bool IsError { get; }                // Message is an error
    public void SetPage(string? label, string? project);
    public void ShowMessage(string message);    // the command message; latest wins
    public void SetError(StatusSource source, string? error); // null clears that source only
    public void ClearForNavigation();           // clears the command message and the Board and Ticket errors (not Shell)
}
```

- [ ] **Step 1: Write the failing tests:**
  - `Error_shows_in_place_of_the_command_message`: `ShowMessage("Poll requested")`, then `SetError(Board, "x")` → `Message == "x"`, `IsError`. Then `SetError(Board, null)` → `Message == "Poll requested"`, `!IsError`.
  - `Each_source_clears_only_its_own_error`: set Shell and Ticket errors, clear Ticket → `Message` is the Shell error.
  - `Navigation_clears_page_state_but_not_shell_errors`: message plus Board and Shell errors, then `ClearForNavigation()` → `Message` is the Shell error. With no Shell error → `Message` is null.
  - `Changes_raise_Changed`: count raises across `SetPage`, `ShowMessage`, `SetError`, `ClearForNavigation` → 4.

- [ ] **Step 2: Run them and confirm they fail.** Run: `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~DashboardChromeTests"`
- [ ] **Step 3: Implement.** No locking needed; it is only used on the circuit's sync context.
- [ ] **Step 4: Run the tests and confirm they pass.**
- [ ] **Step 5: Commit** — `feat(dashboard): chrome service for the shared frame`

---

### Task 3: The frame (title bar, sidebar, status line, tab title, phone menu) and the new tokens

**Files:**
- Modify: `src/Symphonist.Host/Components/Layout/MainLayout.razor`; create `MainLayout.razor.css`
- Create: `src/Symphonist.Host/Components/Shared/ProjectSidebar.razor` + `.razor.css`
- Modify: `src/Symphonist.Host/wwwroot/app.css` (tokens), `src/Symphonist.Host/Components/Shared/StatusLine.razor.css`
- Test: `tests/Symphonist.Host.Tests/Dashboard/MainLayoutTests.cs`

**Interfaces:**
- Consumes: `BoardQuery.LoadShellAsync`, `ShellModel`, `ShellProject` (Task 1); `DashboardChrome` (Task 2); `BoardNotifier.Changed`; `Orchestrator.RequestTick()`.
- Produces:
  - `ProjectSidebar` parameters: `ShellModel? Shell`, `string? CurrentProject`. Rows are `<a>` with `data-sidebar-project="<id>"` (`""` for All projects), `aria-current="page"` on the current row.
  - `public static string MainLayout.TabTitle(int waiting, string? label)` returns `"Symphonist"`, `"(2) Symphonist"`, `"ORD-1 · Symphonist"` or `"(2) ORD-1 · Symphonist"`. `MainLayout` renders `<PageTitle>@TabTitle(...)</PageTitle>`, and the pages stop rendering their own `PageTitle` (Tasks 5 and 7).
  - Title-bar count chips: `data-count="waiting|running|blocked"`, text like `2 waiting on you`, `3 running`, `1 blocked`.
  - `static string ProjectSidebar.ColourOf(string projectId)` picks from a fixed palette of 8 non-ochre, non-red colours using a stable hash (sum of chars or FNV-1a). Never `string.GetHashCode()`, which changes per process.

Layout behaviour:
- Load the shell in `OnInitializedAsync` and on every `BoardNotifier.Changed` (through `InvokeAsync`). On failure, keep the last shell and `Chrome.SetError(StatusSource.Shell, $"Could not refresh the projects: {e.Message}")`. A good load clears it.
- Subscribe to `Chrome.Changed` and re-render. Unsubscribe from both on dispose.
- "Poll now" calls `Orchestrator.RequestTick()`, then `Chrome.ShowMessage("Poll requested")`.
- Render `<StatusLine Message="@Chrome.Message" IsError="@Chrome.IsError" />` under the title bar, then `@Body`.
- The brand is a link to `/`, not a heading (`FocusOnNavigate` targets the page `h1`).
- Sidebar row content: colour square, name (one line, ellipsis), then a pill: the ochre `WaitingOnYou` count when above 0 (visually-hidden "waiting on you"), else the quiet `Active` count when above 0 (visually-hidden "active"), else nothing. A paused row is muted with a "Paused" tag. A problem row has a small caution mark with the visually-hidden text "has a problem" and a `title` holding the problem text. With no projects, a muted line reads "No projects configured".
- The All projects row shows the total waiting count, otherwise the total active count.
- The connection footer from the spec is dropped: there is no cheap source for the last poll time.
- Phone (≤ 40rem): a menu button (`aria-controls="sidebar"`, `aria-expanded`, accessible name "Projects") shows in the title bar. The sidebar is hidden unless the layout has the class `sidebar-open`. A backdrop button ("Close projects") and Escape on the sidebar both close it. `NavigationManager.LocationChanged` closes it too. Closing returns focus to the menu button (`ElementReference.FocusAsync`, failures ignored). At desktop width the menu button and backdrop are `display: none`.
- CSS: rework the `app.css` tokens to the Global Constraints values. Add `--topbar-bg`, `--topbar-fg`, `--topbar-edge`, `--sidebar-bg`, `--sidebar-active-bg`, `--sidebar-active-fg` and the lane tints, each with dark values in both dark blocks. Grid layout: title bar 56px across the top, sidebar 220px, content scrolls.

- [ ] **Step 1: Write the failing tests** in `MainLayoutTests`. Set up the fixture as in `BoardComponentTests` (orders + billing projects, `FakeTicketStore`, orchestrator, notifier), plus `AddScoped<DashboardChrome>()`. Set up the theme module from Task 4 loosely for now: `ctx.JSInterop.Mode = JSRuntimeMode.Loose`; Task 4 tightens it. Render with `ctx.Render<MainLayout>(p => p.Add(l => l.Body, b => b.AddContent(0, "page body")))`.
  - `Title_bar_counts_cover_every_project`: seed ORD-1 plan AwaitingApproval, BIL-1 implement Running, ORD-2 implement Blocked; `chrome.SetPage(null, "orders")`. Assert the `[data-count=waiting]` text contains `1 waiting on you`, running `1 running`, blocked `1 blocked`.
  - `Sidebar_lists_projects_and_marks_the_current_one`: `SetPage("ORD-1", "orders")`. Assert the rows' `data-sidebar-project` values are `["", "billing", "orders"]`, the orders row has `aria-current="page"` and `href="/?project=orders"`, and the `""` row has `href="/"`.
  - `Sidebar_shows_waiting_else_active_counts`: orders has 1 waiting; billing has 2 Running and 0 waiting. Orders' pill text contains `1` plus `waiting on you`; billing's contains `2` plus `active`.
  - `Paused_and_problem_projects_are_marked`: `registry.SetPaused("orders", true)` and a billing problem (arranged as in `Project_problem_and_pause_are_reported`). The orders row contains `Paused`; the billing row contains `has a problem`.
  - `No_projects_shows_an_empty_sidebar_and_zero_counts` (Review Focus 4): delete the project files and `registry.Reload()`. The text contains `No projects configured` and the waiting chip text contains `0 waiting on you`.
  - `Poll_now_shows_its_message` (replaces `Empty_status_line_takes_no_space` in `BoardComponentTests`): the status line has `status-line-empty`; click "Poll now"; the status text is `Poll requested` and the class is gone.
  - `Shell_refresh_failure_is_shown_and_clears` (Review Focus 5): `store.FailNextList = true`, `notifier.Notify()`, `time.Advance(BoardNotifier.Window)`. The status contains `Could not refresh the projects: database is locked`, the sidebar rows are still present and `page body` is still rendered. Notify again and the error is gone.
  - `Long_project_name_stays_on_one_line` (Review Focus 1): a project named with 60 `x`. Its name element has class `project-name`, the class `ProjectSidebar.razor.css` gives `white-space: nowrap; overflow: hidden; text-overflow: ellipsis`. Assert the class is present; the visual check is in Task 8.
  - `Phone_menu_opens_and_closes`: clicking the button sets `aria-expanded="true"` and adds `sidebar-open`; clicking the backdrop removes them.
  - `Tab_title_formats` (pure): the four `TabTitle` cases listed in Interfaces.
- [ ] **Step 2: Run them and confirm they fail.** Run: `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~MainLayoutTests"`
- [ ] **Step 3: Implement** `ProjectSidebar`, `MainLayout`, and the CSS and token rework.
- [ ] **Step 4: Run `MainLayoutTests` and confirm it passes, then run the full suite.** Expect `BoardComponentTests` failures only where Task 5 changes them (status line, poll, header). Note them and do not fix them here.
- [ ] **Step 5: Commit** — `feat(dashboard): app frame with project sidebar and title bar`

---

### Task 4: Light/dark toggle

**Files:**
- Create: `src/Symphonist.Host/wwwroot/js/theme-init.js`, `src/Symphonist.Host/wwwroot/js/theme.js`
- Modify: `src/Symphonist.Host/Components/App.razor` (in `<head>`, before the stylesheets: `<script src="@Assets["js/theme-init.js"]"></script>`), `MainLayout.razor`, `app.css`
- Test: `tests/Symphonist.Host.Tests/Dashboard/MainLayoutTests.cs`

**Interfaces:**
- Produces:
  - `theme-init.js` (classic script): if `localStorage["symphonist.theme"]` is `"light"` or `"dark"`, set `document.documentElement.dataset.theme` to it. Wrapped in try/catch.
  - `theme.js` (module): `export function current()` returns `"light"` or `"dark"` (the `data-theme` value, else `matchMedia("(prefers-color-scheme: dark)")`). `export function set(theme)` sets `data-theme`, stores it, and returns `true`, or `false` if storage refused (the attribute is still set). Neither function throws.
  - The toggle button in the title bar has `data-theme-toggle`. Its accessible name is "Switch to dark mode" when the current theme is light, "Switch to light mode" when dark, and "Switch light or dark mode" until known.

- [ ] **Step 1: Write the failing tests** (switch `MainLayoutTests` to strict interop and set up `./js/theme.js` in the fixture):
  - `Theme_toggle_reads_and_sets_the_theme`: `current` returns `"light"`. After first render the button's `aria-label` is `Switch to dark mode`. Click → `set` was invoked with `"dark"`, and the label is `Switch to light mode`.
  - `Theme_toggle_survives_a_failing_browser` (Review Focus 2): `current` throws a `JSException`. The button renders with `Switch light or dark mode`, and clicking it neither throws nor breaks rendering (`page body` still present).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Load the module in `OnAfterRenderAsync(firstRender)`. Catch everything and log at Debug, like `Board.LoadFoldsAsync`. Dispose the module reference in `DisposeAsync`. In `app.css`, duplicate the dark token block under `:root[data-theme="dark"]` and guard the media block with `:root:not([data-theme="light"])`.
- [ ] **Step 4: Run `MainLayoutTests` and confirm it passes.**
- [ ] **Step 5: Commit** — `feat(dashboard): light and dark toggle`

---

### Task 5: Board page content

**Files:**
- Modify: `src/Symphonist.Host/Components/Pages/Board.razor`, `src/Symphonist.Host/wwwroot/app.css` (board section)
- Rename: `src/Symphonist.Host/Components/Shared/ProjectBar.razor`(+`.css`) → `ProjectHeading.razor`(+`.css`)
- Test: `tests/Symphonist.Host.Tests/Dashboard/BoardComponentTests.cs`

**Interfaces:**
- Consumes: `DashboardChrome` (Task 2), `BoardColumn.Kind` (Task 1).
- Produces: `ProjectHeading` parameters `BoardProject Project`, `EventCallback OnTogglePause`. Its root keeps `data-project="<id>"` and contains the page `<h1>` (the project name).

Changes:
- Remove the `<header class="board-header">`, the project `<select>`, "Poll now", `<StatusLine>` and `<PageTitle>`.
- `OnInitialized`: call `Chrome.ClearForNavigation()`. On every load, call `Chrome.SetPage(null, model.ProjectFilter)`. A load error goes to `Chrome.SetError(StatusSource.Board, …)` with today's text and is cleared on success. Command messages (pause, approve) go to `Chrome.ShowMessage`. `StillWaiting` keeps using the board's own last load error: keep a private `boardError` field next to the chrome call.
- Filtered view: `<ProjectHeading>` with badges (source, `Reviewer: mode`, host mode), the agreement line, Pause/Resume, and the Problem notice.
- All-projects view: `<h1>All projects</h1>`, then one `<p class="project-problem" data-problem="<id>"><strong>@Name:</strong> @Problem</p>` per project with a problem. No per-project rows.
- Columns: add the class `lane-@Kind.ToString().ToLowerInvariant()` when `Kind` is set. The header shows a stage-colour mark, the title and a count pill. Lane tints in `app.css` use one variable per kind (`--stage`, `--lane-tint`): triage grey, plan violet, implement blue, build/uat/release teal, deploy/done green, with dark variants.
- Setup hint, folding, stored folds, "+N older" and "No tickets" are unchanged.

- [ ] **Step 1: Update and add tests:**
  - Register `AddScoped<DashboardChrome>()` in the constructor. Change the `StatusLine(cut)` helper to return `chrome.Message ?? ""` (the resolved service), and use it everywhere the old helper was used.
  - Delete `Empty_status_line_takes_no_space` (moved to `MainLayoutTests` in Task 3).
  - `Project_bar_shows_agreement_numbers`, `Paused_project_offers_resume`, `Pause_that_cannot_be_saved_shows_the_error` and `Host_mode_project_is_marked` render `/?project=<id>` for the project they inspect. Host mode is asserted on billing, and its absence on a separate `/?project=orders` render.
  - `Filter_shows_one_project_pipeline`: unchanged.
  - New `All_projects_view_has_a_heading_and_problem_notices`: with a billing problem, the `h1` text is `All projects`, `[data-problem='billing']` contains the problem, and there is no `[data-project]` element.
  - New `Filtered_view_heading_is_the_project_name`: `/?project=orders` → the `h1` text is `Project orders`, and `chrome.CurrentProject == "orders"`.
  - New `Columns_carry_lane_classes`: `/?project=orders` → `[data-column='plan']` has the class `lane-plan`.
  - New `Board_has_no_project_dropdown`: `cut.FindAll("select")` is empty.
- [ ] **Step 2: Run them and confirm the new and changed tests fail.** Run: `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~BoardComponentTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `BoardComponentTests` and confirm it passes.**
- [ ] **Step 5: Commit** — `feat(dashboard): board content under the new frame`

---

### Task 6: Ticket card restyle with stage progress

**Files:**
- Modify: `src/Symphonist.Host/Components/Shared/TicketCard.razor` + `.razor.css`, `Board.razor` (drop the `From` argument)
- Test: `tests/Symphonist.Host.Tests/Dashboard/BoardComponentTests.cs`

**Interfaces:**
- Consumes: `BoardCard.StageNumber`, `BoardCard.StageCount` (Task 1).
- Produces: the card markup keeps `data-key`, `data-project-card`, `a.card-key` and the `badge-risk-*` classes (via `RiskBadge`). New: `[data-stage-progress]` holding the text `Stage N of M` and the elapsed text. The fill element is `.progress-fill` with `style="width: P%"`, where P is `round(100 * N / M)`. The `From` parameter is removed. `DetailUrl` is `/tickets/{project}/{key}`.

Layout of a card, top to bottom:
1. Key link, `RiskBadge`, then the status at the right.
2. Title.
3. Project name (all-projects board only).
4. Progress row, when `StageNumber` is set: "Stage N of M" plus the elapsed text ("waiting 14m"), then the bar.
5. Blocked reason, as today.
6. Footer, only when non-empty: attempts, cost, and `PR` at the right. When there is no progress row, the elapsed text goes here instead.
7. Approve button.

The `.badge` with `Card.Source` is removed from the card. Waiting and blocked keep their 4px inset rails. Running keeps its pulse.

- [ ] **Step 1: Update and add tests:**
  - Replace `Filtered_board_links_carry_the_filter_to_the_detail_page` with `Card_links_to_the_ticket_page`: the href is `/tickets/billing/BIL-1` on `/?project=billing`.
  - New `Card_shows_stage_progress`: ORD-1 at plan, AwaitingApproval since `Now`, clock at `Now + 14m` → the `[data-stage-progress]` text contains `Stage 2 of 4` and `waiting 14m`; `.progress-fill` style contains `width: 50%`.
  - New `Unknown_stage_card_has_no_progress_but_keeps_elapsed`: stage `"nope"`, Running → no `[data-stage-progress]`, and the card text contains `running`.
  - New `Long_title_wraps_inside_the_card` (Review Focus 1): a 200-char unbroken title → the `.card-title` element exists. Its CSS keeps `overflow-wrap: anywhere`; the visual check is in Task 8.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Enumerate the meta items once per render (it is a deferred item today).
- [ ] **Step 4: Run `BoardComponentTests` and confirm it passes.**
- [ ] **Step 5: Commit** — `feat(dashboard): ticket card with stage progress`

---

### Task 7: Ticket detail page

**Files:**
- Modify: `src/Symphonist.Host/Components/Pages/TicketDetail.razor` + `.razor.css`
- Create: `src/Symphonist.Host/Components/Shared/StageStepper.razor` + `.razor.css`
- Test: `tests/Symphonist.Host.Tests/Dashboard/TicketDetailComponentTests.cs`

**Interfaces:**
- Consumes: `TicketDetailModel.ProjectName`, `TicketDetailModel.Pipeline`, `PipelineStage` (Task 1); `DashboardChrome` (Task 2).
- Produces: `StageStepper` parameters `IReadOnlyList<PipelineStage> Pipeline`, `string CurrentStageId`, `StageStatus Status`, `bool WaitingOnYou`. Its root is `<ol class="stepper" aria-label="Pipeline">`. Each `<li data-step="<id>">` has the class `step-done`, `step-current` or `step-upcoming`. The current one also has `step-waiting`, `step-blocked` or `step-running`, and `aria-current="step"`. Gated stages (`Gate != None`) show the text `gate`. The current gated stage shows `gate · your approval` when `WaitingOnYou`. When the current stage is the Done kind, every step is `step-done`.

Page structure:
- `<nav class="crumbs" aria-label="Breadcrumb">` with `<a href="/?project=<id>">@ProjectName</a> / KEY`. When the model is null (loading or not found), the link text falls back to the project id. Remove the `.back` paragraph and the `From` query parameter.
- Summary panel: the class `summary-waiting` when waiting on you, `summary-blocked` when blocked. It holds the facts row (key, source badge, status `data-status`, risk, attempts, cost, elapsed), `<h1>` (the title only; the key is in the facts row), `<StageStepper>` when `Pipeline.Count > 0`, then the `data-blocked-reason` and `data-feedback` paragraphs.
- Below it, `<div class="detail-grid">` with `<div class="detail-history">` (gate reviews, agent runs, events timeline) and `<aside class="detail-rail">` (the Actions panel when any action is allowed, and the Links panel). At ≤ 40rem the grid is one column with the rail first after the summary (CSS `order`).
- Remove `<StatusLine>` and `<PageTitle>`. When `loadedFor` changes, call `Chrome.ClearForNavigation()` and `Chrome.SetPage(Key, Project)`. Refresh errors go to `Chrome.SetError(StatusSource.Ticket, …)` with today's text. Command and copy messages go to `Chrome.ShowMessage`. The `moved` check in `RunAsync` keeps a private `ticketError` field.
- Keep every existing `data-*` hook (`data-ticket-loaded`, `data-gate-review`, `data-run`, `data-event`, `data-reasons`, …).

- [ ] **Step 1: Update and add tests:**
  - Register `AddScoped<DashboardChrome>()`. Change the `StatusLine(cut)` helper to `chrome.Message ?? ""`.
  - `Missing_ticket_shows_not_found`: assert the `.crumbs a` href is `/?project=orders` (was `.back a` = `/`).
  - Replace `Back_link_keeps_the_board_filter` with `Breadcrumb_links_to_the_ticket_project`: the `.crumbs a` href is `/?project=orders`, its text is `Project orders`, and `chrome.CurrentProject == "orders"` and `chrome.PageLabel == "ORD-1"`.
  - New `Stepper_marks_done_current_and_upcoming`: ORD-1 at implement, AwaitingApproval → triage and plan are `step-done`, implement is `step-current step-waiting` with `aria-current="step"` and contains `gate · your approval`, done is `step-upcoming` with no `gate` text (the fixture gates triage, plan and implement as `human`; done has no gate).
  - New `Unknown_project_has_no_stepper_and_falls_back_to_the_id` (Review Focus 3): seed a ticket for project `gone`, which has no config, and render `/tickets/gone/GONE-1` → no `.stepper`, and the `.crumbs a` text is `gone`.
  - New `Actions_and_links_sit_in_the_rail`: AwaitingApproval → the Approve button is inside `aside.detail-rail`.
  - New `Long_title_and_key_wrap` (Review Focus 1): a 200-char title → `h1` exists; the CSS has `overflow-wrap: anywhere` on `h1` and `.crumbs`.
- [ ] **Step 2: Run them and confirm they fail.** Run: `dotnet test tests/Symphonist.Host.Tests --filter "FullyQualifiedName~TicketDetailComponentTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `TicketDetailComponentTests` and confirm it passes, then the full suite.**
- [ ] **Step 5: Commit** — `feat(dashboard): ticket detail page under the new frame`

---

### Task 8: Finish: reconnect modal, leftovers, browser check

**Files:**
- Modify: `src/Symphonist.Host/wwwroot/app.css` (reconnect modal on the new tokens; delete rules no markup uses any more: `.board-header`, `.counts`, `.board-actions`, `.filter`, `.projects`, `.back`, …)
- Modify: `docs/superpowers/notes/2026-10-23-dashboard-deferred.md` (strike "`TicketCard.Meta()` is enumerated twice" and "If the first board load fails, the header (and "Poll now") is hidden", which this work fixed)

- [ ] **Step 1: Search for dead selectors.** For each selector in `app.css` and the `.razor.css` files, grep `src/Symphonist.Host/Components` for the class, and delete the rules nothing uses.
- [ ] **Step 2: Run the full suite and confirm it passes.** Run: `dotnet test` — expect all green.
- [ ] **Step 3: Check it in a browser.** Run the host (see `README.md`, "symphonist dashboard" prints a sign-in link) with the sample project and a few seeded tickets. Check at 1440px and at 375px, in light and dark (with the toggle, and with no stored choice following the OS):
  - The title bar is clearly set apart.
  - Sidebar selection changes the board, and the ticket page highlights its project.
  - The phone menu opens, closes with Escape, and returns focus.
  - A 60-character project name truncates. Long titles wrap.
  - Waiting cards are the only ochre on the page.
  - Folding still works on the phone.
  - Approve, send back and pause messages appear under the title bar.
  - No horizontal page scroll.

  Take screenshots of board and detail in both themes for the review.
- [ ] **Step 4: Commit** — `chore(dashboard): drop unused styles, restyle reconnect modal`
