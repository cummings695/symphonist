# Dashboard redesign

Date: 2026-10-24
Branch: `dashboard-redesign`
Builds on: `docs/superpowers/plans/2026-10-22-symphonist-dashboard.md` (the current dashboard).
Mockups (local, git-ignored): `.superpowers/brainstorm/675-1792855284/content/layout-merged.html` (board) and
`detail-page.html` (ticket page).

## Intent

The developer keeps the Symphonist board open all day. Today it works, but the page reads as one flat, continuous
surface: a plain heading, a project dropdown, stacked project rows and grey columns. The goal is a board that is
pleasant to look at for hours and easier to move around in, with a clear app frame (a title bar that stands apart, a
sidebar of projects) around a calmer, roomier board. The visual reference was a modern kanban admin template (tinted
lanes, rounded cards, progress bars, coloured top bar); the cues were taken from it, not its orange or its data.

## Goals

- A dark slate **title bar** that clearly separates the frame from the content.
- A **sidebar listing the projects**, replacing the project dropdown.
- A softer, more modern **board**: tinted stage lanes, rounded cards with light shadow, a stage-progress bar on cards.
- The **ticket detail page** gets the same frame and a restyled layout.
- Both light and dark mode look deliberate, with a manual light/dark toggle.

## Non-goals

- No change to what the orchestrator does, to commands (approve, send back, unblock, pause, poll), or to auth.
- No new pages (no settings, no history, no charts).
- No drag and drop. Tickets move through the pipeline, not by hand.
- No CSS framework, no JS build step, no icon font or webfont downloads (the host serves only its own assets).

## Anti-goals (a failure even if it works)

- Losing the one-loud-colour rule: **ochre means "waiting on you"** and nothing else may compete with it. The new teal
  accent stays quiet (links, active sidebar row, the title-bar edge, done steps). Red stays for blocked.
- Status shown by colour alone. Every status, risk and count is still spelled out in text.
- Regressing what works: live updates, approve flow and its messages, phone-width folding of stages, screen-reader
  announcements (`StatusLine` live region), keyboard focus, `?project=` deep links, a waiting count in the tab title.

## Constraints

- Blazor (.NET 10) with global `InteractiveServer` render mode (`App.razor`: `<Routes @rendermode="InteractiveServer" />`),
  so `MainLayout` is interactive and survives navigation between pages.
- Styling is hand-written CSS: global tokens and shared rules in `wwwroot/app.css`, component rules in `*.razor.css`.
- Existing bUnit tests in `tests/Symphonist.Host.Tests/Dashboard/` pin markup (`data-*` hooks, classes, labels). Keep the
  `data-*` hooks; tests that look for header controls inside `Board` move to the layout.
- Phone width breakpoint stays `40rem`.

## Approaches considered

1. **The layout owns the frame (chosen).** `MainLayout` renders the title bar and sidebar, loads projects and counts
   itself and subscribes to `BoardNotifier`. Pages render only their content. The frame persists across navigation (no
   flicker, single source). Costs: "Poll now" and command messages move out of `Board.razor`; on a change both the
   layout and the page reload (cheap on a local host); some tests move.
2. **Each page renders a shared frame component.** Smaller change to page responsibilities, but the detail page needs an
   extra projects/counts query, the frame re-renders on every navigation, and frame wiring is duplicated.
3. **CSS framework (Tailwind/Bootstrap).** Rejected: adds a JS build step to a .NET project with none, and discards the
   existing token system that already handles dark mode and the ochre rule.

The developer picked 1.

## Design

### Visual system

Light mode is C ("light & airy") from the mockups with A's dark title bar. Tokens are reworked in `app.css`; names
may change, but every colour comes from a `:root` token with a dark-mode value.

- **Title bar:** background slate `#1e2733`, light text, a 3px teal bottom edge (`#14b8a6`), soft drop shadow. Dark
  mode: near-black `#0b0f14`, same teal edge.
- **Accent:** teal (`#0f766e` light / `#4fd1c0` dark) replaces today's blue `--accent` for links, keys, active states.
- **Page background** `#f6f7f9`; **surfaces** white; dark mode keeps today's dark palette as the base.
- **Lanes:** each stage column has a faint tint and a matching stage colour used for the progress fill. Tints are
  assigned by `StageKind` so every project's pipeline looks consistent (e.g. triage grey, plan violet, implement blue,
  build/release teal, deploy/done green; exact values are the builder's). Dark mode uses dark tints.
- **Radii:** lanes ~14px, cards and panels ~12px, buttons ~8px. Cards: no border in light mode, a very light shadow.
- **Waiting / blocked cards:** keep the inset 4px ochre (waiting) or red (blocked) left rail and the warm tint for
  waiting.
