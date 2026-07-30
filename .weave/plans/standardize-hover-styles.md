# Standardize Hover Styles & Icon Button Patterns

## TL;DR
Add a `--transition` CSS custom property and hover utility classes to `main.css`, then sweep 21+ components to use consistent `180ms ease-out` timing, `border-radius: 0`, and the prototype's 3-property icon-button hover pattern (bg + border + color).

## Context
The prototype defines a strict hover vocabulary: `180ms ease-out` for all interactive transitions, `border-radius: 0` on all non-circular elements, and icon buttons that gain background + border + color on hover. The Fleet client currently has timings scattered from 120ms–250ms, radii from 3px–999px, and many elements missing transitions entirely. The design tokens `--radius-btn: 0px` and `--radius-card: 0px` already exist in `main.css` but aren't used consistently.

Key files:
- `client/src/assets/main.css` — global tokens and utilities
- `client/src/assets/transitions.css` — Vue transition classes (not in scope)
- 21 component files listed below

## Scope
- In scope:
  - Add `--transition` custom property to `:root` in `main.css`
  - Add hover utility classes for icon-btn, action-btn, list-item, tab-btn patterns
  - Fix all 21 listed components to use standardized timing, radius, and hover properties
- Out of scope:
  - Vue `<Transition>` animation classes in `transitions.css` (those are page/panel animations, not hover)
  - Circular elements (avatars, status dots) — keep `border-radius: 50%` / `999px`
  - Color palette or theme token changes
  - Component logic or structural changes
- Constraints / assumptions:
  - Keep Tailwind where already used; just fix values
  - Scoped CSS is fine; utilities are optional conveniences
  - `--bg` is only defined on light theme currently — dark themes should use `--main-bg` as the hover background. Verify each theme block has a usable hover-bg value, or add `--bg` to all theme blocks mapping to `--main-bg`.

## Objectives
- Every interactive element transitions at `180ms ease-out`
- Every non-circular interactive element has `border-radius: 0`
- Icon buttons follow the 3-property hover pattern (bg, border-color, color)
- Action buttons follow the 2-property hover pattern (bg, border-color)
- List/tree items hover with `background: var(--main-bg)`

## Dependencies and Order
1. **Task 1 must come first** — it establishes the `--transition` token and `--bg` on all themes that other tasks depend on.
2. **Task 2** adds utility classes that later tasks may optionally reference.
3. **Tasks 3–7** are independent of each other and can be done in any order after tasks 1–2.
4. **Task 8** is final verification.

## Tasks

- [x] 1. Add `--transition` token and `--bg` to all theme blocks
  - **What**: Add `--transition: 180ms ease-out;` to the base `:root` block in `main.css`. Add `--bg` (mapped to `--main-bg` value) to every dark theme block (`:root` default, `weave-classic`, `black`, `nord`, `dracula`, and any others). The light theme already has `--bg: #FAF9F7`. Verify the value matches `--main-bg` in each block.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - `--transition: 180ms ease-out` present in base `:root`
    - Every `[data-theme]` block and the default `:root` has a `--bg` variable
    - No existing variables broken

- [x] 2. ~~Add hover utility classes to `main.css`~~ (skipped — components fix styles inline)
  - **What**: Append `@utility` blocks for the four hover patterns. These are optional helpers — components can also fix styles inline in scoped CSS. Utilities: `icon-btn`, `action-btn`, `list-item-hover`, `tab-btn`. Each sets the base state (transparent bg, correct border, transition) and the `:hover` overrides per the prototype spec. Use `var(--transition)` for all timings.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Four `@utility` blocks appended to `main.css`
    - Each uses `var(--transition)` for timing
    - Icon-btn hover sets background, border-color, and color
    - Action-btn hover sets background and border-color
    - `bun run build` still succeeds (Tailwind v4 `@utility` syntax valid)

