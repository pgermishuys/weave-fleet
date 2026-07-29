# Prototype Alignment

## TL;DR
Align the Fleet Vue client's UI components with the static prototype at `.weave/prototype/index.html` — focusing on button styling, status pills, input fields, toggles, select dropdowns, and light theme completeness. The directory picker already exists and is close; minor refinements needed.

## Context
The prototype defines a flat, borderless, 0px-radius design system with specific hover states. The Fleet client uses shadcn-vue (reka-ui) with CVA variants and Tailwind CSS v4. Most colour tokens already match in the light theme. The main gaps are:

1. **Buttons**: CVA variants use shadcn defaults (rounded, shadow) rather than the prototype's flat bordered aesthetic
2. **Badge/Pill**: `badgeVariants` uses `rounded-full` — prototype pills use `border-radius: 0` with coloured borders
3. **Status pills in TopBar**: Use `border-radius: 20px` — should be `0` to match prototype
4. **Input fields**: Use `shadow-xs` and `border-input` — prototype uses no shadow, `border-radius: 0`
5. **Select trigger**: Has shadow and unspecified radius — should match prototype
6. **Switch/Toggle**: Uses `rounded-full` — prototype toggle also uses rounded (10px), so this is fine
7. **Light theme**: Missing `--transition` equivalent; also `--bg` alias not mapped as a Tailwind colour

Key files:
- `client/src/assets/main.css` — CSS variables and base styles
- `client/src/components/ui/button/index.ts` — button CVA variants
- `client/src/components/ui/badge/index.ts` — badge CVA variants
- `client/src/components/ui/input/Input.vue` — input component
- `client/src/components/ui/select/SelectTrigger.vue` — select trigger
- `client/src/components/ui/switch/Switch.vue` — toggle switch
- `client/src/components/layout/TopBar.vue` — status pills
- `client/src/components/ui/DirectoryPickerPopover.vue` — directory picker
- `.weave/prototype/index.html` — reference prototype

## Scope
- In scope:
  - Button variant styling (all variants: default, outline, ghost, destructive, secondary)
  - Badge/pill variant styling
  - Status pill styling in TopBar
  - Input and select component styling
  - Light theme CSS variable completeness
  - Directory picker minor style alignment
- Out of scope:
  - Dark theme changes (only light theme is the prototype reference)
  - Font changes (Inter and JetBrains Mono are intentional Fleet choices — keep them)
  - New component creation (directory picker already exists)
  - Functional/behavioural changes
  - Switch/toggle (prototype uses rounded toggles too — already aligned)
- Constraints / assumptions:
  - All radius values should use `var(--radius-btn)` / `var(--radius-card)` CSS vars (already `0px`)
  - Changes must work across all themes, not just light — use CSS vars not hardcoded colours
  - Tailwind v4 `@theme inline` system is used for colour bridging

## Objectives
- Every button variant renders with 0px radius and matches prototype hover states
- Status pills render with 0px radius and bordered style matching prototype
- Input fields and select triggers render with 0px radius, no shadow
- Light theme CSS variables are complete relative to prototype
- Badge component uses 0px radius matching prototype pills

## Dependencies and Order
1. Light theme CSS vars first (Task 1) — downstream components need correct vars
2. Button, badge, input, select can be done in parallel (Tasks 2-5)
3. Status pill and directory picker refinements last (Tasks 6-7) as they depend on updated base components
4. Verification last (Task 8)

## Tasks

