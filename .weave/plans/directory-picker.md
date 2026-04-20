# DirectoryPicker Component

## TL;DR
> **Summary**: Create a `DirectoryPicker.vue` component that replaces the plain text input in `NewSessionDialog.vue` with a visual directory browser using a popover-based UI.
> **Estimated Effort**: Short

## Context
### Original Request
Add a DirectoryPicker component for session creation that provides visual directory browsing instead of a plain text input.

### Key Findings
- **Composable exists** at `client/src/composables/use-directory-browser.ts` — returns `entries`, `currentPath`, `parentPath`, `roots`, `isLoading`, `error`, `search`, `browse`, `goUp`, `setSearch`, `hasActivated`, `canBrowse`, `refresh`
- **React reference** at `weave-fleet/client/src/components/session/directory-picker.tsx` (291 lines) — port this UX
- **All required shadcn-vue components exist**: `Popover`, `Command` (with `CommandInput`, `CommandList`, `CommandItem`, `CommandGroup`, `CommandEmpty`), `Button`, `Input`, `ScrollArea`
- **Lucide icons** already used via `lucide-vue-next`
- **DirectoryEntry** type: `{ name, path, isGitRepo }`
- **Current integration point**: `NewSessionDialog.vue` lines 462-470, plain `<Input>` bound to `v-model="directory"`
- The composable takes an `enabled` boolean param (not a ref) — the component should call `browse(null)` when popover opens to activate

## Objectives
### Core Objective
Port the React DirectoryPicker to Vue 3 with identical UX, then wire it into `NewSessionDialog.vue`.

### Deliverables
- [ ] `DirectoryPicker.vue` component
- [ ] Integration into `NewSessionDialog.vue`

### Definition of Done
- [ ] Browsing directories works via popover with breadcrumbs, search, and folder navigation
- [ ] Selecting a directory updates the `v-model` and closes the popover
- [ ] `npm run type-check` passes
- [ ] `npm run build` succeeds

### Guardrails (Must NOT)
- Do not modify `use-directory-browser.ts` composable
- Do not change the API contract
- Do not add new dependencies

## TODOs

- [ ] 1. Create DirectoryPicker.vue
  **What**: New SFC at `client/src/components/sessions/DirectoryPicker.vue`. Port the React reference 1:1 in Vue 3 Composition API with `<script setup lang="ts">`. Structure:
  - **Props**: `modelValue: string`, `placeholder?: string`, `disabled?: boolean`, `id?: string`
  - **Emits**: `update:modelValue`
  - **Template layout** (matching React reference):
    - Outer `div.flex.gap-1.5` containing:
      1. `<Input>` bound to `modelValue` with `@update:model-value` emit, `class="flex-1"`
      2. `<Popover v-model:open="popoverOpen">` with `<PopoverTrigger asChild>` wrapping a `<Button variant="outline" size="icon">` with `<FolderOpen>` icon
      3. `<PopoverContent align="end" side="bottom" class="p-0">` containing:
         - **Breadcrumb bar** (conditional on `currentPath !== null`): "Roots" button + `v-for` crumbs with `<ChevronRight>` separators, each clickable via `browse(crumb.path)`
         - **Command palette**: `<Command :should-filter="false">` with `<CommandInput>` bound to search via `setSearch`, `<CommandList class="max-h-[250px]">` containing loading/error/empty states and `<CommandGroup>` with "..(up)" item + directory entries
         - Each entry: `<CommandItem @select="browse(entry.path)">` with `<Folder>` icon, name, `<GitBranch>` icon if `isGitRepo`, and a "Select" `<Button>` that emits the path and closes popover
         - **Footer**: "Use this directory" `<Button>` that selects `currentPath`
  - **Script logic**:
    - `const popoverOpen = shallowRef(false)`
    - `const browser = useDirectoryBrowser()` — call `browser.browse(null)` on first popover open via `watch(popoverOpen, (open) => { if (open && !browser.hasActivated.value) browser.browse(null) })`
    - Computed `breadcrumbs` — same logic as React: find matching root, split path into segments with accumulated paths
    - `handleSelect(path)`: emit `update:modelValue`, close popover
    - Use `useTemplateRef` for popover width matching (measure container `offsetWidth` on open)
  **Files**: `client/src/components/sessions/DirectoryPicker.vue`
  **Acceptance**: Component renders, type-checks, and matches React reference UX

- [ ] 2. Wire DirectoryPicker into NewSessionDialog.vue
  **What**: Replace lines 462-470 in `NewSessionDialog.vue`:
  - Add import: `import DirectoryPicker from "@/components/sessions/DirectoryPicker.vue"`
  - Replace the `<Input id="new-session-directory" v-model="directory" ...>` with `<DirectoryPicker id="new-session-directory" v-model="directory" placeholder="/absolute/path/to/workspace" :disabled="isCreating" />`
  - Keep the surrounding `<div v-else class="space-y-2">` and `<label>` unchanged
  **Files**: `client/src/components/sessions/NewSessionDialog.vue`
  **Acceptance**: Directory section shows input + browse button; selecting a directory populates the field

## Verification
- [ ] `npm run type-check` passes
- [ ] `npm run build` succeeds
- [ ] Manual test: open New Session dialog, switch to directory source, click browse, navigate folders, select one — path appears in input