- [x] 3. Fix high-priority components — rail and top bar
  - **What**: Update `IconRail.vue` rail buttons: change 250ms → `var(--transition)`, add bg + border hover properties, set `border-radius: 0`. Update `TopBar.vue`: replace Tailwind hover classes with consistent pattern, ensure 180ms timing.
  - **Files**: `client/src/components/layout/IconRail.vue`, `client/src/components/layout/TopBar.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - No hardcoded transition durations other than `var(--transition)` in these files
    - Rail icon buttons show bg + border + color change on hover
    - No `border-radius` values > 0 on non-circular elements
    - Visual: hover state matches prototype (bg fills, border appears, color brightens)

- [x] 4. Fix high-priority components — session list group
  - **What**: Fix `SessionItem.vue` (add transition), `SessionsPanel.vue` (add transition to action buttons), `ProjectGroup.vue` (250ms → `var(--transition)`). All list items get `transition: background var(--transition)` and hover `background: var(--main-bg)`. Action buttons in panels get icon-btn pattern.
  - **Files**: `client/src/components/sessions/SessionItem.vue`, `client/src/components/sessions/SessionsPanel.vue`, `client/src/components/sessions/ProjectGroup.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - All three files use `var(--transition)` for timing
    - No missing hover transitions
    - Session items hover with background change
    - Panel icon buttons have 3-property hover

- [x] 5. Fix high-priority components — composer and activity stream
  - **What**: Fix `Composer.vue` attach-btn and interrupt-btn hover timing to use `var(--transition)`. Fix `ActivityStream.vue`: change jump-to-latest `border-radius: 999px` → `0`, fix delegation links from 140ms → `var(--transition)`.
  - **Files**: `client/src/components/session/Composer.vue`, `client/src/components/session/ActivityStream.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - No hardcoded ms values for hover transitions in either file
    - Jump-to-latest button has `border-radius: 0`
    - All interactive elements transition at 180ms

- [x] 6. Fix medium-priority components — panels and tabs
  - **What**: Fix `RightPanelTabs.vue`, `CollapsedRightRail.vue`, `SettingsNavPanel.vue`, `ArtifactsPanel.vue` — all from 150ms → `var(--transition)`. Fix border-radius values (4px → 0). Tab buttons get the tab-btn hover pattern (color + border-color transition).
  - **Files**: `client/src/components/layout/RightPanelTabs.vue`, `client/src/components/layout/CollapsedRightRail.vue`, `client/src/components/settings/SettingsNavPanel.vue`, `client/src/components/session/ArtifactsPanel.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - All four files use `var(--transition)`
    - No `border-radius` > 0 on non-circular elements
    - Tab buttons transition color and border-color on hover

- [x] 7. Fix medium/low-priority components — remaining files
  - **What**: Fix `ToolCard.vue` (150ms → var), `DiffsTray.vue` (120ms, 7px radius), `FilesChangedFileList.vue` (200ms, 8px), `FilesChangedView.vue` (no transition, 8px), `SessionAnalyticsPopover.vue` (150ms, 6px), `QuestionCard.vue` (150ms, 999px pill → 0, 6px → 0), `SessionCard.vue` (Tailwind shadow hover — normalize timing). For each: replace hardcoded timing with `var(--transition)`, set non-circular border-radius to 0, ensure hover properties match the appropriate pattern (icon-btn, action-btn, or list-item).
  - **Files**: `client/src/components/session/ToolCard.vue`, `client/src/components/session/DiffsTray.vue`, `client/src/components/session/FilesChangedFileList.vue`, `client/src/components/session/FilesChangedView.vue`, `client/src/components/session/SessionAnalyticsPopover.vue`, `client/src/components/session/QuestionCard.vue`, `client/src/components/dashboard/SessionCard.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Zero hardcoded transition durations (grep for `\d+ms` in scoped styles should find none except `var(--transition)` references)
    - Zero non-circular `border-radius` > 0
    - Each interactive element has appropriate hover properties per its pattern type

- [x] 8. Final verification
  - **What**: Run build, grep for remaining violations, visual spot-check.
  - **Depends on**: Tasks 1–7
  - **Acceptance**:
    - `bun run build` succeeds with no errors
    - `rg '\d+ms' --glob '*.vue' client/src/components/` returns zero matches in `<style>` blocks (excluding `var(--transition)` definition)
    - `rg 'border-radius:\s*[1-9]' --glob '*.vue' client/src/components/` returns only circular elements (50%, 999px on dots/avatars)
    - Visual: hover any icon button → bg fills, border appears, color changes; hover any list item → bg fills; hover any tab → color changes

## Verification
```bash
# Build check
bun run build

# No hardcoded transition durations in component styles
rg '\d+ms' --glob '*.vue' -g '!node_modules' client/src/components/

# No non-circular border-radius violations
rg 'border-radius:\s*[1-9]' --glob '*.vue' client/src/components/

# Confirm --transition token exists
rg '\-\-transition' client/src/assets/main.css
```
All commands should pass with expected output as described in Task 8 acceptance.
