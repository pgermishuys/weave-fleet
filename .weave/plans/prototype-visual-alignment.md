# Prototype Visual Alignment Plan

**Goal:** Align the Weave Fleet Vue app with the static HTML prototype at `.weave/prototype/index.html`.

**Verification:** After each phase, run:
```powershell
cd tests/beta-harness
bun run tsx visual-compare.ts --app-only
```
Then read screenshots from `findings/visual-compare/` to assess fidelity.

**Prereqs:** Dev server running: `cd client && bun run dev:mock`

---

## Phase 1: Theme & Accent Colour Alignment

**Status:** Not started  
**Effort:** Small (1-2 files)  
**Dependencies:** None

### Problem
- The app defaults to system theme (dark on most dev machines). For prototype fidelity the light theme should be the comparison baseline.
- The active rail indicator uses indigo/purple; prototype uses coral `#D95A3A`.
- Some components may use hardcoded Tailwind `rounded-*` classes overriding the `0px` CSS vars.

### Files to change

| File | Change |
|------|--------|
| `client/src/components/layout/IconRail.vue` | Change active indicator colour from accent/indigo to coral (`var(--coral)` / `text-coral` / `bg-coral`) |
| `client/src/assets/main.css` | Verify `--coral` and `--indigo` are defined in `:root` (not just light theme) for use as utility colours |
| Global search for `rounded-lg`, `rounded-md`, `rounded-xl` | Replace with `rounded-none` or remove — the CSS vars already set 0px radius |

### Verification
- Screenshot app-landing.png and app-session-detail.png
- Compare rail indicator colour (should be coral bar, not indigo)
- Check all cards/buttons have sharp corners

---

## Phase 2: Message Styling

**Status:** Not started  
**Effort:** Medium (2-4 files)  
**Dependencies:** Phase 1 (colours)

### Problem
Prototype messages are simple inline blocks with role icons and timestamps. App uses card-style containers with backgrounds and role label headers.

### Target style (from prototype)
- **User messages:** Small person icon + message text, right-aligned timestamp (e.g., "3m")
- **Assistant messages:** Small robot icon + message text, right-aligned timestamp
- **No card backgrounds** — messages sit directly on the page background
- **Tool calls:** Collapsible disclosure triangle (`▶`) with monospace command text (e.g., `grep — "jsonWebToken" in package.json`), result shown in muted text below
- **Streaming state:** Italic muted text (e.g., "Analyzing the existing middleware chain...")
- **Inline code:** Monospace with subtle background

### Files to change

| File | Change |
|------|--------|
| `client/src/components/session/ActivityStream.vue` | Investigate current message rendering structure |
| Message bubble/card components (find in `components/session/`) | Remove card backgrounds, add role icon (small, inline), add relative timestamp right-aligned |
| Tool call components | Render as collapsible `<details>` with disclosure triangle, monospace command name |

### Verification
- Screenshot app-session-detail.png
- Compare message layout against prototype-conversation.png

---

## Phase 3: Right Panel — Artifacts Tab

**Status:** Not started  
**Effort:** Medium-Large (3-5 files, new component)  
**Dependencies:** None (can be done in parallel with Phase 2)

### Problem
Right panel shows "Session" tab with "TODO LIST" placeholder. Prototype shows "Artifacts" + "Info" tabs with a file list grouped by category.

### Target (from prototype)
- **Tabs:** "Artifacts" | "Info"
- **Summary line:** "2 new · 2 edit · 2 source"
- **Grouped list:**
  - **NEW (count):** Green dot + filename + line count right-aligned
  - **MODIFIED (count):** Blue dot + filename + "+N -M" diff stats right-aligned
  - **SOURCE (count):** Grey dot + filename + "read" or line count right-aligned
- Each file is clickable (opens content viewer — future work)

### Files to change

| File | Change |
|------|--------|
| `client/src/components/layout/RightPanelTabs.vue` | Add "Artifacts" tab alongside existing "Session" tab |
| `client/src/components/layout/ContextPanel.vue` | Render artifacts content when Artifacts tab is selected |
| New: `client/src/components/session/ArtifactsPanel.vue` | File list component with grouped display (new/modified/source) |
| Data source: session diffs/files API | Wire to existing `useDiffs` composable or session file data |

### Verification
- Screenshot app-session-detail.png
- Right panel should show Artifacts tab with file groupings

---

## Phase 4: Status Bar

**Status:** Not started  
**Effort:** Small-Medium (1-2 new files)  
**Dependencies:** None

### Problem
No status bar exists. Prototype has a persistent bottom bar with keyboard shortcuts and session state.

### Target (from prototype)
- Fixed to bottom of viewport
- **Left section:** Keyboard shortcut hints: `Ctrl N` New session · `Ctrl K` Command · `Ctrl Enter` Send · `Ctrl .` Approve · `Esc` Cancel
- **Right section:** Green dot + "IDLE" | model badge (e.g., `claude-opus-4`) | token count (e.g., "4,218 tokens")
- Subtle border-top, small text, muted colour

### Files to change

| File | Change |
|------|--------|
| New: `client/src/components/layout/StatusBar.vue` | Bottom bar component with shortcuts + session state |
| `client/src/components/layout/AppShell.vue` | Add StatusBar to bottom of shell layout |
| Data source | Session status from session store, model/tokens from session detail |

### Verification
- Screenshot app-session-detail.png
- Bottom should show shortcut bar with session status

---

## Phase 5: Automations Page

**Status:** Not started  
**Effort:** Medium (2-3 files)  
**Dependencies:** Phase 1 (coral CTA colour)

### Problem
Pipelines page shows "Coming soon" placeholder. Prototype shows full automation cards.

### Target (from prototype)
- Page title: "Automations" with coral "New Automation" button top-right
- Cards (full-width, no background, subtle border):
  - **Title** (bold) + **Enabled/Disabled** badge (green outline / grey outline) + action icons (play, pause, edit, delete)
  - **Prompt:** Description text
  - **Trigger:** "Schedule: `0 9 * * 1-5`" or "Event: `pull_request.merged`" (monospace for cron/event)
  - **Policy:** "Max N concurrent, M/hour, Tmin timeout"

### Files to change

| File | Change |
|------|--------|
| `client/src/routes/pipelines.tsx` | Replace placeholder with automation list |
| New: `client/src/components/automations/AutomationCard.vue` | Card component matching prototype layout |
| Mock data: `client/src/mocks/` | Add automations mock data (or extend vite-plugin-mock-api) |

### Verification
- Screenshot app-pipelines.png
- Compare against prototype-automations.png

---

## Task Checklist

- [x] Phase 1: Theme & Accent Colour Alignment
- [x] Phase 2: Message Styling
- [x] Phase 3: Right Panel — Artifacts Tab
- [x] Phase 4: Status Bar
- [ ] Phase 5: Automations Page

## Execution Order

Recommended parallel tracks:
- **Track A:** Phase 1 → Phase 2 (theme then messages)
- **Track B:** Phase 3 (artifacts panel, independent)
- **Track C:** Phase 4 (status bar, independent)
- **After A+B+C:** Phase 5 (automations)

Each phase should take a visual comparison screenshot on completion.