- [x] 1. Complete light theme CSS variables
  - **What**: Add missing prototype tokens to `:root[data-theme="light"]` in `main.css`. Specifically: add `--bg` alias if not present (prototype uses `--bg` extensively — Fleet already has `--main-bg` mapped to the same value, but some components may reference `--bg` directly). Also verify `--surface` is not needed (confirmed: Fleet doesn't use it). Ensure `--radius-card`, `--radius-btn`, `--radius-panel` are all `0px` in `:root` (already are). Add a `--color-surface` to the `@theme inline` block mapping to `--panel-bg` for components that need the prototype's `--surface` concept.
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - All prototype `:root` variables have Fleet equivalents in `[data-theme="light"]`
    - No hardcoded prototype colours leak into component files

- [x] 2. Align button variants
  - **What**: Update `buttonVariants` CVA definition to ensure 0px radius and prototype-matching hover states. Add `rounded-none` (or use the `--radius-btn` var) to the base classes. For `default` variant: match prototype's primary button (`bg-accent text-white border border-accent hover:bg-accent/85`). For `outline` variant: match prototype's cancel button (`border-border bg-panel-bg hover:bg-main-bg`). For `ghost` variant: match prototype's icon button (`border border-transparent hover:bg-main-bg hover:border-border`). Remove `shadow-xs` from all variants.
  - **Files**: `client/src/components/ui/button/index.ts`
  - **Depends on**: Task 1
  - **Acceptance**:
    - All button variants render with 0px border-radius
    - `default` variant has accent background with white text, bordered
    - `outline` variant has transparent background with border, hover shows `--main-bg`
    - `ghost` variant has transparent border that appears on hover
    - No `shadow-xs` on any button variant
    - Existing button usages across the app don't break

- [x] 3. Align badge/pill variants
  - **What**: Update `badgeVariants` CVA definition. Change `rounded-full` to `rounded-none` in base classes. Add a new `status` variant for session status pills matching prototype's `.pill` pattern: `border border-current bg-transparent`. Add sub-variants or keep using the existing pattern where status-specific colours are applied via additional classes. The prototype pills: `.pill-running` (indigo border/text), `.pill-idle` (border/muted), `.pill-complete` (green), `.pill-waiting` (coral).
  - **Files**: `client/src/components/ui/badge/index.ts`, `client/src/components/ui/badge/Badge.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Badge base renders with 0px radius
    - A `status` variant exists for bordered, transparent-background status pills
    - Existing badge usages don't break

- [x] 4. Align input component
  - **What**: Remove `shadow-xs` from the Input component's class string. Ensure `rounded-none` or `rounded-[var(--radius-btn)]` is applied (Tailwind v4: check if `border` already uses 0 radius from the `--radius-btn` var or if explicit `rounded-none` is needed). Match prototype: `border: 1px solid var(--border); border-radius: 0; focus: border-color var(--indigo)`.
  - **Files**: `client/src/components/ui/input/Input.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Input renders with 0px radius
    - No box shadow on input
    - Focus state shows accent-coloured border

- [x] 5. Align select trigger
  - **What**: Remove `shadow-xs` from SelectTrigger class string. Add `rounded-none`. Match prototype's select: `border: 1px solid var(--border); border-radius: 0`.
  - **Files**: `client/src/components/ui/select/SelectTrigger.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Select trigger renders with 0px radius
    - No box shadow
    - Consistent with input field styling

- [x] 6. Align TopBar status pills
  - **What**: Change `.status-pill` `border-radius: 20px` to `border-radius: 0` in TopBar scoped styles. Verify the pill colours map to prototype equivalents: running=indigo (not green), idle=muted/border, complete=green, waiting/error=coral. The prototype uses a different colour mapping than Fleet's current one (prototype: running=indigo, Fleet: running=green). Decide whether to align colours to prototype or keep Fleet's semantic colours. Recommendation: keep Fleet's colour choices (green=running is more intuitive) but fix the radius.
  - **Files**: `client/src/components/layout/TopBar.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Status pills render with 0px radius
    - Pills have bordered, transparent-background style matching prototype pattern

- [x] 7. Refine directory picker styling
  - **What**: The `DirectoryPickerPopover.vue` already exists and is functional. Minor alignment: ensure the popover content uses `rounded-none` (check if `PopoverContent` respects `--radius-card`). Check if `hover:bg-white/[0.06]` on entries works well in light theme — should use `hover:bg-main-bg` instead for theme-agnostic behaviour. Also verify `shadow-xl shadow-black/50 ring-1 ring-white/[0.08]` on the popover content is appropriate for light theme (may look too heavy). Consider using `shadow-lg` with theme-aware ring colour.
  - **Files**: `client/src/components/ui/DirectoryPickerPopover.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Popover renders with 0px radius
    - Hover states work correctly in both light and dark themes
    - Shadow/ring doesn't look jarring in light theme

- [x] 8. Visual verification
  - **What**: Run the dev server (`bun run dev` in `client/`), switch to light theme, and compare against prototype. Check: buttons in New Session form (Create/Cancel), status pills on session list, input fields, select dropdowns, directory picker popover, badge components. Confirm no regressions in dark themes.
  - **Depends on**: Tasks 1-7
  - **Acceptance**:
    - Light theme visually matches prototype for all common UI patterns
    - No regressions in default dark theme or other theme variants
    - All interactive states (hover, focus, disabled) are correct

## Verification
```bash
cd client
bun run dev
```
Open the app in browser, switch to light theme via settings. Compare each UI element against `.weave/prototype/index.html` opened in another tab. Key checkpoints:
1. New Session form: Create button (accent bg, white text, 0 radius), Cancel button (bordered, 0 radius)
2. Session list: status pills (0 radius, bordered)
3. Input fields: 0 radius, no shadow, accent border on focus
4. Select dropdowns: 0 radius, no shadow
5. Directory picker: popover with 0 radius, theme-appropriate hover states
6. Switch themes to dark/other variants — no visual regressions

Also run type checking:
```bash
bunx vue-tsc --noEmit
```
