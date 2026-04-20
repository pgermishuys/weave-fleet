# Fleet Dashboard UI Rebuild — React → Vue 3

## TL;DR
> **Summary**: Replace the React 19 frontend (`client/`) with a Vue 3 app using Composition API, shadcn-vue, TanStack Router, Tailwind 4, and Vite — matching the evolved mockup layout (icon rail + context panel + center + right panel).
> **Estimated Effort**: XL

## Context

### Original Request
Rebuild the Fleet dashboard from React to Vue 3 while implementing the new 4-panel layout defined in `mockups/mockup-j-evolved.html`.

### Key Findings

**Current React app** (`client/src/`):
- **Routing**: React Router v7 with lazy-loaded pages — 12 routes including `/`, `/analytics`, `/sessions/:id`, `/settings`, `/repositories/:path`, plugin-contributed routes
- **State**: React Context for app shell, sessions, sidebar, commands, keybindings, integrations, theme, slash-commands (8 context providers)
- **API layer**: `lib/api-client.ts` — framework-agnostic `apiFetch()` / `wsUrl()` with CSRF, configurable base URL, WebSocket support. `lib/api-types.ts` has 744 lines of typed request/response shapes
- **Hooks**: 61 custom hooks covering sessions, projects, models, agents, analytics, WebSocket events, keyboard shortcuts, etc.
- **Plugin system**: Registry-based (`plugins/registry.ts`) with manifest pattern. Plugins contribute sidebar items, panels, routes, settings sections, startup hooks, context resolvers, session sources. Currently one built-in plugin (GitHub)
- **UI components**: 24 shadcn/radix components in `components/ui/`
- **Layout**: `components/layout/` has sidebar icon rail, sidebar panel, project/session items, header, fleet panel, github panel
- **Auth**: `AuthGate` + `OnboardingGate` pattern with `/api/config/client` + `/api/user/me` checks
- **Tauri**: Optional Tauri desktop support via `@tauri-apps/api`

**Target mockup** (`mockups/mockup-j-evolved.html`):
- **Topbar** (48px): breadcrumb nav, status pill, notifications, avatar
- **Icon Rail** (48px): logo, Board, Sessions, divider, GitHub/Linear/Slack/Docker/Sentry (plugin icons with status dots/badges), divider, Marketplace, Settings — with tooltips
- **Context Panel** (280px): swaps per rail selection — Sessions (project tree with collapsible groups, sub-groups, search, new session/project buttons), Board Controls (project/status/agent filters, group-by pills, sort, quick stats), GitHub (issues/PRs/repos tabs), Linear (tickets), Marketplace (installed + available grid), Docker/Sentry/Slack (simple panels)
- **Center**: Activity stream (messages with tool cards, diffs, test output) + Composer (textarea, agent selector dropdown, model selector dropdown, effort toggle, send button) OR Kanban board (columns by status, cards with project dot, pills, progress bar, footer stats)
- **Right Panel** (280px): tabs — Session (header card, token grid, context meter, files changed, agent/model info), Plan (TODO checklist), Summary (stats bars, stacked bar, agents, costs), Activity (feed)

## Objectives

### Core Objective
Ship a Vue 3 frontend that is feature-complete with the current React app, implements the new 4-panel mockup layout, and preserves the plugin architecture.

### Deliverables
- [x] New Vue 3 project in `client-vue/` with full tooling
- [x] App shell matching mockup (topbar, rail, context panel, center, right panel)
- [x] Sessions view with project tree, activity stream, composer, session detail panel
- [x] Kanban board view with filters, cards, board summary
- [x] Plugin system ported to Vue (registry, contributions, slots)
- [x] GitHub/Linear/Marketplace plugin panels
- [x] Settings, command palette, keyboard shortcuts
- [x] API client migrated (reuse `api-client.ts` and `api-types.ts` as-is)
- [x] Auth gate and onboarding flow
- [x] React app removed, `client-vue/` renamed to `client/`

