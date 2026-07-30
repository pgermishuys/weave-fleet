# Design System Enforcement

## TL;DR
Extend the Button CVA with new variants matching the prototype, add utility classes for non-button interactive patterns, migrate ~100 raw `<button>` usages to the `<Button>` component, and add a lint script to catch future drift.

## Context
The client app (`client/`) uses Vue 3 + Tailwind CSS v4 with shadcn-vue components. The `Button` component at `src/components/ui/button/index.ts` uses CVA with 6 variants and 6 sizes. Only ~20 components import `Button`; the remaining ~30+ components use raw `<button>` elements with hand-written scoped CSS, causing visual inconsistency. The prototype at `.weave/prototype/index.html` is the visual reference.

Key files:
- `client/src/components/ui/button/index.ts` (CVA definition)
- `client/src/components/ui/button/Button.vue` (component)
- `client/src/assets/main.css` (CSS variables, utilities)

## Scope
- In scope:
  - New CVA button variants (`toolbar-icon`, `filter`, `tab`)
  - New CVA sizes (`toolbar`, `toolbar-lg`)
  - Global CSS utility classes for non-button interactive elements
  - Migration of raw `<button>` to `<Button>` across all components
  - Lint script for CI enforcement
  - Brief design system documentation
- Out of scope:
  - Changing existing variant names or breaking current consumers
  - Vue page transition animations
  - Badge component changes (separate effort)
  - Theme/color token changes
- Constraints / assumptions:
  - All radius is 0 (already enforced via `rounded-none` in base class)
  - `var(--transition)` = `180ms ease-out` is the canonical timing
  - `bun` for all tooling
  - Button component uses reka-ui `Primitive`, so `as` prop supports rendering as `<a>` etc.

## Objectives
- Make it harder to write inconsistent UI than consistent UI
- Eliminate hand-written hover/transition/radius CSS for interactive elements
- Automated CI detection of design system drift

## Dependencies and Order
1. Phase 1 (variants) and Phase 2 (utilities) must complete before Phase 3 (migration) starts, because migrations depend on the new variants/utilities existing.
2. Phase 3 is split into 4 independent sub-tasks grouped by area; they can run in parallel.
3. Phase 4 (lint) can run in parallel with Phase 3.
4. Phase 5 (docs) runs last, after variants and utilities are finalized.

## Tasks

