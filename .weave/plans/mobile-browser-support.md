# Mobile Browser Support

## TL;DR
> **Summary**: Port mobile browser features (responsive navigation, virtual keyboard handling, swipe gestures, responsive dialogs, safe areas, foldable support) from the predecessor React/Next.js app to the current Vue 3/Vite app.
> **Estimated Effort**: Large

## Context
### Original Request
Port 9 categories of mobile browser support from `weave-agent-fleet` (React/Next.js) to the current Vue 3 app: media query hooks, sidebar/nav adaptation, swipe gestures, virtual keyboard detection, responsive dialogs, safe area insets, iOS web app metadata, foldable device support, and mobile-specific UI adaptations.

### Key Findings
- **Media queries**: `use-media-query.ts` already exists with `useIsMobile()`, `useIsMobileNav()`, `useIsTablet()`, `useIsDesktop()` — well-implemented with shared `MediaQueryList` subscription. ✅ Ready to use.
- **Foldable screen**: `use-foldable-screen.ts` already exists with `useFoldableScreen()` composable. ✅ Ready to use.
- **Sidebar store**: `stores/sidebar.ts` has `panelCollapsed`, `togglePanelCollapsed()` but **no mobile drawer state** (`mobileDrawerOpen`, `setMobileDrawerOpen`). The `togglePanelCollapsed` does not distinguish mobile vs desktop behavior.
- **AppShell layout**: Currently `IconRail + ContextPanel + CenterContent` in a flex row. On mobile, IconRail (48px) + ContextPanel (280px) = 328px leaves almost no room. **No mobile adaptation exists.**
- **TopBar**: No hamburger menu button. No mobile-awareness.
- **Sheet component**: shadcn-vue Sheet exists with left/right/top/bottom sides. Can be used for mobile drawer and responsive dialog.
- **Dialog component**: shadcn-vue Dialog exists. Need a `ResponsiveDialog` wrapper.
- **`index.html`**: Viewport meta is `width=device-width, initial-scale=1.0` — missing `viewport-fit=cover`. No iOS web app meta tags.
- **`main.css`**: No safe area utilities, no foldable CSS utilities, no `--visual-vh`, no thin-scrollbar utility.
- **Commands**: `toggle-sidebar` calls `sidebarStore.togglePanelCollapsed()` — needs mobile-aware behavior (toggle drawer on mobile, toggle collapse on desktop).

## Objectives
### Core Objective
Make the Vue 3 app fully usable on mobile browsers (phones, tablets, foldables) with native-feeling navigation, keyboard handling, and responsive components.

### Deliverables
- [ ] Mobile-responsive sidebar navigation (drawer on mobile, inline on desktop)
- [ ] Virtual keyboard detection with `--visual-vh` CSS property
- [ ] Swipe-to-open navigation gesture
- [ ] ResponsiveDialog component (bottom sheet on mobile, dialog on desktop)
- [ ] Safe area CSS utilities and viewport meta fixes
- [ ] iOS web app metadata
- [ ] Mobile-specific UI adaptations (enter key, typography, scrollbars)
- [ ] Foldable device CSS utilities integrated into layout

### Definition of Done
- [ ] App is usable at 360px viewport width (no horizontal overflow, all features accessible)
- [ ] Sidebar renders as sheet drawer below 717px, inline above
- [ ] Virtual keyboard opening does not obscure input fields
- [ ] Swipe from left edge opens navigation drawer
- [ ] `npm run lint` passes
- [ ] `npm run type-check` passes (if available)
- [ ] Manual test on Chrome DevTools mobile emulator (iPhone SE, Pixel 7, Surface Duo)

