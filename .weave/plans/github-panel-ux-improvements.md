# GitHub Panel UX Improvements

## TL;DR
> **Summary**: Add realtime repo filtering to the GitHub panel and move issues/PRs browsing into the center content area for better usability.
> **Estimated Effort**: Medium

## Context
### Original Request
Two improvements requested:
1. Realtime filtering of GitHub repositories via a filter box
2. Use the middle content area (instead of the 280px left ContextPanel) for browsing issues and pull requests

### Key Findings
- **Layout**: `AppShell.vue` → `IconRail` | `ContextPanel` (280px) | `CenterContent` (flex:1) | `RightPanel`
- **GitHub panel**: `src/plugins/builtin/github/GitHubPanel.vue` — a 758-line component rendered inside ContextPanel when sidebar rail = "github"
- **Repo selector**: Uses a native `<select>` with `<optgroup>` (Bookmarked / All). No text filter exists.
- **Issues/PRs**: Rendered as list items inside the same 280px panel — very cramped.
- **Routing**: `src/routes/repositories.tsx` exists but is separate from the plugin panel.
- **Plugin slot system**: `ContextPanel.vue` resolves panels via `getSidebarPanels()` keyed by `viewId`.

---

## Options

### Option A — Hybrid: Filter in Panel + Center Content Detail View (Recommended)

**Concept**: Keep the GitHub ContextPanel as a navigation sidebar (repo picker + filter + issue/PR list as compact links), but when a user clicks an issue/PR, render a rich detail view in CenterContent. Add a text input above the repo `<select>` that filters repos in realtime.

**Why recommended**: Lowest risk, preserves existing navigation mental model, doesn't require new routes or major layout changes. The panel stays useful as a quick-nav while the center area gets used for the content that actually needs space.

**Files to touch**:
- `src/plugins/builtin/github/GitHubPanel.vue` — add repo filter input, wire `computed` filter over `repoSelectorGroups`
- `src/plugins/builtin/github/composables/use-github-repos.ts` — no change needed (filtering is client-side)
- New: `src/routes/github.ts` — dedicated GitHub routes (NOT under /repositories)
- New: `src/components/pages/GitHubIssuePage.vue` — center content detail view
- New: `src/components/pages/GitHubPullRequestPage.vue` — center content detail view
- `src/plugins/builtin/github/IssueItem.vue` / `PullRequestItem.vue` — add `router.navigate` on click

**Implementation approach**:
1. Replace the native `<select>` with a filterable combobox/searchable picker (using a text input + dropdown list pattern) that filters `repoSelectorGroups` by substring match on `full_name`
2. Add dedicated GitHub routes: `/github/:owner/:repo/issues/:number` and `/github/:owner/:repo/pulls/:number` (NOT under `/repositories` which belongs to local repos)
3. On item click, navigate to that route → CenterContent renders the detail page

---

### Option B — Full Center Content Takeover

**Concept**: When the GitHub rail is active, render the entire GitHub experience (repo picker, filter, issue/PR list, detail) in CenterContent instead of ContextPanel. The ContextPanel either hides or shows a minimal repo tree.

**Files to touch**:
- `src/components/layout/ContextPanel.vue` — conditionally hide or show minimal view for `github` rail
- `src/stores/sidebar.ts` — possibly add a `centerOverride` concept
- New: `src/components/pages/GitHubBrowser.vue` — full-page component with split list/detail
- `src/plugins/builtin/github/GitHubPanel.vue` — extract logic into composable for reuse
- Router changes for `/github` route

**Trade-offs**: More disruptive, breaks the pattern other plugins follow, but gives maximum screen real estate. Risk of scope creep.

---

### Option C — Expandable Panel / Drawer

**Concept**: Keep everything in the ContextPanel but make it resizable/expandable (e.g., drag to widen, or a "pop out" button that expands it to ~500px). Add the repo filter input as in Option A.

**Files to touch**:
- `src/components/layout/ContextPanel.vue` — make width dynamic (CSS resize or drag handle)
- `src/plugins/builtin/github/GitHubPanel.vue` — add repo filter input
- `src/stores/sidebar.ts` — add `panelWidth` state

**Trade-offs**: Simplest change, but doesn't fundamentally solve the space problem. Users still browse in a panel rather than the main content area.

---

## Objectives
### Core Objective
Improve GitHub repo/issue/PR browsing UX with filtering and better use of screen space.

### Deliverables
- [x] Realtime text filter for repository selector
- [x] Issues/PRs viewable in center content area (or expanded panel)

### Definition of Done
- [x] Typing in repo filter narrows visible repos instantly
- [x] Clicking an issue/PR shows detail in center content (Option A) or expanded view
- [x] No regressions to existing panel functionality

### Guardrails (Must NOT)
- Must not break other plugin panels (Linear, Slack, etc.)
- Must not remove the ability to quickly switch repos from the sidebar
- Must not introduce new external dependencies

## TODOs

- [x] 1. Replace Repo Selector with Filterable Combobox
  **What**: Remove the native `<select>` and replace it with a searchable combobox component (text input + filtered dropdown list) that filters repos by substring match on `full_name`. Use a `repoFilterQuery` ref + computed over `repoSelectorGroups`.
  **Files**: `src/plugins/builtin/github/GitHubPanel.vue`
  **Acceptance**: Typing filters the repo list in realtime; clearing restores full list; selecting a repo works as before

- [x] 2. Create Center Content Detail Views
  **What**: Create page components for viewing a single issue or PR in the center content area
  **Files**: `src/components/pages/GitHubIssuePage.vue`, `src/components/pages/GitHubPullRequestPage.vue`
  **Acceptance**: Components render issue/PR title, body, labels, comments

- [x] 3. Add Dedicated GitHub Routes for Issue/PR Detail
  **What**: Register routes `/github/:owner/:repo/issues/:number` and `/github/:owner/:repo/pulls/:number` that render the detail pages in CenterContent. Do NOT use `/repositories` namespace (reserved for local repos).
  **Files**: `src/routes/github.ts`
  **Acceptance**: Navigating to `/github/octocat/hello-world/issues/1` renders the issue detail page

- [x] 4. Wire Panel Items to Navigate
  **What**: Make `IssueItem` and `PullRequestItem` clickable to navigate to the detail route
  **Files**: `src/plugins/builtin/github/IssueItem.vue`, `src/plugins/builtin/github/PullRequestItem.vue`
  **Acceptance**: Clicking an item navigates to center content detail view

## Verification
- [x] All tests pass
- [x] No regressions to other sidebar panels
- [x] Repo filter works with 0, 1, and many repos
- [x] Issue/PR detail renders correctly in center content