- [x] 1. Add new Button CVA variants and sizes
  - **What**: Add three new variants and two new sizes to the `buttonVariants` CVA definition:
    - `toolbar-icon` variant: `border border-transparent bg-transparent text-muted hover:bg-main-bg hover:border-border hover:text-text transition-[background,color,border-color] duration-[var(--transition)]`
    - `filter` variant: `bg-transparent text-muted text-[11px] hover:bg-main-bg hover:text-text data-[active=true]:bg-accent-dim data-[active=true]:text-accent transition-[background,color] duration-[var(--transition)]`
    - `tab` variant: `bg-transparent border-b-2 border-transparent text-muted text-[12px] font-medium hover:text-text data-[active=true]:text-text data-[active=true]:border-b-accent rounded-none transition-[color,border-color] duration-[var(--transition)]`
    - `toolbar` size: `size-7` (28px)
    - `toolbar-lg` size: `size-8` (32px)
    
    Verify Tailwind v4 supports `duration-[var(--transition)]` syntax; if not, use `duration-[180ms] ease-out` directly. Keep the existing `transition-all` in the base class but override per-variant with scoped `transition-[...]` for performance.
  - **Files**: `client/src/components/ui/button/index.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Existing variants (`default`, `destructive`, `outline`, `secondary`, `ghost`, `link`) unchanged
    - Existing sizes (`default`, `sm`, `lg`, `icon`, `icon-sm`, `icon-lg`) unchanged
    - `buttonVariants({ variant: 'toolbar-icon', size: 'toolbar' })` returns correct class string
    - `bun run build` in `client/` succeeds
    - TypeScript types auto-update (CVA infers variant union)

- [x] 2. Add global CSS utility classes for non-button interactive patterns
  - **What**: Add Tailwind v4 `@utility` blocks to `main.css` for interactive elements that can't use the Button component (list items, nav items, anchor-based tabs):
    - `list-item-hover`: `background: transparent; transition: background var(--transition); &:hover { background: var(--main-bg); }`
    - `tab-hover`: `color: var(--muted); border-bottom: 2px solid transparent; transition: color var(--transition), border-color var(--transition); &:hover { color: var(--text); } &[data-active=true], &.active { color: var(--text); border-bottom-color: var(--coral); }`
    - `interactive-icon`: `color: var(--muted); transition: color var(--transition); &:hover { color: var(--text); }` (for standalone icons that aren't buttons)
  - **Files**: `client/src/assets/main.css`
  - **Depends on**: None
  - **Acceptance**:
    - Utilities are available in Tailwind classes (e.g., `class="list-item-hover"`)
    - No existing utility names conflict
    - `bun run build` succeeds

- [x] 3. Migrate session and composer components
  - **What**: Replace raw `<button>` + scoped CSS with `<Button>` component in session-area components. For each file: import `Button`, replace `<button>` tags with `<Button variant="..." size="...">`, delete corresponding scoped CSS rules, verify visual parity against the prototype.
  - **Files**:
    - `client/src/components/session/Composer.vue` (7 buttons)
    - `client/src/components/layout/TopBar.vue` (2 buttons)
    - `client/src/components/layout/RightPanelTabs.vue` (2 buttons, likely `tab` variant)
    - `client/src/components/layout/AppShell.vue` (1 button)
    - `client/src/components/layout/CollapsedRightRail.vue` (1 button)
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - No raw `<button>` elements remain in listed files (except reka-ui slot usage)
    - Corresponding scoped CSS hover/transition rules deleted
    - Visual appearance matches prototype
    - `bun run build` succeeds

- [x] 4. Migrate layout and navigation components
  - **What**: Same migration pattern as Task 3, applied to layout/navigation components.
  - **Files**:
    - `client/src/components/layout/IconRail.vue` (3 buttons, `toolbar-icon` variant)
    - `client/src/components/analytics/AnalyticsTabs.vue` (tab buttons)
    - `client/src/components/analytics/tabs/SessionsTab.vue` (1 button)
    - `client/src/components/automations/AutomationCard.vue` (4 buttons)
    - `client/src/components/auth/LoginPage.vue` (1 button)
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - No raw `<button>` elements remain in listed files
    - Scoped CSS hover/transition rules deleted
    - Visual appearance matches prototype
    - `bun run build` succeeds

- [x] 5. Migrate GitHub plugin and page components
  - **What**: Same migration pattern for GitHub-related plugin and page components.
  - **Files**:
    - `client/src/plugins/builtin/github/GitHubPanel.vue` (4 buttons)
    - `client/src/plugins/builtin/github/GitHubSettings.vue` (10 buttons)
    - `client/src/plugins/builtin/github/components/IssueFilterBar.vue` (2 buttons, `filter` variant)
    - `client/src/plugins/builtin/github/components/filters/SortControl.vue` (1 button, `filter` variant)
    - `client/src/plugins/builtin/github/components/filters/MilestoneFilter.vue` (1 button, `filter` variant)
    - `client/src/plugins/builtin/github/components/filters/LabelFilter.vue` (1 button, `filter` variant)
    - `client/src/components/pages/GitHubWorkItemDetailPage.vue` (2 buttons)
    - `client/src/components/pages/GitHubRepoPage.vue` (10 buttons)
    - `client/src/components/pages/GitHubBrowserPage.vue` (2 buttons)
    - `client/src/plugins/builtin/smart-links/SmartLinksPanel.vue` (1 button)
    - `client/src/plugins/builtin/smart-links/SmartLinkItem.vue` (5 buttons)
    - `client/src/plugins/builtin/marketplace/MarketplacePanel.vue` (1 button)
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - No raw `<button>` elements remain in listed files
    - Scoped CSS hover/transition rules deleted
    - `bun run build` succeeds

- [x] 6. Migrate board, onboarding, and remaining components
  - **What**: Same migration pattern for board (kanban), onboarding, and any remaining components.
  - **Files**:
    - `client/src/components/board/KanbanBoard.vue` (13 buttons)
    - `client/src/components/board/KanbanColumn.vue` (9 buttons)
    - `client/src/components/board/KanbanCard.vue` (4 buttons)
    - `client/src/components/board/BoardSourceConfig.vue` (3 buttons)
    - `client/src/components/board/BoardControlsPanel.vue` (1 button)
    - `client/src/components/onboarding/OnboardingWizard.vue` (6 buttons)
    - `client/src/components/pages/PipelinesPage.vue` (1 button)
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - No raw `<button>` elements remain in listed files
    - Scoped CSS hover/transition rules deleted
    - `bun run build` succeeds

- [x] 7. Create design system lint script
  - **What**: Create a lint script that detects design system violations and exits non-zero on failure. The script should check:
    1. Raw `<button` in `.vue` files not inside `components/ui/` (allowlist for reka-ui slot patterns like `<button v-bind="...">`)
    2. Hardcoded `border-radius` values > 0 in `<style>` blocks of `.vue` files
    3. Hardcoded transition durations (e.g., `150ms`, `200ms`, `0.2s`) in `<style>` blocks that don't use `var(--transition)`
    4. Summary report with file:line for each violation
    
    Add a `"lint:design"` script to `package.json`.
  - **Files**:
    - `client/scripts/lint-design-system.ts`
    - `client/package.json` (add script entry)
  - **Depends on**: None (can run in parallel with migration tasks)
  - **Acceptance**:
    - `bun run lint:design` exits 0 when no violations exist
    - `bun run lint:design` exits 1 and lists violations when raw buttons / hardcoded values are found
    - Allowlist mechanism works (UI primitives in `components/ui/` excluded)
    - Script runs in under 5 seconds

- [x] 8. Document the design system
  - **What**: Create a brief design system reference documenting available variants, utility classes, when to use each pattern, and how to add new patterns. Reference the prototype as the visual source of truth.
  - **Files**: `client/DESIGN_SYSTEM.md`
  - **Depends on**: Tasks 1, 2, 7
  - **Acceptance**:
    - Documents all button variants with usage guidance
    - Documents utility classes with usage guidance
    - References `.weave/prototype/index.html` as visual reference
    - Explains how to add a new pattern
    - Explains the lint script and how to run it

- [x] 9. Final verification
  - **What**: Run full build and lint to confirm zero regressions and zero design system violations.
  - **Depends on**: Tasks 1-8
  - **Acceptance**:
    - `bun run build` succeeds
    - `bun run lint:design` exits 0
    - `bunx vue-tsc --noEmit` succeeds (type check)

## Verification
Run the following from `client/`:
```bash
bun run build
bun run lint:design
bunx vue-tsc --noEmit
```
All three commands should exit 0. The `lint:design` output should show no violations.

### Pitfalls
- **Tailwind v4 `duration-[var(--transition)]`**: Tailwind v4 may not support CSS custom properties in `duration-[]`. Test this in Task 1; fallback is `[transition:background_180ms_ease-out,color_180ms_ease-out]` arbitrary property syntax or defining the transition in the variant class directly.
- **`transition-all` in CVA base class**: The existing base class has `transition-all` which may conflict with per-variant `transition-[...]`. Remove `transition-all` from the base and add explicit transition properties to each existing variant, or leave it and let per-variant classes override.
- **reka-ui slot buttons**: Some components use `<button>` as a slot child for reka-ui primitives (e.g., `DialogTrigger`, `DropdownMenuTrigger`). These should use `asChild` on the trigger and wrap with `<Button>` instead, or be allowlisted in the lint script.
- **Board components**: KanbanBoard/KanbanColumn have many buttons; some may be contextual actions that need careful variant selection. Review each against the prototype before choosing a variant.