### Guardrails (Must NOT)
- Must not break desktop layout or behavior
- Must not add new npm dependencies (use existing `@vueuse/core`, shadcn-vue components)
- Must not change the Pinia store API in breaking ways (extend, don't replace)

## TODOs

### Phase 1: Foundation

- [x] 1. Add viewport meta and iOS web app metadata
  **What**: Update `index.html` to add `viewport-fit=cover`, `apple-mobile-web-app-capable`, `apple-mobile-web-app-status-bar-style`, and `apple-mobile-web-app-title` meta tags. Add `theme-color` meta.
  **Files**: `client/index.html`
  **Acceptance**: `index.html` contains `viewport-fit=cover` and all iOS web app meta tags.

- [x] 2. Add CSS utilities: safe areas, foldable, thin-scrollbar, visual-vh default
  **What**: Add to `main.css`:
  - `@utility safe-top/safe-bottom/safe-left/safe-right` using `env(safe-area-inset-*)`
  - `@utility fold-left/fold-right/fold-gap` with `@media (horizontal-viewport-segments: 2)`
  - `:root { --fold-gap: 0px; --visual-vh: 100dvh; }`
  - `@utility no-scrollbar` (hide scrollbar)
  - `@utility thin-scrollbar` (narrow scrollbar styling)
  - Small-screen typography: `@media (max-width: 359px) { html { font-size: 12px; } }`
  **Files**: `client/src/assets/main.css`
  **Acceptance**: Utilities are defined and usable in Tailwind classes. No build errors.

- [x] 3. Create `useVisualViewport` composable
  **What**: Port `use-visual-viewport.ts` from predecessor. On mount, listen to `visualViewport.resize`, `visualViewport.scroll`, and `window.resize`. Set `--visual-vh` on `<html>` to `Math.min(visualViewport.height, window.innerHeight)` in px. Clean up listeners on unmount. Use singleton pattern (shared listener count like foldable screen).
  **Files**: `client/src/composables/use-visual-viewport.ts`
  **Acceptance**: Composable exists, sets `--visual-vh` CSS property. Can verify in DevTools with mobile keyboard emulation.

- [x] 4. Wire `useVisualViewport` into AppShell
  **What**: Call `useVisualViewport()` in `AppShell.vue` setup so it's always active. Update `.app` height from `100vh` to `var(--visual-vh, 100dvh)`.
  **Files**: `client/src/components/layout/AppShell.vue`, `client/src/assets/main.css`
  **Acceptance**: AppShell respects visual viewport height. On desktop, `--visual-vh` equals window height.

### Phase 2: Navigation

- [x] 5. Extend sidebar store with mobile drawer state
  **What**: Add to `useSidebarStore`:
  - `mobileDrawerOpen: shallowRef(false)`
  - `setMobileDrawerOpen(open: boolean)`
  - `isMobileNav: computed(() => useIsMobileNav().value)` — BUT since composables can't be called inside Pinia easily, instead accept `isMobileNav` as a parameter to `togglePanelCollapsed` or create a new `toggleSidebar()` method that checks mobile state.
  
  Better approach: Add `mobileDrawerOpen` and `setMobileDrawerOpen` to the store. Create a separate composable `useSidebarMobile()` that combines the store with `useIsMobileNav()` and provides `toggleSidebar()` with mobile-aware behavior.
  **Files**: `client/src/stores/sidebar.ts`, `client/src/composables/use-sidebar-mobile.ts` (new)
  **Acceptance**: Store has `mobileDrawerOpen` state. `useSidebarMobile()` returns `{ isMobileNav, mobileDrawerOpen, toggleSidebar }`.

- [x] 6. Make AppShell responsive: Sheet drawer on mobile, inline on desktop
  **What**: In `AppShell.vue`:
  - Import `useIsMobileNav` and `useSidebarMobile`
  - When `isMobileNav` is true: hide `IconRail` and `ContextPanel` from inline layout. Render them inside a `<Sheet side="left">` bound to `mobileDrawerOpen`. Sheet width: `280px`, contains `IconRail` + `ContextPanel` in a flex row.
  - When `isMobileNav` is false: render as current (inline).
  - Update `.app` to use `var(--visual-vh, 100dvh)` for height.
  **Files**: `client/src/components/layout/AppShell.vue`
  **Acceptance**: At ≤716px, sidebar renders as a sheet drawer. At ≥717px, renders inline as before.

- [x] 7. Add hamburger menu button to TopBar on mobile
  **What**: In `TopBar.vue`:
  - Import `useIsMobileNav` and sidebar mobile composable
  - When `isMobileNav` is true, render a `<button>` with `Menu` (lucide) icon before the breadcrumb, which calls `setMobileDrawerOpen(true)`
  - Add `aria-label="Open menu"` and `aria-expanded` bound to drawer state
  **Files**: `client/src/components/layout/TopBar.vue`
  **Acceptance**: Hamburger button visible at mobile breakpoint. Tapping opens the sidebar drawer.

- [x] 8. Update ⌘B command to be mobile-aware
  **What**: In `use-commands.ts`, update the `toggle-sidebar` command action: if `isMobileNav`, toggle `mobileDrawerOpen`; otherwise call `togglePanelCollapsed()`. Update label accordingly.
  **Files**: `client/src/composables/use-commands.ts`
  **Acceptance**: ⌘B opens/closes drawer on mobile, collapses/expands panel on desktop.

- [x] 9. Add swipe gesture support
  **What**: Create `client/src/composables/use-swipe-drawer.ts`. Tracks touch events on a target element:
  - `touchstart`: if touch starts in first 24px from left and drawer is closed, record start position
  - `touchend`: if horizontal delta ≥50px and vertical delta <60px, open drawer
  Wire it into `AppShell.vue` on the root `.app` div via template ref.
  **Files**: `client/src/composables/use-swipe-drawer.ts` (new), `client/src/components/layout/AppShell.vue`
  **Acceptance**: Swiping right from left edge opens the navigation drawer on mobile.

### Phase 3: Input & Keyboard

- [x] 10. Auto-scroll input into view when virtual keyboard opens
  **What**: Create `client/src/composables/use-keyboard-scroll.ts`. Watches `--visual-vh` changes (via `visualViewport.resize`). When viewport shrinks (keyboard opens), find `document.activeElement`, if it's an input/textarea, call `scrollIntoView({ block: 'center', behavior: 'smooth' })` after a 50ms debounce.
  Alternatively, integrate this logic into `use-visual-viewport.ts` as an optional behavior.
  **Files**: `client/src/composables/use-keyboard-scroll.ts` (new) or extend `client/src/composables/use-visual-viewport.ts`
  **Acceptance**: When tapping an input field on mobile, the input scrolls into view above the keyboard.

- [x] 11. Mobile enter key behavior for chat input
  **What**: Find the chat input component (likely in session detail). On mobile (`useIsMobile()`), Enter key inserts a newline; a separate Send button submits. On desktop, Enter submits (existing behavior). This is a behavioral change in the keydown handler.
  **Files**: Chat input component (likely `client/src/components/sessions/` or similar — find the component with `@keydown.enter` or equivalent)
  **Acceptance**: On mobile viewport, Enter creates newline. Send button still works. Desktop behavior unchanged.

### Phase 4: Responsive Components

- [x] 12. Create ResponsiveDialog component
  **What**: Create `client/src/components/ui/responsive-dialog/ResponsiveDialog.vue`. Props: `open`, `title`, `class`. Emits: `update:open`.
  - Uses `useIsMobile()` to choose rendering mode
  - Mobile: renders `<Sheet side="bottom">` with `<SheetHeader>/<SheetTitle>` and scrollable content area with `max-h-[calc(var(--visual-vh,100dvh)*0.75)]` and `safe-bottom` padding
  - Desktop: renders `<Dialog>` with `<DialogHeader>/<DialogTitle>`
  - Expose slot for content and optional trigger slot
  **Files**: `client/src/components/ui/responsive-dialog/ResponsiveDialog.vue` (new), `client/src/components/ui/responsive-dialog/index.ts` (new)
  **Acceptance**: Component renders as bottom sheet on mobile, centered dialog on desktop.

- [x] 13. Mobile overflow menus and button adaptations
  **What**: Identify places where inline action buttons overflow on mobile. Wrap them in overflow menus (`DropdownMenu`) on mobile. Key areas:
  - Session detail header actions
  - Board card actions
  - Any toolbar with >3 action buttons
  Use `useIsMobile()` to conditionally render overflow menu vs inline buttons.
  **Files**: Various component files (audit needed — likely `TopBar.vue`, session action bars)
  **Acceptance**: No button overflow at 360px width. Actions accessible via overflow menu.

- [x] 14. Typography and spacing adjustments for small screens
  **What**: Add responsive CSS in `main.css`:
  - `@media (max-width: 359px)` — increase minimum font to 12px if base is 10px
  - Reduce padding/gaps in tight areas on mobile
  - Ensure `.rail` is hidden on mobile (handled by Phase 2, but verify no phantom space)
  **Files**: `client/src/assets/main.css`
  **Acceptance**: Text is readable at 360px width. No clipped or overlapping text.

### Phase 5: Foldable & Polish

- [x] 15. Integrate foldable CSS utilities with layout
  **What**: In `AppShell.vue`, when `useFoldableScreen()` reports `isFolded`:
  - Set `--fold-gap` CSS property on root element based on `foldWidth`
  - Apply `fold-left` / `fold-right` classes to split the layout across screens (sidebar on left screen, content on right screen)
  - On foldable, always show sidebar inline (not as drawer) regardless of width
  **Files**: `client/src/components/layout/AppShell.vue`, `client/src/assets/main.css`
  **Acceptance**: On Surface Duo emulation, layout splits across fold with proper gap.

- [x] 16. Mobile diff viewer: force unified mode
  **What**: Find the diff viewer component. When `useIsMobile()` is true, force unified diff mode (no side-by-side). Override any user preference on mobile.
  **Files**: Diff viewer component (likely in `client/src/components/` — find component with split/unified diff toggle)
  **Acceptance**: Diff viewer always shows unified view on mobile viewports.

- [x] 17. Thin scrollbar utility application
  **What**: Apply `thin-scrollbar` (or `no-scrollbar` where appropriate) to scrollable containers:
  - Main content area
  - Context panel
  - Session list
  - Chat message area
  **Files**: Various layout/panel components
  **Acceptance**: Scrollbars are thin/subtle across the app. No layout shift from scrollbar appearance.

## Verification
- [ ] All existing tests pass (`npm test` / `vitest`)
- [ ] Lint passes (`npm run lint`)
- [ ] No regressions on desktop (1920×1080)
- [ ] Mobile emulation test: iPhone SE (375px) — all features accessible
- [ ] Mobile emulation test: Pixel 7 (412px) — sidebar drawer works
- [ ] Tablet emulation test: iPad (768px) — layout transitions correctly
- [ ] Foldable emulation test: Surface Duo (540px×720px dual) — fold layout works
- [ ] Virtual keyboard test: focus input in mobile emulation, verify no obstruction