- Fonts stay the system stack and the existing monospace stack.

### The frame (`MainLayout`)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ♪ Symphonist   [● 2 waiting on you] [3 running] [1 blocked]  Poll now  ◐ │  slate, teal edge
├───────────────┬──────────────────────────────────────────────────────────┤
│ PROJECTS      │  page content                                            │
│ All projects 2│                                                          │
│ ▪ Orders     2│                                                          │
│ ▪ Billing    4│                                                          │
│ ▪ Website  ⏸ │                                                          │
│ …             │                                                          │
│ Host connected│                                                          │
└───────────────┴──────────────────────────────────────────────────────────┘
```

- **Title bar:** logo and "Symphonist" (links to `/`), the three counts as chips, "Poll now", and the theme toggle.
  The waiting chip is ochre when the count is above 0 and quiet otherwise; blocked uses red text when above 0. Counts are
  **totals across all projects** (it is global chrome), not the filtered view. *(My call; today's counts follow the
  filter.)*
- **Sidebar:** "All projects" then every configured project (ordered as today, by id). Each row: a small colour square,
  the name, and a count pill: the project's waiting-on-you count in ochre when above 0, otherwise a quiet count of its
  active (not Done) tickets, hidden when that is 0 too. *(My call; matches the mockup's quiet "4".)*
  A paused project is muted with a "Paused" tag. A project with a `Problem` shows a small caution marker (text
  available to screen readers). Rows are links: `/` and `/?project=<id>`. The current one is highlighted (teal tint)
  and has `aria-current="page"`. On the ticket page the ticket's project is highlighted. The project colour square is
  derived from the project id (stable hash into a small palette). *(My call.)*
- **Sidebar footer:** connection and freshness line, e.g. "Polled 1m ago" from the last notifier change (or "Host
  connected" when no time is known). *(My call; drop it if there is no cheap source of truth.)*
- **No projects configured:** the sidebar shows the label and an empty note; the board shows today's setup hint.
- **Status line:** `StatusLine` moves into the layout, just under the title bar, so "Poll requested", approve outcomes
  and refresh errors appear in one place on both pages. It stays a `role="status"` live region. Pages report to it
  through a small scoped service (e.g. `DashboardStatus`, with a `Changed` event) that keeps today's rules, per source
  *(my call)*:
  - It holds one **command message** (latest wins) and a **refresh error per source** (shell, board, ticket).
  - A refresh error that is still true is shown in place of the command message, as today (`refreshError ?? commandMessage`).
  - Each source clears only its own error when its next load succeeds.
  - The command message and the page sources' errors clear on navigation to another page, as leaving a page does today.
- **Tab title:** owned by the layout and based on the **global** waiting count, so it always matches the title bar:
  `(N) Symphonist` on the board and `(N) KEY · Symphonist` on the ticket page (no `(N)` when 0). *(My call; today the
  board's count follows the filter and the ticket page has no count.)*
- **Theme toggle:** sets `data-theme="light|dark"` on `<html>` and stores the choice in `localStorage`
  (`symphonist.theme`). Without a stored choice the OS setting applies as today. CSS: dark tokens under
  `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {…} }` and `:root[data-theme="dark"] {…}`.
  The stored choice is applied by a small script file loaded in `App.razor`'s `<head>` (not inline; the CSP may
  forbid inline scripts) so the page does not flash the wrong theme. The button has an accessible name ("Switch to dark mode" / "Switch to light mode"). Storage failures are
  ignored, like `board-folds.js`.
- **Phone width (≤ 40rem):** the sidebar collapses behind a menu button in the title bar (opens as an overlay panel,
  closes on selection, Escape, or tapping outside, and returns focus to the button). The count chips may shrink to
  numbers with their text kept for screen readers. Columns keep stacking and folding as today.

**Data:** add `BoardQuery.LoadShellAsync(ct)` returning, for every configured project, id, name, paused, problem,
waiting-on-you count, plus totals (waiting, running, blocked). The layout loads it on first render and on every
`BoardNotifier.Changed`, and after pause/resume. `BoardModel.Counts` can stay for the board's own use or be dropped if
unused.

### Board page (`Board.razor`)

- The page heading (`<h1>`) is the project name, or "All projects". It stays the focus target for `FocusOnNavigate`.
- **Project heading (filtered view):** name, source badge, "Reviewer: mode" badge, "host mode" caution badge, the
  reviewer-agreement line (today's `ProjectBar` content), the Pause/Resume button, and the Problem notice under it.
  `ProjectBar` is reworked into this heading.
- **All-projects view:** heading "All projects". Pause/Resume for each project is reached by selecting it in the
  sidebar. Problems still show: one compact notice per project with a problem above the columns (project name and
  problem). *(My call; today every project's bar is stacked here.)*
- The project dropdown is removed. Filtering is the sidebar plus the `?project=` query, unchanged.
- **Columns:** tinted lanes, header with a stage-colour mark, the title and a count pill. "+N older" and "No tickets"
  stay. Phone folding and stored folds are unchanged.
- **Cards** (`TicketCard`):
  - Top row: key (link to detail), risk pill, status with dot at the right ("Waiting on you", "Running" with its pulse,
    "Blocked", …). The source badge moves off the card (it is on the project heading); on the all-projects board the
    project name stays on the card.
  - Title.
  - **Stage progress:** "Stage N of M" and the elapsed time ("waiting 14m", "running 3m"), above a thin bar filled to
    N/M in the lane's stage colour. N is the card's stage position in *its own project's* pipeline (1-based), M the
    pipeline length. Blocked cards keep the bar (it shows where the ticket is stuck). Cards with an unknown stage or
    project, and Done cards, show no bar. *(My call; the mockup's blocked card simply omitted it.)*
  - Footer row (only when there is something to show): attempts, cost, and the PR link at the right.
  - Blocked reason (truncated as today) and the Approve button as today.
- **Data:** add `StageNumber` and `StageCount` (nullable) to `BoardCard`, filled in `BoardQuery.ToCard` from the
  project's pipeline.

### Ticket detail page (`TicketDetail.razor`)

Same frame. Layout from `detail-page.html`:

- **Breadcrumb** "Project name / KEY" replaces "← Back to the board". The project crumb always links to the ticket's
  own project (`/?project=<id>`), matching the highlighted sidebar row; "All projects" in the sidebar is the way back
  to the unfiltered board. The `from` query is no longer needed for the back link. *(My call.)*
- **Summary panel** (ochre left rail when waiting on you, red when blocked): key, source badge, status pill, risk pill,
  attempts, cost, elapsed; the title as the `<h1>`; a **stage stepper** of the project's pipeline: done stages ticked
  in teal, the current stage highlighted (ochre ring when waiting on you, red when blocked, blue when running),
  later stages hollow; gated stages labelled ("gate", plus "your approval" on the current one when waiting). Below:
  the blocked reason and last feedback, as today.
- **Two columns** (desktop): left, the history: **Gate reviews** as bordered cards (stage, time, mode badge, risk,
  outcome; summary; Reasons / Checked / Risk areas lists), **Agent runs** as a cleaner table (same columns, Copy path
  kept), **Events** as a vertical timeline. Right rail (~300px): **Actions** panel (Approve, Unblock, send-back form,
  same rules as today) and **Links** panel (tracker issue, PR, release PR, branch, spec, plan).
- **Phone width:** one column; the Actions and Links panels come right after the summary panel, before the history.
- Empty states ("No gate reviews yet." etc.), not-found and loading text stay.
- **Data:** add the pipeline to `TicketDetailModel`: an ordered list of `(StageId, Kind, Gate)` for the ticket's project
  (empty when the project is unknown, in which case the stepper is hidden).

### Behaviour that must not change

Approve/unblock/send-back logic and messages, stale-approve protection, live refresh through `BoardNotifier`, the 30s
elapsed re-render, stored folds, a waiting count in the tab title (now global, see Tab title), all `data-*` test hooks, text never rendered as markup,
`h1` focus on navigation, and the reconnect modal (restyle it with the new tokens only).

## Testing

- Keep the existing bUnit tests passing; move header/pause/status assertions to a new layout test class.
- New tests: sidebar lists projects with waiting counts and marks the current one (`aria-current`); paused and problem
  markers; title-bar totals ignore the filter; stage progress text and N/M on cards (and none for unknown/done); stepper
  states on the detail page; theme toggle calls the module and survives a failing browser; status messages from both
  pages reach the layout's status line.
- `BoardQueryTests`: `LoadShellAsync` totals and per-project counts; `StageNumber`/`StageCount`; detail pipeline.
- Manual check in a browser at desktop and phone widths, light and dark, before calling it done.

## Left to the builder

- Pause/resume already reaches the layout: `Program.cs` wires `ProjectRegistry.PausedChanged` to `BoardNotifier.Notify`.
- Exact colour values within the stated palette, spacing, shadows, and the stage-kind tint mapping.
- Icon drawing (inline SVG or CSS shapes; no icon fonts).
- How the theme module avoids a flash of the wrong theme.
- Whether `ProjectBar` is renamed (e.g. `ProjectHeading`).
- Splitting the work into tasks and the order of moves (frame first is suggested).