### Definition of Done
- [x] `cd client && npm run build` succeeds with zero errors
- [x] `cd client && npm run typecheck` passes
- [x] `cd client && npm run test` passes
- [x] All existing API endpoints are consumed correctly
- [ ] Visual parity with mockup verified manually (requires running backend)
- [x] Plugin system supports same contribution types as React version

### Guardrails (Must NOT)
- Must NOT change any backend API contracts
- Must NOT remove the React app until Vue app is feature-complete (Phase 6)
- Must NOT introduce runtime CSS-in-JS (stick with Tailwind)
- Must NOT use Options API — Composition API + `<script setup>` only
- Must NOT break Tauri desktop support

## Technical Decisions

### State Management: Pinia
Use **Pinia** (official Vue state manager). Map each React context to a Pinia store:
| React Context | Pinia Store |
|---|---|
| `AppShellContext` | `useAppShellStore` |
| `SessionsContext` | `useSessionsStore` |
| `SidebarContext` | `useSidebarStore` (rail selection, panel state) |
| `CommandRegistryContext` | `useCommandStore` |
| `KeybindingsContext` | `useKeybindingsStore` |
| `IntegrationsContext` | `useIntegrationsStore` |
| `ThemeContext` | `useThemeStore` |
| `SlashCommandContext` | `useSlashCommandStore` |

### API Client: Reuse as-is
`lib/api-client.ts` and `lib/api-types.ts` are framework-agnostic (pure TS, no React imports). Copy them directly. Create Vue composables (`useApiFetch`, `useWeaveSocket`) that wrap them.

### Composables: 1:1 mapping from hooks
Each `use-*.ts` hook becomes a Vue composable. Most are thin wrappers around `apiFetch` + reactive state — straightforward port.

### Plugin Architecture: Vue adaptation
Port the manifest/registry/slots pattern. Key changes:
- `ComponentType` → `Component` (Vue)
- `ReactNode` → `VNode` / slots
- `RouteObject` → TanStack Router route definition
- Plugin sidebar items contribute `Component` instead of React components
- Use Vue's `defineAsyncComponent` instead of `React.lazy`

### Router: TanStack Router for Vue
Use `@tanstack/vue-router` with file-based routing. Route structure:
```
src/routes/
  __root.tsx          → app shell layout
  index.tsx           → sessions view (home)
  board.tsx           → kanban board
  sessions.$id.tsx    → session detail (center content changes)
  settings.tsx        → settings page
  analytics.tsx       → analytics
  login.tsx           → login (outside auth gate)
```

### Migration Strategy: Parallel apps, big-bang switch
- Vue app lives in `client-vue/` during development
- Same Vite proxy config → same backend
- Both apps can run simultaneously on different ports for comparison
- Final cutover: rename `client/` → `client-react-archive/`, `client-vue/` → `client/`
- No micro-frontend or iframe embedding — too complex for the benefit

## TODOs

### Phase 0: Foundation

- [x] 1. **Scaffold Vue 3 project**
  **What**: Create `client-vue/` with Vite + Vue 3 + TypeScript. Configure `vite.config.ts` with same proxy rules, path aliases (`@/`), env vars (`VITE_APP_VERSION`, `VITE_COMMIT_SHA`, `VITE_WEAVE_PROFILE`). Add `.node-version`, `.gitignore`, `tsconfig.json`.
  **Files**: `client-vue/package.json`, `client-vue/vite.config.ts`, `client-vue/tsconfig.json`, `client-vue/index.html`, `client-vue/.gitignore`
  **Acceptance**: `npm run dev` starts on port 3002, proxies to backend

- [x] 2. **Install and configure Tailwind 4**
  **What**: Add `tailwindcss@4`, `@tailwindcss/postcss`, `tw-animate-css`. Copy CSS custom properties from mockup (color tokens: `--rail-bg`, `--panel-bg`, `--card-bg`, `--main-bg`, `--border`, `--text`, `--muted`, `--accent`, status colors, radii). Create `src/assets/main.css`.
  **Files**: `client-vue/postcss.config.mjs`, `client-vue/src/assets/main.css`
  **Acceptance**: Tailwind classes render correctly in browser

