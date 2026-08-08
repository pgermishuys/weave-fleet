# Prototype Visual Alignment

## TL;DR
Transfer the static HTML prototype's design language into the existing Vue 3 + Tailwind client — warm palette with coral/indigo accents, 0px radii, and structural UI components (conversation view, artifacts panel, status bar, automations, session tree).

## Context
The client already has:
- A light theme (`data-theme="light"`) in `client/src/assets/main.css` with `--bg: #FAF9F7`, `--coral: #D95A3A`, `--indigo: #5B6EC7` defined (lines 31-55)
- `--radius-card`, `--radius-btn`, `--radius-panel` all set to `0px` globally (lines 24-26)
- Tailwind v4 via `@tailwindcss/postcss` (no `tailwind.config.*` file — config is in CSS via `@theme`)
- `@theme` block mapping CSS vars to Tailwind tokens (lines 220-245)
- Layout shell: `AppShell.vue` → `IconRail`, `ContextPanel`, `CenterContent`, `RightPanelTabs`
- Session conversation: `ActivityStream.vue`, `MessageBubble.vue`, `ToolCard.vue`, `Composer.vue`
- Sessions list: `SessionsPanel.vue`, `SessionItem.vue`, `ProjectGroup.vue`
- No automations route or component exists yet
- Routes use TanStack Router with file-based generation (`client/src/routes/`)

## Scope
- In scope: Gaps 1-7 as listed; light theme is the target (dark themes untouched for now)
- Out of scope: Dark theme updates, mobile responsiveness changes, backend API changes
- Constraints: Must use existing CSS variable system; no Tailwind config file (v4 CSS-based config)

## Objectives
- Light theme uses coral (`#D95A3A`) as primary action colour (buttons, active states)
- Indigo (`#5B6EC7`) remains secondary/accent for links and subtle highlights
- Border radius confirmed at 0px everywhere (already done — verify no overrides)
- Conversation view matches prototype layout (messages, tool call cards, streaming)
- Artifacts panel on right shows files/diffs/sources
- Status bar at bottom with shortcuts and session info
- Automations route with card-based list
- Session tree with session→task hierarchy in left panel

## Dependencies and Order
1. Gaps 1-2 (colour/radius) are CSS-only quick wins — do first
2. Gap 5 (status bar) is a simple new component, no deps
3. Gaps 3-4 (conversation + artifacts) are related — conversation references artifacts
4. Gap 6 (automations) is independent
5. Gap 7 (session tree) modifies existing `SessionsPanel`

## Tasks

- [ ] 1. Wire coral as primary action colour in light theme
  - **What**: In the light theme block, set `--accent: #D95A3A` (coral) so all buttons/active states use coral. Keep `--indigo` available as a separate token for secondary use. Add `--color-secondary: var(--indigo)` to the `@theme` block.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - In light mode, primary buttons render with `#D95A3A` background
    - Indigo available via `text-secondary` / `bg-secondary` Tailwind utilities
    - Dark theme unchanged

- [ ] 2. Audit and remove any hardcoded border-radius overrides
  - **What**: Search all `.vue` and `.css` files for `border-radius` or `rounded-` classes that override the 0px global. Remove or replace with `var(--radius-card)` / `var(--radius-btn)` / `var(--radius-panel)`.
  - **Files**: Multiple — grep for `rounded-` and `border-radius` across `client/src/`
  - **Depends on**: None
  - **Acceptance**:
    - No element renders with visible border-radius in light theme
    - `rounded-full` on avatars/badges is acceptable (explicit exemption)

- [ ] 3. Add status bar component
  - **What**: Create `StatusBar.vue` — a fixed bottom bar (height ~32px) showing: left = keyboard shortcuts hints (⌘K, ⌘/, etc.), right = session status (model, token count, session state). Mount it in `AppShell.vue` below the main content area.
  - **Files**: `client/src/components/layout/StatusBar.vue`, `client/src/components/layout/AppShell.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Bar visible at bottom of viewport on all routes
    - Shows at least 2 keyboard shortcut hints and current session status
    - Uses `--panel-bg` background with `--border` top border

- [ ] 4. Enhance conversation view to match prototype
  - **What**: Update `ActivityStream.vue` and `MessageBubble.vue` to match prototype styling: user messages right-aligned with coral accent, assistant messages left-aligned with subtle bg, tool call cards with collapsible detail, clear visual separation between message clusters.
  - **Files**: `client/src/components/session/ActivityStream.vue`, `client/src/components/session/MessageBubble.vue`, `client/src/components/session/ToolCard.vue`
  - **Depends on**: Task 1 (coral as primary)
  - **Acceptance**:
    - User messages visually distinct (coral tinted bg or border)
    - Tool calls render as collapsible cards
    - Message clusters visually grouped with spacing

- [ ] 5. Build artifacts panel
  - **What**: Create or enhance the right panel (currently `SessionsV2RightPanel.vue` / `RightPanelTabs.vue`) to show an artifacts view with tabs: Files Changed, Sources, Preview. Integrate with existing `FilesChangedPanel.vue` and `DiffView.vue`.
  - **Files**: `client/src/components/layout/RightPanelTabs.vue`, `client/src/components/session/FilesChangedPanel.vue` (wire into right panel)
  - **Depends on**: Task 4 (conversation must exist to show artifacts alongside)
  - **Acceptance**:
    - Right panel shows tabs: Files, Sources, Preview
    - Files tab lists changed files with diff counts
    - Clicking a file shows inline diff (reuse `DiffView.vue`)

- [ ] 6. Create automations route and card list
  - **What**: Add route file `client/src/routes/automations.tsx` and component `AutomationsPanel.vue`. Display automation cards (trigger, schedule, status) in a grid. Wire into `IconRail.vue` navigation.
  - **Files**: `client/src/routes/automations.tsx`, `client/src/components/automations/AutomationsPanel.vue`, `client/src/components/automations/AutomationCard.vue`, `client/src/components/layout/IconRail.vue`
  - **Depends on**: None
  - **Acceptance**:
    - `/automations` route renders card grid
    - Each card shows: name, trigger type, last run, status badge
    - Rail icon navigates to automations view

- [ ] 7. Restructure session list as session→task tree
  - **What**: Update `SessionsPanel.vue` and `ProjectGroup.vue` to render sessions as expandable tree nodes. Each session expands to show child tasks (delegated sub-sessions). Add expand/collapse with indent levels.
  - **Files**: `client/src/components/sessions/SessionsPanel.vue`, `client/src/components/sessions/ProjectGroup.vue`, `client/src/components/sessions/SessionItem.vue`
  - **Depends on**: None (but do after tasks 1-5 for visual consistency)
  - **Acceptance**:
    - Sessions with children show expand chevron
    - Expanded session shows indented child task items
    - Collapse/expand state persists during session

## Verification
1. Run `cd client && npm run build` — no errors
2. Run `cd client && npm run lint` — no new warnings
3. Visual check in browser with `data-theme="light"`:
   - Background is `#FAF9F7`, buttons are coral, no rounded corners
   - Conversation shows styled messages with tool cards
   - Right panel shows artifacts tabs
   - Bottom status bar visible
   - `/automations` shows card grid
   - Session list shows tree hierarchy
