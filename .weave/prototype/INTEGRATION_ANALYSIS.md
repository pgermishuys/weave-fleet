# Prototype Integration: Technical Feasibility & Gap Analysis

**Status:** Reference Document
**Created:** 2026-07-29
**Prototype:** `.weave/prototype/index.html`

---

## Overview

This document analyzes the feasibility of integrating the `.weave/prototype/index.html` design prototype into the Weave Fleet production frontend (`client/`). It covers what exists today, what is missing, and a phased approach to implementation.

The prototype introduces:
- A new design system (sharp geometry, coral/indigo accents, warm neutrals)
- Rich tool call rendering (per-tool-type visual components)
- Reasoning/thinking display in the conversation stream
- A right-side artifact panel with markdown/HTML rendering
- An automations system (schedule and event-based triggers)
- Structural layout changes (icon rail, context panel, resize gutter)

---

## Current Architecture

### Backend (.NET, Clean Architecture)

- `WeaveFleet.Domain`: Session, Instance, Delegation, Board, Project, Workspace, PersistedMessage, HarnessEventLogEntry
- `WeaveFleet.Api`: 32 endpoint files covering sessions, events, boards, analytics, plugins, preferences, WebSocket streaming
- **No** Automation or Artifact entities exist in the domain layer

### Frontend (Vue 3 + Vite + Tailwind 4 + TanStack Router + Pinia)

- Conversation: `ActivityStream.vue`, `MessageBubble.vue`, `ToolCard.vue`, `DiffView.vue`
- Session management: `FilesChanged*.vue`, `ModelSelector.vue`, `Composer.vue`, `SessionDetailPanel.vue`
- Reasoning: Already tracked in event-state (`part.type === "reasoning"`), accumulated in `pagination-utils.ts`
- Layout: Sidebar + main content area (no icon rail, no right artifact panel)
- Theming: Tailwind 4 with custom tokens, Inter + DM Sans + JetBrains Mono fonts

---

## Layer-by-Layer Analysis

### Layer 1: Design System Reskinning

| Aspect | Prototype | Current | Effort |
|--------|-----------|---------|--------|
| Typography | Inter + Courier New | Inter + DM Sans + JetBrains Mono | Font var swap |
| Colours | `--coral: #D95A3A`, `--indigo: #5B6EC7`, `--bg: #FAF9F7` | Tailwind theme tokens | Remap vars |
| Corners | 0px everywhere | Rounded (reka-ui/shadcn defaults) | Override `--radius` |
| Layout | Icon rail + context panel + center + gutter + right panel | Sidebar + main | Structural rework |
| Spacing | 8px gap panels with individual borders | Shared borders | CSS refactor |
| Transitions | 180ms ease-out | Varies | Standardize |

**Feasibility:** HIGH. Pure theming and layout restructure.

**Key files to modify:**
- `client/src/App.vue` or layout wrapper (shell restructure)
- Tailwind config / CSS custom properties (theme tokens)
- All `ui/` components (radius, border, colour overrides)

---

### Layer 2: Rich Tool Call Rendering

The prototype renders each tool type as a purpose-built visual component rather than a generic collapsible block.

| Tool Type | Prototype Treatment | Current State | Gap |
|-----------|--------------------|--------------:|-----|
| Read | File icon + path + line preview | Generic ToolCard | New sub-component |
| Write | Pencil + diff-style green additions | DiffView exists | Restyling |
| Glob | Search icon + indigo pattern pill | Generic | New sub-component |
| Grep | Search + pattern + match count | Generic | New sub-component |
| Skill | Layers icon + skill name + launch status | Generic | New sub-component |
| Diagram | Mermaid.js inline render + expand | Not present | New dependency + component |
| Shell | Terminal-style output | Generic | New sub-component |

**Feasibility:** HIGH. Data already flows with tool type identification via `activity-stream-tool-card.ts`.

**Implementation approach:**
1. Create a tool-type dispatcher in `ToolCard.vue` (or replace it) that selects a sub-component by `kind`
2. Build per-type components: `ReadToolCard.vue`, `WriteToolCard.vue`, `GlobToolCard.vue`, `GrepToolCard.vue`, `ShellToolCard.vue`, `SkillToolCard.vue`, `DiagramToolCard.vue`
3. Add `mermaid` npm dependency for diagram rendering

