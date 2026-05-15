# Port GitHub Browser UX to Main Content Area

## TL;DR
> **Summary**: Move the GitHub issue/PR browser from the sidebar panel into a full-width main content page at `/github`, add a rich filter bar (labels, author, milestone, assignee, sort, expression field), and simplify the sidebar to show bookmarked repo links only.
> **Estimated Effort**: Large

## Context
### Original Request
Port the GitHub browser UX pattern from the predecessor app (Next.js/React at `weave-agent-fleet`) to the current Vue 3 app. The predecessor renders the full browser in the main content area with rich filtering; the current app crams everything into a narrow sidebar panel.

### Key Findings
- **UI library**: shadcn-vue is available with all needed primitives: `command`, `popover`, `dropdown-menu`, `tabs`, `badge`, `button`, `input`, `select`
- **Routing**: TanStack Router with file-based routes in `client/src/routes/`. No `/github` index route exists yet — only detail routes (`github.$owner.$repo.issues.$number.tsx` and `github.$owner.$repo.pulls.$number.tsx`)
- **Composables already exist**: `useGitHubIssues` already accepts full `IssueFilterState` (labels, milestone, assignee, author, sort, direction, search) and `milestones` for title→number resolution. `useGitHubLabels`, `useGitHubMilestones`, `useGitHubAssignees` metadata composables exist in `use-github-metadata.ts`
- **Types already exist**: `IssueFilterState`, `DEFAULT_ISSUE_FILTER`, `GitHubLabel`, `GitHubMilestone`, `GitHubAssignee` are all defined in `github-types.ts`
- **Plugin manifest**: GitHub plugin registers `sidebarItems` (rail icon) and `sidebarPanels` (the panel component). The manifest supports `routes` contribution but GitHub doesn't use it yet
- **Page pattern**: Pages are `.vue` SFCs in `client/src/components/pages/`, route files are `.tsx` in `client/src/routes/`
- **Predecessor filter-expression.ts**: Has `parseFilterExpression` and `serializeFilterExpression` — pure functions, directly portable to TypeScript with zero React dependency

## Objectives
### Core Objective
Replace the sidebar-only GitHub browsing experience with a full-width main content page that has rich filtering, while simplifying the sidebar to a bookmarked-repos nav list.

### Deliverables
- [x] New `/github` route with `GitHubBrowserPage.vue` as the main content page
- [x] Rich filter bar with label, author, milestone, assignee, sort, and expression field controls
- [x] Simplified `GitHubPanel.vue` showing only bookmarked repos as nav links + "Add Repo" button
- [x] Enhanced issue/PR row items with comment counts, inline labels, and click-to-navigate
- [x] Filter expression parser/serializer (`filter-expression.ts`) ported from predecessor

### Definition of Done
- [x] Navigating to `/github` renders the full browser page with repo selector, tabs, filter bar, and item list
- [x] Clicking a bookmarked repo in the sidebar navigates to `/github` and selects that repo
- [x] Filter controls (label, author, milestone, assignee, sort, expression field) update the issue list
- [x] Clicking an issue/PR row navigates to the existing detail page routes
- [x] `npm run lint` and `npm run type-check` pass
- [x] Existing detail page routes (`/github/$owner/$repo/issues/$number`, `/github/$owner/$repo/pulls/$number`) continue to work

### Guardrails (Must NOT)
- Must NOT change the existing detail page components (`GitHubWorkItemDetailPage.vue`, `GitHubIssuePage.vue`, `GitHubPullRequestPage.vue`)
- Must NOT add collapsible inline detail — rows navigate to detail pages only
- Must NOT remove the existing composables — build on top of them
- Must NOT change backend API endpoints

## TODOs

- [x] 1. **Port filter expression utilities**
  **What**: Create `filter-expression.ts` with `parseFilterExpression()` and `serializeFilterExpression()` functions, directly ported from predecessor's `src/integrations/github/lib/filter-expression.ts`. These are pure functions with zero framework dependency. Import `IssueFilterState` and `DEFAULT_ISSUE_FILTER` from the existing `github-types.ts`.
  **Files**: `client/src/plugins/builtin/github/lib/filter-expression.ts`
  **Acceptance**: Functions parse `is:open label:bug author:octocat search terms` into `IssueFilterState` and serialize back. Unit-testable.