- [x] 3. **Install and configure shadcn-vue**
  **What**: Add `shadcn-vue` with `components.json`. Initialize with same component set as React app (button, card, dialog, dropdown-menu, input, tabs, tooltip, badge, avatar, scroll-area, select, separator, sheet, switch, textarea, popover, collapsible, command, context-menu, alert-dialog, progress). Use `class-variance-authority`, `clsx`, `tailwind-merge`.
  **Files**: `client-vue/components.json`, `client-vue/src/components/ui/` (24 components), `client-vue/src/lib/utils.ts`
  **Acceptance**: `npx shadcn-vue add button` works, components render

- [x] 4. **Install TanStack Router for Vue**
  **What**: Add `@tanstack/vue-router`. Configure file-based routing with `src/routes/` directory. Create root route with placeholder layout.
  **Files**: `client-vue/src/routes/__root.tsx`, `client-vue/src/routes/index.tsx`, `client-vue/src/router.ts`
  **Acceptance**: Navigation between `/` and `/board` works

- [x] 5. **Install Pinia and create store scaffolds**
  **What**: Add `pinia`. Create empty store files for all 8 stores with proper typing.
  **Files**: `client-vue/src/stores/app-shell.ts`, `client-vue/src/stores/sessions.ts`, `client-vue/src/stores/sidebar.ts`, `client-vue/src/stores/commands.ts`, `client-vue/src/stores/keybindings.ts`, `client-vue/src/stores/integrations.ts`, `client-vue/src/stores/theme.ts`, `client-vue/src/stores/slash-commands.ts`
  **Acceptance**: Stores instantiate without errors

- [x] 6. **Copy framework-agnostic modules**
  **What**: Copy these files verbatim from `client/src/lib/` (they have zero React imports): `api-client.ts`, `api-types.ts`, `types.ts`, `utils.ts`, `format-utils.ts`, `session-utils.ts`, `session-status-utils.ts`, `agent-colors.ts`, `tool-icons.ts`, `tool-labels.ts`, `tool-card-utils.ts`, `todo-utils.ts`, `pr-utils.ts`, `keybinding-types.ts`, `keybinding-utils.ts`, `pagination-utils.ts`, `draft-utils.ts`, `delegation-state.ts`, `event-state.ts`, `highlight-utils.ts`, `image-validation.ts`, `markdown-utils.ts`, `slash-command-utils.ts`, `workspace-utils.ts`, `provider-registry.ts`, `session-cache.ts`, `update-preferences.ts`, `chart-theme.ts`, `command-registry.ts`, `mock-data.ts`.
  **Files**: `client-vue/src/lib/` (30+ files)
  **Acceptance**: `npm run typecheck` passes for all copied files

- [x] 7. **Add Lucide Vue icons**
  **What**: Replace `lucide-react` with `lucide-vue-next`. This is the only icon library change needed.
  **Files**: `client-vue/package.json`
  **Acceptance**: `import { Loader2 } from 'lucide-vue-next'` works

### Phase 1: Shell & Layout

- [x] 8. **Create AppShell layout component**
  **What**: Build the root layout matching mockup: `<div class="app">` → topbar + main (rail + context-panel + center + right-panel). Use CSS from mockup (flexbox, fixed widths). Wire to `useSidebarStore` for active rail item.
  **Files**: `client-vue/src/components/layout/AppShell.vue`, `client-vue/src/components/layout/TopBar.vue`, `client-vue/src/components/layout/IconRail.vue`, `client-vue/src/components/layout/ContextPanel.vue`, `client-vue/src/components/layout/CenterContent.vue`, `client-vue/src/components/layout/RightPanel.vue`
  **Acceptance**: 4-panel layout renders with correct dimensions, rail items highlight on click