---

### Layer 3: Reasoning/Thinking Display

| Feature | Current State | Gap |
|---------|---------------|-----|
| Reasoning part accumulation | Complete (`event-state.ts` line 225) | None |
| Reasoning token tracking | Complete (tracked separately in token objects) | None |
| Activity filter for reasoning | Complete (`use-activity-filter.ts` line 50) | None |
| Visual rendering (brain icon, italic, muted) | Not implemented | Small: new bubble variant |

**Feasibility:** HIGH. The entire data pipeline exists. Only the visual treatment is missing.

**Implementation:** Add a reasoning-specific render path in `MessageBubble.vue` or create a `ThinkingBubble.vue` component that renders with brain icon + italic muted text + collapsible detail.

---

### Layer 4: Right Panel (Artifacts Viewer)

The prototype shows a right-side panel with:
- File list (new / modified / source categories with dot indicators)
- Rendered markdown view with inline annotation support
- Raw source view toggle
- HTML preview via sandboxed iframe
- Resize gutter between center content and right panel

| Feature | Current State | Gap |
|---------|---------------|-----|
| File list with categories | `FilesChanged*.vue` components exist | Restyling + category mapping |
| Markdown rendering | `markdown-it` dependency present | Rendering exists; needs panel context |
| HTML iframe preview | Not present | New component |
| RENDERED / RAW toggle | Not present | New toggle + view switching |
| Resize gutter | Not present | New: pointer events + flex resize |
| Artifact directory concept | Not present | Backend config + file discovery |
| Comment/annotation system | Not present | Significant new feature |

**Feasibility:** MEDIUM-HIGH.

**Key decisions needed:**
1. **Artifact directory**: Where do artifacts live? Options: (a) configurable relative path per session, (b) convention-based (e.g., `.weave/artifacts/`), (c) tracked from tool call outputs (files written by the agent)
2. **File discovery**: Poll the filesystem? Watch via backend? Derive from session event stream (already have file write events)?
3. **Annotations**: Defer to a later phase or implement as local-only draft comments?

**Recommended approach:** Start by deriving the artifact list from the session's file-write events (which already exist in the event stream). Add a rendered viewer panel. Defer annotations.

---

### Layer 5: Automations

This is entirely new. The prototype shows:
- Automation list view (cards with name, prompt, trigger, policy)
- CRUD form (name, prompt, trigger type toggle, cron/event config, run policy)
- Trigger types: Schedule (cron) and Event (pull_request.merged, push, etc.)
- Run policy: max concurrent, max per hour, timeout

| Component | Current State | Work Required |
|-----------|---------------|---------------|
| Domain entities | Not present | `Automation`, `AutomationTrigger`, `AutomationExecution`, `AutomationRunPolicy` |
| API endpoints | Not present | Full CRUD + execution status |
| Scheduler | Not present | Cron-based job scheduling (Hangfire/Quartz.NET) |
| Event triggers | Not present | Event subscription + filter matching |
| Run policy enforcement | Not present | Concurrency limiter, rate limiter, timeout |
| Frontend list view | Not present | `AutomationsList.vue` |
| Frontend form | Not present | `AutomationForm.vue` |

**Feasibility:** LOW-MEDIUM for full implementation. This is the largest gap and requires full-stack architecture work.

**Possible shortcuts:**
- Phase 1: UI only (form + list) with no backend execution, to validate the UX
- Phase 2: Integrate with existing session creation to "auto-create sessions" on trigger
- Phase 3: Full scheduler + event infrastructure

---

### Layer 6: Other Features

| Feature | Exists Today | Gap | Effort |
|---------|-------------|-----|--------|
| Session tree (context panel) with drag-drop | Sidebar exists; delegation hierarchy in domain | Restyle + drag-drop interaction | Medium |
| Model selector with search/filter | `ModelSelector.vue` exists | Restyle to match prototype | Low |
| Hotkey bar | `keybindings.ts` store exists | New bottom bar component | Low |
| New Session form | Likely partial | Verify; prototype form is well-defined | Low-Medium |
| Board/Kanban view | `board.ts` store + route exist | Restyle | Low |
| Analytics view | Components + route exist | Restyle | Low |
| Settings view with nav columns | Components exist | Restyle to prototype layout | Low-Medium |
| Queued comments system | Not present | New feature (frontend queue + backend delivery) | Medium |
| Message hover effects (indigo border) | Not present | CSS only | Trivial |
| Artifact pill in breadcrumb toolbar | Not present | New UI element | Low |

