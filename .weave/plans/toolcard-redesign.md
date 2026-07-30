# ToolCard Redesign

## TL;DR
Overhaul `ToolCard.vue` to match the prototype's rich tool-call rendering: tool-specific Lucide icons, bold sans-serif labels, indigo pattern pills for glob/grep, preview lines, and subtle status display.

## Context
- `ToolCard.vue` currently uses a `<details>` element with monospace text and a `StatusGlyph`.
- `activity-stream-tool-card.ts` builds `ToolCardItem` from `AccumulatedToolPart.state`.
- `tool-labels.ts` already extracts contextual labels per tool kind.
- `ToolIcon.vue` exists but maps IDE/terminal tool IDs — not agent tool-call kinds. A new icon mapping is needed.
- Lucide Vue Next is already a project dependency, widely used across components.
- The prototype shows a flat card (no disclosure triangle), icon + bold label + muted detail, optional pattern pill, and preview line.

## Scope
- In scope:
  - Restyle `ToolCard.vue` template and CSS to match prototype
  - Add tool-kind → Lucide icon mapping (new utility or inline in ToolCard)
  - Add `preview` field to `ToolCardItem` and populate it in `toToolCardItem()`
  - Format `kind` as Title Case display label in the header
- Out of scope:
  - Changing `AccumulatedToolPart` or upstream data structures
  - Modifying `MessageBubble.vue` or `ActivityStream.vue` (props interface stays the same)
  - External link button (file-open-in-editor) — future enhancement
- Constraints / assumptions:
  - Must work in dark and light themes via CSS variables
  - Keep collapsible body for output/diff but default to expanded
  - Status glyph shown only for Running/Error states

## Objectives
- Match prototype visual design for tool calls
- Add tool-specific icons to the header
- Show glob/grep patterns in styled indigo pills
- Add content preview line below header
- Hide status glyph for completed tools

## Dependencies and Order
1. Create icon mapping first (Task 1) — ToolCard template depends on it.
2. Add `preview` field to `ToolCardItem` (Task 2) — ToolCard template depends on it.
3. Rewrite `ToolCard.vue` template and styles (Task 3) — depends on Tasks 1 & 2.

## Tasks

- [x] 1. Create tool-kind icon mapping
  - **What**: Create a mapping from tool kind strings (`read`, `write`, `edit`, `bash`, `glob`, `grep`, `skill`, `task`, `webfetch`, `question`) to Lucide icon components. Export a function `getToolIcon(kind: string)` that returns the component. Also export a function `getToolDisplayLabel(kind: string)` that returns Title Case labels (e.g., `"read"` → `"Read"`, `"bash"` → `"Bash"`, `"webfetch"` → `"Web Fetch"`). Icon mapping:
    - `read` → `FileText`
    - `write`, `edit` → `Pencil`
    - `glob`, `grep` → `Search`
    - `skill` → `Layers`
    - `bash` → `Terminal`
    - `task` → `GitBranch`
    - `webfetch` → `Globe`
    - `question` → `MessageCircleQuestion`
    - fallback → `Wrench`
  - **Files**: `client/src/lib/tool-icons.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Exports `getToolIcon` returning a Lucide component for known kinds, `Wrench` for unknown
    - Exports `getToolDisplayLabel` returning Title Case string
    - No runtime errors for unknown tool names

- [x] 2. Add `preview` field to ToolCardItem
  - **What**: Add optional `preview?: string` to the `ToolCardItem` interface. In `toToolCardItem()`, populate it by extracting a one-line preview from the output/summary (first non-empty line, truncated to ~80 chars, with total line count appended like `"└ 1 ### Next Steps … (16 lines)"`). Also add `isPatternTool?: boolean` to indicate glob/grep (drives pill rendering).
  - **Files**: `client/src/components/session/activity-stream-tool-card.ts`
  - **Depends on**: None
  - **Acceptance**:
    - `ToolCardItem` has `preview` and `isPatternTool` fields
    - Preview is generated from output's first line with line count
    - Glob and grep tools have `isPatternTool: true`
    - Existing tests (if any) still pass

- [x] 3. Rewrite ToolCard.vue template and styles
  - **What**: Replace the current `<details>`-based template with the prototype layout. Key changes:
    - **Header**: flex row with `<component :is="icon">` (14px), bold sans-serif label (13px, `font-weight: 600`), tool detail in monospace muted (12px, truncated with ellipsis). For glob/grep, wrap the title in a `.tool-call-pattern` pill span. Status glyph only shown when `status === 'Running' || status === 'Error'`.
    - **Preview line**: Below header, show `preview` in monospace muted (12px) if present and body is collapsed.
    - **Body**: Keep `<details>` for collapsible output/diff but hide the native disclosure marker. Default open. The `<summary>` becomes the header row. Or alternatively, use a click-to-toggle div if `<details>` marker suppression is unreliable.
    - **Styles**: Replace all existing styles with prototype CSS (border-radius: 0, padding: 10px 12px, etc.). Remove monospace from label. Keep monospace for detail and preview.
  - **Files**: `client/src/components/session/ToolCard.vue`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Tool-specific Lucide icon renders in header
    - Label is bold, sans-serif, 13px, Title Case
    - Detail (file path/pattern) is monospace, muted, truncated
    - Glob/grep patterns render in indigo pill
    - Preview line shows below header
    - Status glyph hidden for Completed tools
    - Works in both light and dark themes
    - Existing props interface unchanged (new optional props only)
    - Collapsed/expanded toggle still works

- [x] 4. Verify rendering
  - **Depends on**: Task 3
  - **Acceptance**:
    - `bun run type-check` passes
    - `bun run lint` passes
    - Visual inspection in dev server shows tool cards matching prototype layout

## Verification
```bash
cd client
bun run type-check
bun run lint
bun run dev  # visual check in browser
```
Passing: no type errors, no lint errors, tool cards render with icons, bold labels, pills for glob/grep, preview lines, and subtle status.