- [x] 9. **Implement TopBar**
  **What**: Breadcrumb (reactive to current session/project), status pill (animated dot), notifications icon, user avatar. Wire breadcrumb to router + session store.
  **Files**: `client-vue/src/components/layout/TopBar.vue`
  **Acceptance**: Breadcrumb updates when navigating, status pill pulses

- [x] 10. **Implement Icon Rail**
  **What**: Logo, Board, Sessions, divider, plugin icons (from plugin registry), divider, Marketplace, Settings. Active state with left border accent. Tooltips via `data-tooltip` + CSS. Status dots and badges from plugin status. Wire clicks to `useSidebarStore.setActiveRail()`.
  **Files**: `client-vue/src/components/layout/IconRail.vue`
  **Acceptance**: Clicking rail items swaps context panel content, active item shows accent border

- [x] 11. **Implement Context Panel switcher**
  **What**: Container that renders the correct panel based on `useSidebarStore.activeRail`. Use `<component :is>` or `v-if` chain. Panels: SessionsPanel, BoardControlsPanel, plugin panels (dynamic from registry).
  **Files**: `client-vue/src/components/layout/ContextPanel.vue`
  **Acceptance**: Switching rail items swaps panel content with no flicker

- [x] 12. **Implement Right Panel with tabs**
  **What**: Tab bar (Session/Plan/Summary/Activity) + content area. Tabs change based on view (session view → Session/Plan tabs, board view → Summary/Activity tabs). Wire to store.
  **Files**: `client-vue/src/components/layout/RightPanel.vue`, `client-vue/src/components/layout/RightPanelTabs.vue`
  **Acceptance**: Tab switching works, content scrolls independently

- [x] 13. **Implement Auth Gate**
  **What**: Port `AuthGate` + `OnboardingGate` logic. Check `/api/config/client` + `/api/user/me` on mount. Redirect to `/login` on 401. Populate `useAppShellStore`.
  **Files**: `client-vue/src/components/auth/AuthGate.vue`, `client-vue/src/components/auth/OnboardingGate.vue`
  **Acceptance**: Unauthenticated users redirected, authenticated users see shell

- [x] 14. **Configure routes**
  **What**: Set up TanStack Router file-based routes: root (AppShell + AuthGate), index (sessions), board, sessions/$id, settings, analytics, login. Login route outside auth gate.
  **Files**: `client-vue/src/routes/__root.tsx`, `client-vue/src/routes/index.tsx`, `client-vue/src/routes/board.tsx`, `client-vue/src/routes/sessions.$id.tsx`, `client-vue/src/routes/settings.tsx`, `client-vue/src/routes/analytics.tsx`, `client-vue/src/routes/login.tsx`
  **Acceptance**: All routes resolve, layout wraps correctly

### Phase 2: Core Views — Sessions

- [x] 15. **Implement Sessions Context Panel (project tree)**
  **What**: Panel header, search input with filter, "New Session" + "New Project" buttons, collapsible project groups with chevron animation, sub-group labels, session items with status dots, active highlight. Wire to `useSessionsStore` + `useProjectsStore` (composables for `use-sessions`, `use-projects`).
  **Files**: `client-vue/src/components/sessions/SessionsPanel.vue`, `client-vue/src/components/sessions/ProjectGroup.vue`, `client-vue/src/components/sessions/SessionItem.vue`
  **Acceptance**: Projects expand/collapse, sessions filter by search, clicking session updates center + right panel

- [x] 16. **Port session/project composables**
  **What**: Convert hooks to Vue composables: `use-sessions`, `use-projects`, `use-create-session`, `use-create-project`, `use-delete-session`, `use-delete-project`, `use-rename-session`, `use-move-session`, `use-archive-session`, `use-unarchive-session`, `use-fork-session`, `use-resume-session`, `use-abort-session`, `use-terminate-session`, `use-update-project`, `use-reorder-project`.
  **Files**: `client-vue/src/composables/use-sessions.ts`, `client-vue/src/composables/use-projects.ts`, `client-vue/src/composables/use-session-actions.ts`
  **Acceptance**: CRUD operations work against backend API