---

## Phased Implementation Plan

### Phase 1: Design System & Shell (Estimated: 3-5 days)

1. Extract prototype CSS variables into Tailwind theme config
2. Restructure app shell: icon rail, context panel, center content, right panel, resize gutter
3. Override all `--radius` to 0px, remap colour tokens
4. Swap font stack to Inter + Courier New (or keep JetBrains Mono for code)
5. Add 8px gap-based panel layout with individual borders
6. Standardize 180ms ease-out transitions
7. Add hotkey bar component
8. Add message hover effects (indigo left-border)

**Outcome:** Visual parity with prototype for existing features.

### Phase 2: Rich Conversation Experience (Estimated: 3-5 days)

1. Implement tool-type dispatcher pattern over existing `ToolCard.vue`
2. Build per-tool-type components (Read, Write, Glob, Grep, Shell, Skill)
3. Add `mermaid` dependency and `DiagramToolCard.vue`
4. Add `ThinkingBubble.vue` for reasoning/thinking display
5. Implement message action buttons (copy, etc.)
6. Add artifact pill in conversation toolbar

**Outcome:** Conversation view matches prototype's rich rendering.

### Phase 3: Artifact Panel (Estimated: 5-8 days)

1. Design artifact directory configuration (per-session or workspace-level)
2. Derive artifact list from session file-write events (or add directory watcher)
3. Build right-panel artifact list view with new/modified/source categories
4. Build markdown rendered view (reuse `markdown-it`)
5. Build HTML preview with sandboxed iframe
6. Add RENDERED / RAW toggle
7. Implement resize gutter (pointer events + CSS flex)
8. Wire artifact pill dropdown to panel navigation

**Outcome:** Right panel functional with artifact browsing and preview.

### Phase 4: Automations (Estimated: 2-3 weeks)

1. Design domain model: `Automation`, `AutomationTrigger`, `AutomationExecution`
2. Create migration + repository
3. Build CRUD API endpoints
4. Implement cron scheduler (Hangfire or Quartz.NET)
5. Implement event-based trigger matching
6. Build run policy enforcement (concurrency, rate, timeout)
7. Build frontend list view and CRUD form
8. Wire automation execution to session creation
9. Add automation icon rail nav + view switching

**Outcome:** Full automation system operational.

### Phase 5: Polish & Advanced Features (Ongoing)

1. Comment/annotation system on artifacts
2. Queued comments to agent workflow
3. Session tree drag-and-drop reordering
4. New Session form refinements (directory picker, issue source toggle)
5. Board/Kanban restyling

---

## Key Technical Decisions to Make

1. **Artifact source**: Derive from event stream (file writes) vs filesystem watching vs configurable directory path?
2. **Automation scheduler**: Hangfire (mature, SQL-backed) vs Quartz.NET (lighter) vs custom?
3. **Mermaid rendering**: Client-side only (add npm dep) vs server-side SVG generation?
4. **Comment persistence**: Backend (new entity + API) vs local-only drafts vs defer entirely?
5. **Design system migration strategy**: Big-bang retheme vs incremental component-by-component?

---

## File Reference

| Concern | Key Files |
|---------|-----------|
| App shell / layout | `client/src/App.vue`, layout components |
| Theme tokens | Tailwind config, CSS custom properties |
| Tool rendering | `client/src/components/session/ToolCard.vue`, `activity-stream-tool-card.ts` |
| Reasoning state | `client/src/lib/event-state.ts`, `use-activity-filter.ts` |
| Message display | `client/src/components/session/MessageBubble.vue`, `ActivityStream.vue` |
| Files changed | `client/src/components/session/FilesChanged*.vue` |
| Model selector | `client/src/components/session/ModelSelector.vue` |
| Stores | `client/src/stores/` (sessions, workspace-ui, preferences, keybindings) |
| Backend domain | `src/WeaveFleet.Domain/Entities/` |
| Backend API | `src/WeaveFleet.Api/Endpoints/` |
| Prototype | `.weave/prototype/index.html` |
| Design reference | `.weave/prototype/DESIGN.md` |