- [x] 2. **Create filter bar sub-components**
  **What**: Create Vue 3 SFC equivalents of the predecessor's filter controls. Each uses shadcn-vue `Popover` + `Command` (for searchable dropdowns) or `DropdownMenu` (for sort). All are presentational — they receive data/selection via props and emit changes.

  **2a. LabelFilter.vue** — Multi-select label filter. Props: `labels: GitHubLabel[]`, `isLoading: boolean`, `selected: string[]`. Emits: `toggle(labelName: string)`. Uses `Popover` + `Command` with checkbox items and color dots. Port from predecessor's `filters/label-filter.tsx`.

  **2b. AuthorFilter.vue** — Single-select author filter. Props: `users: GitHubAssignee[]`, `isLoading: boolean`, `selected: string | null`. Emits: `select(author: string | null)`. Uses `Popover` + `Command` with avatar + login. Port from `filters/author-filter.tsx`.

  **2c. MilestoneFilter.vue** — Single-select milestone filter. Props: `milestones: GitHubMilestone[]`, `isLoading: boolean`, `selected: string | null`. Emits: `select(milestone: string | null)`. Includes "No milestone" option. Port from `filters/milestone-filter.tsx`.

  **2d. AssigneeFilter.vue** — Single-select assignee filter. Props: `assignees: GitHubAssignee[]`, `isLoading: boolean`, `selected: string | null`. Emits: `select(assignee: string | null)`. Includes "Unassigned" option. Port from `filters/assignee-filter.tsx`.

  **2e. SortControl.vue** — Sort dropdown. Props: `sort: "created" | "updated" | "comments"`, `direction: "asc" | "desc"`. Emits: `change(sort, direction)`. Uses `DropdownMenu` with radio items. Port from `filters/sort-control.tsx`.

  **2f. FilterExpressionField.vue** — Text input that displays serialized filter expression. Props: `filter: IssueFilterState`, `isSearching: boolean`. Emits: `change(filter: IssueFilterState)`. On blur/Enter, parses expression via `parseFilterExpression()` and emits the structured state. On focus, switches to editing mode. Has clear button. Port from `filter-expression-field.tsx`.

  **Files**: `client/src/plugins/builtin/github/components/filters/LabelFilter.vue`, `client/src/plugins/builtin/github/components/filters/AuthorFilter.vue`, `client/src/plugins/builtin/github/components/filters/MilestoneFilter.vue`, `client/src/plugins/builtin/github/components/filters/AssigneeFilter.vue`, `client/src/plugins/builtin/github/components/filters/SortControl.vue`, `client/src/plugins/builtin/github/components/FilterExpressionField.vue`
  **Acceptance**: Each component renders correctly in isolation with the shadcn-vue primitives available in the project.

- [x] 3. **Create IssueFilterBar.vue**
  **What**: Composite component that assembles the filter expression field, state toggle (Open/Closed buttons), and all filter dropdowns + sort control in a single bar. Props: `filter: IssueFilterState`, `isSearching: boolean`, `labels: GitHubLabel[]`, `labelsLoading: boolean`, `milestones: GitHubMilestone[]`, `milestonesLoading: boolean`, `assignees: GitHubAssignee[]`, `assigneesLoading: boolean`. Emits: `change(filter: IssueFilterState)`. Layout: expression field full-width on top row, then a row with Open/Closed toggle | separator | Label | Author | Milestone | Assignee | (spacer) | Sort. Port from predecessor's `issue-filter-bar.tsx`.
  **Files**: `client/src/plugins/builtin/github/components/IssueFilterBar.vue`
  **Acceptance**: Renders all filter controls; changing any control emits an updated `IssueFilterState`.

- [x] 4. **Enhance IssueItem.vue and PullRequestItem.vue for full-width layout**
  **What**: Update the existing item components to work well at full page width (not just sidebar width). Add comment count display (`MessageSquare` icon + count when `> 0`). Ensure labels render inline on the title row (they already do). Add `onLabelClick` emit so clicking a label can toggle it in the filter. Adjust font sizes and spacing for full-width context (slightly larger text, more padding). Keep click-to-navigate behavior. The existing `item` prop interfaces need `comments: number` added.
  **Files**: `client/src/plugins/builtin/github/IssueItem.vue`, `client/src/plugins/builtin/github/PullRequestItem.vue`
  **Acceptance**: Items display comment count; clicking a label emits `labelClick`; layout works at full page width.