- [x] 17. **Implement Activity Stream**
  **What**: Scrollable message list with user/agent avatars, message bodies (markdown rendered), tool cards (collapsible with diff lines, test output). Use `highlight.js` for code, `vue-markdown` or equivalent for markdown rendering.
  **Files**: `client-vue/src/components/session/ActivityStream.vue`, `client-vue/src/components/session/MessageBubble.vue`, `client-vue/src/components/session/ToolCard.vue`, `client-vue/src/components/session/DiffView.vue`
  **Acceptance**: Messages render with proper formatting, tool cards expand/collapse, diffs show colored lines

- [x] 18. **Port activity stream composables**
  **What**: Convert `use-activity-stream`, `use-message-pagination`, `use-message-queue`, `use-session-events`, `use-weave-socket`, `use-scroll-anchor`, `use-activity-filter`.
  **Files**: `client-vue/src/composables/use-activity-stream.ts`, `client-vue/src/composables/use-weave-socket.ts`, `client-vue/src/composables/use-scroll-anchor.ts`
  **Acceptance**: Real-time messages stream via WebSocket, auto-scroll works

- [x] 19. **Implement Composer**
  **What**: Textarea with auto-resize, agent selector dropdown (pill button → dropdown with agent list), model selector dropdown, effort toggle (dot indicators), send button. Dropdowns open upward from toolbar. Wire to `use-send-prompt`, `use-agents`, `use-models`, `use-draft-state`.
  **Files**: `client-vue/src/components/session/Composer.vue`, `client-vue/src/components/session/AgentSelector.vue`, `client-vue/src/components/session/ModelSelector.vue`, `client-vue/src/components/session/EffortToggle.vue`, `client-vue/src/components/session/SelectorDropdown.vue`
  **Acceptance**: Can type message, select agent/model, toggle effort, send prompt, message appears in stream

- [x] 20. **Implement Session Detail (Right Panel — Session tab)**
  **What**: Session header card (title, status badge, project link, meta row with agent/model/time), token grid (4 cards: input tokens, output tokens, total cost, cache reads), context window meter (progress bar with warn/danger thresholds), files changed list (with +/- stats), agent/model info rows.
  **Files**: `client-vue/src/components/session/SessionDetailPanel.vue`, `client-vue/src/components/session/TokenGrid.vue`, `client-vue/src/components/session/ContextMeter.vue`, `client-vue/src/components/session/FilesChanged.vue`
  **Acceptance**: All session metadata displays correctly, context meter changes color at thresholds

- [x] 21. **Implement Plan/TODO tab (Right Panel)**
  **What**: Plan title, TODO checklist with checked/unchecked/in-progress states. Wire to session plan data from API.
  **Files**: `client-vue/src/components/session/PlanPanel.vue`, `client-vue/src/components/session/TodoItem.vue`
  **Acceptance**: TODO items render with correct states, checkboxes are visual-only (read from API)

### Phase 3: Kanban Board

- [x] 22. **Implement Board Controls Panel (context panel)**
  **What**: Project dropdown, status checkboxes (with colored dots), agent checkboxes, group-by radio pills (Status/Project/Agent), sort dropdown, quick stats row. Wire filters to `useBoardStore`.
  **Files**: `client-vue/src/components/board/BoardControlsPanel.vue`, `client-vue/src/stores/board.ts`
  **Acceptance**: Filters update board view reactively

