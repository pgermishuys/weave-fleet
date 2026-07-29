# Phase 1: Prototype Visual Integration

## TL;DR
Align the Vue app's visual layer with the prototype by swapping design tokens, restructuring the shell into composable panels with 8px gaps, and applying sharp geometry + hover effects.

## Context
- Current app: Vue 3 + Vite + Tailwind 4 + TanStack Router + Pinia
- Shell lives in `client/src/components/layout/AppShell.vue` with `IconRail`, `ContextPanel`, `CenterContent`, and right panels
- CSS variables defined in `client/src/assets/main.css` (Tailwind 4 `@theme inline` block)
- Prototype target: `.weave/prototype/index.html` with design tokens in `.weave/prototype/DESIGN.md`
- Verification: use `bun run tsx visual-compare.ts` in `tests/beta-harness/` to screenshot prototype vs running app

## Scope
- In scope: CSS token swap, font stack, zero radius, 8px gap panel layout, app shell restructure, message hover effect
- Out of scope: Backend, new routes, tool call rendering, content panel internals, mobile layout changes
- Constraints / assumptions:
  - Must preserve existing theme system (multiple themes); add a new `"prototype"` / default theme or modify the light theme
  - Keep mobile drawer behavior intact
  - TanStack Router route structure unchanged — only layout components change

## Objectives
- Match prototype's warm-neutral palette (coral, indigo, warm bg)
- Sharp 0px radius everywhere
- Inter + JetBrains Mono font stack (already partially set up)
- 8px gap panel layout with individual borders
- Composable panel vocabulary per AGENTS.md

## Dependencies and Order
1. Token/theme changes (Task 1-3) are independent of layout restructure
2. Layout restructure (Task 4) depends on understanding current shell but not on token changes
3. Message hover (Task 5) is independent
4. Verification (Task 6) runs last

## Tasks

- [ ] 1. Add prototype design tokens to CSS
  - **What**: In `client/src/assets/main.css`, add a new theme block (or modify `:root[data-theme="light"]`) with prototype tokens: `--bg: #FAF9F7`, `--surface: #FFFFFF`, `--border: #E8E6E3`, `--text: #1A1918`, `--muted: #6E6A65`, `--coral: #D95A3A`, `--indigo: #5B6EC7`. Map these to existing variable names: `--main-bg → #FAF9F7`, `--panel-bg → #FFFFFF`, `--border → #E8E6E3`, `--text → #1A1918`, `--muted → #6E6A65`, `--accent → #5B6EC7`. Add `--coral: #D95A3A` as a new variable. Update the `@theme inline` block to expose `--color-coral`.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - Light theme uses warm-neutral palette
    - `--coral` and `--indigo` are available as Tailwind colors via `@theme inline`

- [ ] 2. Zero radius override
  - **What**: Set `--radius-card: 0px`, `--radius-btn: 0px`, `--radius-panel: 0px` in the `:root` block (applies to all themes). Remove any hardcoded `border-radius` in layout components that override these.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - All cards, buttons, panels render with sharp corners
    - No `border-radius` > 0 visible in the app

- [ ] 3. Font stack confirmation
  - **What**: Verify `--font-sans-stack` already uses Inter (it does). Ensure `--font-mono-stack` uses JetBrains Mono (it does). No DM Sans references exist. Confirm Google Fonts or local font loading includes Inter 400/500/600. Check `client/index.html` for font links.
  - **Files**: `client/index.html` (if font links need updating)
  - **Depends on**: None
  - **Acceptance**:
    - `font-family` resolves to Inter for body, JetBrains Mono for code
    - No DM Sans references anywhere in client/

- [ ] 4. App shell restructure with 8px gap panel layout
  - **What**: Restructure `AppShell.vue` template so `.main` uses `gap: 8px` and `padding: 8px` (panels float in an 8px-gapped grid). Each panel (`IconRail`, `ContextPanel`, `CenterContent`, right panels) gets `border: 1px solid var(--border)` and `background: var(--panel-bg)`. The layout semantics become: `[rail][context]` base, where context composition changes by route. Rename existing components if needed to match vocabulary: `IconRail` → rail, `ContextPanel` → session-list, `CenterContent` → conversation area. Add a comment block at top of AppShell explaining the panel vocabulary.
  - **Files**: `client/src/components/layout/AppShell.vue`, `client/src/components/layout/IconRail.vue`, `client/src/components/layout/ContextPanel.vue`, `client/src/components/layout/CenterContent.vue`
  - **Depends on**: Task 1 (needs `--border` token correct)
  - **Acceptance**:
    - 8px gap visible between all panels
    - Each panel has its own 1px border
    - Rail is 48px wide, fixed left
    - No resize gutter except between conversation and content (right panel)

- [ ] 5. Message hover effect
  - **What**: In `MessageBubble.vue`, add a hover state: `border-left: 3px solid var(--indigo)` (or `--accent` since we mapped it) and `background: color-mix(in srgb, var(--indigo) 5%, transparent)` on `.msg:hover` or equivalent wrapper. Use CSS transition `180ms ease-out`.
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: Task 1 (needs indigo token)
  - **Acceptance**:
    - Hovering a message shows indigo left border + faint indigo background
    - Transition is smooth (180ms)

- [ ] 6. Visual verification
  - **What**: Run `bun run tsx visual-compare.ts` in `tests/beta-harness/` to capture screenshots of both prototype and running app. Compare token colors, gap spacing, border radius, and hover states. Document any remaining discrepancies.
  - **Depends on**: Tasks 1-5
  - **Acceptance**:
    - Screenshots captured successfully
    - No major visual discrepancies between prototype and app for: colors, spacing, radius, font rendering

## Verification
1. `cd tests/beta-harness && bun run tsx visual-compare.ts` — captures comparison screenshots
2. Visual inspection: panels have 8px gaps, 0px radius, warm-neutral colors, indigo hover on messages
3. `cd client && bun run build` — no build errors
