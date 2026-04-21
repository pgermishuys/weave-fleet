# Port `@` Mentions and `/` Slash Command Autocomplete into Composer

## TL;DR
> **Summary**: Wire the existing `useAutocomplete` composable into `Composer.vue` by creating an `AutocompletePopup.vue` component and updating the composer to track cursor position, delegate keydown events, and render the popup.
> **Estimated Effort**: Short

## Context
### Original Request
Port `@` file/agent references and `/` slash command composer autocomplete from weave-agent-fleet into the weave-fleet UI.

### Key Findings
Almost all backend and frontend plumbing **already exists** in weave-fleet:
- **`use-autocomplete.ts`** — fully ported Vue composable with trigger detection (`/` at position 0, `@` after whitespace), keyboard handling (Arrow/Enter/Tab/Escape), escape suppression, and selection/insertion logic. Returns `isOpen`, `items`, `selectedValue`, `onKeyDown`, `onSelect`, `onClose`.
- **`use-find-files.ts`** — debounced file search against `GET /api/instances/:id/find/files?q=`.
- **`api-types.ts`** — `AutocompleteCommand` and `AutocompleteAgent` interfaces already defined.
- **`use-autocomplete.ts` internally calls** `useInstanceCommands` (fetches `/api/instances/:id/commands`) and `useInstanceAgents` (fetches `/api/instances/:id/agents`).
- **`Composer.vue`** — plain `<textarea>` with `handleInput`, `handleKeydown`, `handleSend`. No autocomplete wiring yet. Receives `sessionId` and optional `instanceId` props.

The **only missing pieces** are:
1. An `AutocompletePopup.vue` component (the React version exists in weave-agent-fleet as reference).
2. Wiring in `Composer.vue`: cursor position tracking, passing `instanceId` to `useAutocomplete`, delegating keydown, rendering the popup, and preventing Enter-to-send when autocomplete captures it.

## Objectives
### Core Objective
Enable inline `/command` and `@file`/`@agent` autocomplete in the session composer with keyboard navigation and mouse selection.

### Deliverables
- [x] `AutocompletePopup.vue` — floating popup component
- [x] `Composer.vue` updated to integrate autocomplete
- [x] Keyboard and mouse interaction fully working

### Definition of Done
- [x] Typing `/` at position 0 shows command suggestions filtered by typed text
- [x] Typing `@` (after whitespace or at start) shows agent + file suggestions
- [x] Arrow keys navigate, Enter/Tab selects, Escape dismisses
- [x] Selected item replaces trigger text and positions cursor after insertion
- [x] Popup does not appear when `instanceId` is absent
- [x] No regressions to normal send flow (Enter sends when popup is closed)

### Guardrails (Must NOT)
- Do NOT modify `use-autocomplete.ts` — it is complete and tested
- Do NOT modify `use-find-files.ts` or `api-types.ts`
- Do NOT add new API endpoints — all backend APIs exist
- Do NOT introduce external UI libraries (no cmdk/headless-ui) — use plain Vue with scoped CSS matching existing patterns

## TODOs

- [x] 1. Create `AutocompletePopup.vue`
  **What**: A floating popup component that renders grouped autocomplete items (commands, agents, files) with icons, labels, descriptions, loading/error/empty states, and mouse-click selection. Positioned absolutely above the composer box. Must call `preventDefault()` on `mousedown` to avoid blurring the textarea.
  **Files**: `client/src/components/session/AutocompletePopup.vue`
  **Acceptance**: Component renders items grouped by type with correct icons (Terminal for commands, Bot for agents with optional color dot, FileText/Folder for files). Shows loading spinner, error alert, or "No results" as appropriate. Clicking an item calls `onSelect` with the item's value. `mousedown` is prevented.

  Props interface:
  ```
  open: boolean
  items: AutocompleteItem[]
  isLoading: boolean
  selectedValue: string | null
  error?: string
  onSelect: (value: string) => void
  ```

  UX rules:
  - Group order: commands → agents → files (with separator between groups)
  - Highlighted item driven by `selectedValue` prop (add `aria-selected` + highlight class)
  - Max height 300px with overflow scroll
  - Icons: `Terminal` for commands, `Bot` for agents (with colored dot via `meta`), `FileText`/`Folder` for files (directory when `meta === "dir"`)
  - Scoped CSS using existing CSS custom properties (`--border`, `--card-bg`, `--text`, `--muted`, `--accent`, `--error`)

- [x] 2. Wire autocomplete into `Composer.vue`
  **What**: Add cursor position tracking, instantiate `useAutocomplete`, delegate keydown events, and render `AutocompletePopup` inside the composer box. The composer box (`div.composer-box`) needs `position: relative` for absolute popup positioning.
  **Files**: `client/src/components/session/Composer.vue`
  **Acceptance**: Autocomplete popup appears/disappears correctly. Keyboard navigation works. Enter/Tab selects when popup is open (does NOT send message). Enter sends when popup is closed. Escape closes popup without side effects.

  Specific changes:
  - Import `AutocompletePopup` and `useAutocomplete`
  - Add `cursorPosition` ref, update it in `handleInput` and on `keyup`/`click` events on the textarea
  - Call `useAutocomplete({ value: computed(() => draft.text), setValue: setText, instanceId: props.instanceId ?? "", inputRef: textareaRef, cursorPosition })`
  - In `handleKeydown`: call `autocomplete.onKeyDown(event)` **first**; if `autocomplete.isOpen.value` and event was Enter/Tab/Escape, return early (don't send)
  - Render `<AutocompletePopup>` inside `.composer-box` div, above the textarea
  - Add `position: relative` to `.composer-box` CSS rule

- [x] 3. Verify end-to-end behavior
  **What**: Manual verification of all interaction paths.
  **Acceptance**:
  - `/` at start → command list appears, filters as you type, Enter selects and replaces input
  - `@` after space → agent + file list appears, files searched server-side with debounce
  - Arrow Up/Down wraps around item list
  - Tab selects like Enter
  - Escape closes popup; re-typing resumes autocomplete
  - Clicking an item selects it without blurring textarea
  - Normal Enter-to-send works when popup is closed
  - Shift+Enter still inserts newline
  - No popup when `instanceId` is undefined/empty

## Verification
- [x] `npm run type-check` passes (or equivalent `vue-tsc`)
- [x] `npm run lint` passes
- [x] No regressions — normal message send flow unchanged
- [x] Autocomplete works for `/`, `@agent`, and `@filepath` triggers