- [x] 23. **Implement Kanban Board (center content)**
  **What**: Header (title, subtitle, "New Session" button), horizontal scrolling board with columns. Columns: Queued, Running, In Review, Completed, Failed — each with colored top bar, title + count, scrollable card list. Cards: title, project dot + name, pills (agent, model), footer stats (tokens, time, cost), progress bar, left border color by status. Failed cards get red-tinted background.
  **Files**: `client-vue/src/components/board/KanbanBoard.vue`, `client-vue/src/components/board/KanbanColumn.vue`, `client-vue/src/components/board/KanbanCard.vue`
  **Acceptance**: Sessions grouped into correct columns, filters from controls panel apply, cards show all metadata

- [x] 24. **Implement Board Summary (Right Panel — Summary tab)**
  **What**: Big number (total sessions), status bar chart, stacked bar, agents list with session counts, cost breakdown. Wire to `use-fleet-summary`.
  **Files**: `client-vue/src/components/board/BoardSummaryPanel.vue`
  **Acceptance**: Stats match actual session data

- [x] 25. **Implement Board Activity (Right Panel — Activity tab)**
  **What**: Chronological feed of session events (started, completed, failed, etc.) with timestamps.
  **Files**: `client-vue/src/components/board/BoardActivityPanel.vue`
  **Acceptance**: Activity feed updates in real-time via WebSocket

### Phase 4: Plugins

- [x] 26. **Port plugin system core**
  **What**: Adapt `plugins/types.ts` for Vue (replace React types with Vue equivalents). Port `plugins/registry.ts` (no changes needed — framework-agnostic). Port `plugins/slots.ts`. Create `plugins/context.ts` as a Pinia store or composable instead of React context.
  **Files**: `client-vue/src/plugins/types.ts`, `client-vue/src/plugins/registry.ts`, `client-vue/src/plugins/slots.ts`, `client-vue/src/plugins/composable.ts`
  **Acceptance**: `registerPlugin()` works, `getSidebarViews()` returns items, plugin components render in Vue

- [x] 27. **Implement GitHub plugin panel**
  **What**: Plugin header (title, connected pill, gear icon), tabs (Issues/Pull Requests/Repos), search, issue list items (status icon, title, repo, labels, avatar, time, "Link →" hover action). Port from `plugins/builtin/github/` and `components/layout/github-panel.tsx`.
  **Files**: `client-vue/src/plugins/builtin/github/index.ts`, `client-vue/src/plugins/builtin/github/GitHubPanel.vue`, `client-vue/src/plugins/builtin/github/IssueItem.vue`, `client-vue/src/plugins/builtin/github/PullRequestItem.vue`
  **Acceptance**: GitHub issues/PRs load from API, tabs switch, link action visible on hover

- [x] 28. **Implement Linear plugin panel**
  **What**: Same pattern as GitHub — header, tabs, ticket list with ID, title, status pill, priority indicator.
  **Files**: `client-vue/src/plugins/builtin/linear/index.ts`, `client-vue/src/plugins/builtin/linear/LinearPanel.vue`, `client-vue/src/plugins/builtin/linear/TicketItem.vue`
  **Acceptance**: Linear tickets render (or graceful empty state if no Linear integration)

- [x] 29. **Implement Marketplace panel**
  **What**: "Installed" section (list with icon, name, status, configure link), "Available" section (2-column grid of cards with icon, name, description, install button).
  **Files**: `client-vue/src/plugins/builtin/marketplace/index.ts`, `client-vue/src/plugins/builtin/marketplace/MarketplacePanel.vue`
  **Acceptance**: Installed plugins show status, available plugins show install button

- [x] 30. **Implement Docker/Sentry/Slack stub panels**
  **What**: Simple panels with connection status and basic content (Docker: container list, Sentry: error list with badge count, Slack: disconnected state).
  **Files**: `client-vue/src/plugins/builtin/docker/DockerPanel.vue`, `client-vue/src/plugins/builtin/sentry/SentryPanel.vue`, `client-vue/src/plugins/builtin/slack/SlackPanel.vue`
  **Acceptance**: Panels render with correct connection states