- [x] 5. **Create GitHubBrowserPage.vue**
  **What**: The main content page component rendered at `/github`. Structure:
  - Page header with "GitHub" title + connected status pill + settings link
  - Repo selector bar (using shadcn-vue `Popover` + `Command`, ported from predecessor's `repo-selector.tsx` pattern but reusing existing `useGitHubRepos` and `useGitHubBookmarks` composables)
  - `Tabs` component (shadcn-vue) with "Issues" and "Pull Requests" tabs, each showing a count badge
  - Inside Issues tab: `IssueFilterBar` + issue list using `useGitHubIssues` with full `IssueFilterState` + metadata composables (`useGitHubLabels`, `useGitHubMilestones`, `useGitHubAssignees`) + load more + refresh
  - Inside PRs tab: state filter (open/closed) + PR list using `useGitHubPulls` + load more
  - Empty states, loading states, error states with retry
  - `handleLabelClick` callback that toggles label in the filter state

  This component extracts and recomposes most of the logic currently in `GitHubPanel.vue`, but for full-width rendering with the rich filter bar. The filter state is managed as a `shallowRef<IssueFilterState>` initialized to `DEFAULT_ISSUE_FILTER`.

  **Files**: `client/src/components/pages/GitHubBrowserPage.vue`
  **Acceptance**: Full browser renders with repo selector, tabs, filter bar, and paginated item lists.

- [x] 6. **Create /github route**
  **What**: Add a TanStack Router file route at `client/src/routes/github.tsx` that renders `GitHubBrowserPage.vue`. Follow the pattern of `repositories.tsx`:
  ```tsx
  import { createFileRoute } from "@tanstack/vue-router";
  import GitHubBrowserPage from "@/components/pages/GitHubBrowserPage.vue";
  export const Route = createFileRoute("/github")({
    component: GitHubBrowserPage,
  });
  ```
  **Files**: `client/src/routes/github.tsx`
  **Acceptance**: Navigating to `/github` renders the browser page.

- [x] 7. **Simplify GitHubPanel.vue to bookmarked-repos sidebar**
  **What**: Gut the current `GitHubPanel.vue` and replace it with a slim sidebar panel that shows:
  - "GitHub" header link (clicking navigates to `/github`)
  - "Add Repository" button (opens a dialog or navigates to `/github` to add)
  - List of bookmarked repos as nav links. Each link navigates to `/github?repo=owner/name` (or simply to `/github` and selects the repo). Use `useGitHubBookmarks` composable.
  - Context menu on each repo link with "Remove" option (calls `removeBookmark`)
  - "Not connected" message when GitHub is disconnected

  Port from predecessor's `github-panel.tsx` layout. Keep it minimal — no tabs, no search, no item lists.

  **Files**: `client/src/plugins/builtin/github/GitHubPanel.vue`
  **Acceptance**: Sidebar shows bookmarked repos as links; clicking one navigates to `/github`.

- [x] 8. **Wire up plugin manifest for the new route**
  **What**: The `/github` route is file-based so it auto-registers with TanStack Router. However, verify that the sidebar rail item's `defaultPath: "/github"` still works (it already points to `/github`). No manifest changes should be needed since the route is file-based and the sidebar item already has `defaultPath: "/github"`. If the route needs to be registered via the manifest's `routes` contribution instead, add it there.
  **Files**: `client/src/plugins/builtin/github/index.ts`
  **Acceptance**: Clicking the GitHub rail icon navigates to `/github` and renders the browser page.

## Verification
- [x] All existing detail page routes work unchanged
- [x] `/github` route renders full browser page with repo selector, tabs, filters, items
- [x] Sidebar panel shows bookmarked repos only
- [x] Filter expression field parses and serializes correctly
- [x] Label/Author/Milestone/Assignee filters update the issue list via API
- [x] `npm run lint` passes
- [x] `npm run type-check` passes
- [x] No regressions in other plugins