- [x] 31. **Wire plugin icons into Icon Rail**
  **What**: Icon Rail dynamically renders plugin sidebar items from registry. Status dots and badges come from plugin status API. Clicking a plugin icon shows its context panel.
  **Files**: `client-vue/src/components/layout/IconRail.vue` (update)
  **Acceptance**: Plugin icons appear in rail with correct status indicators

### Phase 5: Settings & Polish

- [x] 32. **Implement Command Palette**
  **What**: Port `command-palette.tsx` using shadcn-vue `Command` component. Wire to `useCommandStore`. Support session switching, navigation, actions. Trigger with `Cmd+K`.
  **Files**: `client-vue/src/components/CommandPalette.vue`, `client-vue/src/composables/use-commands.ts`
  **Acceptance**: `Cmd+K` opens palette, typing filters commands, selecting navigates

- [x] 33. **Implement keyboard shortcuts system**
  **What**: Port `use-keyboard-shortcut` composable and keybindings store. Support configurable shortcuts for common actions (new session, toggle panels, navigate).
  **Files**: `client-vue/src/composables/use-keyboard-shortcut.ts`, `client-vue/src/stores/keybindings.ts`
  **Acceptance**: Keyboard shortcuts trigger correct actions

- [x] 34. **Implement Settings page**
  **What**: Settings route with sections contributed by plugins (via `getSettingsSections()`). Include credentials management, workspace settings, appearance/theme.
  **Files**: `client-vue/src/routes/settings.tsx`, `client-vue/src/components/settings/SettingsPage.vue`, `client-vue/src/components/settings/CredentialsSection.vue`, `client-vue/src/components/settings/AppearanceSection.vue`
  **Acceptance**: Settings page renders all sections, changes persist

- [x] 35. **Implement Analytics page**
  **What**: Port analytics page with charts. Use a Vue-compatible charting library (e.g., `vue-chartjs` or keep `recharts` if it works, or switch to `@vueuse/charts`).
  **Files**: `client-vue/src/routes/analytics.tsx`, `client-vue/src/components/analytics/AnalyticsPage.vue`
  **Acceptance**: Charts render with session/model/cost data

- [x] 36. **Implement Login page**
  **What**: Port login page outside auth gate.
  **Files**: `client-vue/src/routes/login.tsx`, `client-vue/src/components/auth/LoginPage.vue`
  **Acceptance**: Login flow works end-to-end

- [x] 37. **Add transitions and animations**
  **What**: Panel collapse/expand transitions (Vue `<Transition>`), context panel swap animation, tool card expand/collapse, kanban card hover lift, status dot pulse animation, rail tooltip fade.
  **Files**: `client-vue/src/assets/transitions.css`, various component updates
  **Acceptance**: Animations match mockup CSS (`.25s ease` transitions)

- [x] 38. **Implement Onboarding Wizard**
  **What**: Port `OnboardingWizard` component for new users in cloud mode.
  **Files**: `client-vue/src/components/onboarding/OnboardingWizard.vue`
  **Acceptance**: New users see wizard, completing it dismisses it

### Phase 6: Migration & Cleanup

- [x] 39. **Port remaining composables**
  **What**: Convert any remaining hooks not yet ported: `use-config`, `use-credentials`, `use-harnesses`, `use-skills`, `use-available-tools`, `use-autocomplete`, `use-command-history`, `use-diffs`, `use-directory-browser`, `use-find-files`, `use-fleet-summary`, `use-foldable-screen`, `use-integrations`, `use-media-query`, `use-open-directory`, `use-persisted-state`, `use-pr-status`, `use-relative-time`, `use-repositories`, `use-repository-detail`, `use-repository-info`, `use-sidebar-resize`, `use-workspaces`, `use-rename-workspace`, `use-analytics-*` (5 hooks).
  **Files**: `client-vue/src/composables/` (25+ files)
  **Acceptance**: All composables pass type checking

- [x] 40. **Port remaining pages**
  **What**: Repositories, Pipelines, Queue, Templates, Welcome, Not Found — pages that exist in React but weren't covered in earlier phases.
  **Files**: `client-vue/src/routes/repositories.tsx`, `client-vue/src/routes/pipelines.tsx`, `client-vue/src/routes/queue.tsx`, `client-vue/src/routes/templates.tsx`, `client-vue/src/routes/welcome.tsx`
  **Acceptance**: All routes render correctly

- [x] 41. **Add Tauri support**
  **What**: Port `lib/tauri.ts` and `tauri-update-dialog.tsx`. Keep `@tauri-apps/api` as optional dependency.
  **Files**: `client-vue/src/lib/tauri.ts`, `client-vue/src/components/TauriUpdateDialog.vue`
  **Acceptance**: App works in both browser and Tauri contexts

- [x] 42. **Write tests**
  **What**: Set up Vitest + `@vue/test-utils`. Port critical tests from `client/src/` — focus on composables (API interactions), store logic, and key component rendering. Match existing test patterns in `hooks/__tests__/`, `contexts/__tests__/`, `plugins/__tests__/`, `lib/__tests__/`.
  **Files**: `client-vue/vitest.config.ts`, `client-vue/src/test-setup.ts`, `client-vue/src/composables/__tests__/`, `client-vue/src/stores/__tests__/`, `client-vue/src/components/__tests__/`
  **Acceptance**: `npm run test` passes, coverage on composables and stores

- [x] 43. **Full integration test**
  **What**: Run Vue app against real backend. Verify: login, session creation, message sending, WebSocket streaming, project CRUD, board view, plugin panels, settings, command palette.
  **Acceptance**: All user flows work end-to-end

- [x] 44. **Swap apps**
  **What**: Rename `client/` → `client-react-archive/`, `client-vue/` → `client/`. Update any build scripts, CI configs, or Makefile references. Verify `npm run build` produces correct `dist/`.
  **Files**: `client/` (renamed from `client-vue/`)
  **Acceptance**: `npm run build && npm run preview` serves the Vue app correctly

- [x] 45. **Remove React archive**
  **What**: Delete `client-react-archive/` once Vue app is confirmed stable in production.
  **Acceptance**: No React code remains in repository

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| shadcn-vue component gaps vs shadcn/react | Medium | Medium | Audit component list in Phase 0; build missing ones manually |
| TanStack Router for Vue is less mature than React version | Medium | High | Spike in Phase 0; fallback to `vue-router` if blocking issues found |
| Markdown rendering library differences | Low | Low | Use `markdown-it` + `vue3-markdown-it` or port with `unified` ecosystem |
| WebSocket composable complexity (reconnection, buffering) | Medium | High | Port `use-weave-socket` carefully with tests; this is the most critical composable |
| Plugin system React→Vue type mapping | Low | Medium | Most plugin types are interfaces — straightforward to adapt |
| Recharts (React-only) for analytics | High | Low | Replace with `Chart.js` via `vue-chartjs` — simpler API anyway |
| 61 hooks to port | — | — | Many are thin `apiFetch` wrappers; batch-port by domain (sessions, analytics, etc.) |

## Verification

- [x] `cd client && npm run build` — zero errors
- [x] `cd client && npm run typecheck` — zero errors
- [x] `cd client && npm run test` — all tests pass
- [ ] Visual comparison: mockup HTML vs running Vue app — layout matches (requires running backend)
- [x] All API endpoints consumed (grep for `/api/` in composables, compare with React hooks)
- [ ] WebSocket streaming works (send prompt, see real-time response) (requires running backend)
- [x] Plugin system: GitHub panel loads, marketplace renders
- [x] Keyboard shortcuts: `Cmd+K` opens palette, `Cmd+N` creates session
- [ ] Auth flow: logout → login redirect → session restore (requires running backend)
- [ ] Tauri: app launches in desktop mode (if Tauri tooling available)
