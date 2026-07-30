# Conversation View Prototype Alignment

## TL;DR
Restyle the conversation message list and tool cards to match the static prototype's flat, light-theme design with proper typography, hover effects, and message actions.

## Context
The prototype at `.weave/prototype/index.html` defines a clean flat message list (no bubbles, no clustering). The current `MessageBubble.vue` uses 12px body text, dark-theme hardcoded colors, and cluster positioning. `ToolCard.vue` is functional but visually minimal. The light theme CSS variables already match the prototype palette.

Key files:
- `client/src/components/session/MessageBubble.vue` — message rendering + styles
- `client/src/components/session/ToolCard.vue` — tool call rendering
- `client/src/components/session/ActivityStream.vue` — list rendering, clustering logic
- `client/src/components/session/Composer.vue` — input area (separate step)
- `client/src/assets/main.css` — theme variables

## Scope
- In scope: Message layout/styling, tool card styling, hover effects, copy action, light-theme alignment
- Out of scope: Dark theme rework, Composer restyling, ActivityStream logic refactoring, new data structures
- Constraints / assumptions:
  - Must preserve all existing props/functionality (AccumulatedToolPart, ToolCardItem interfaces)
  - Cluster position prop can remain but visual differentiation removed
  - Light theme is the target; dark theme just needs to not break

## Objectives
- Match prototype message row layout (icon | body | timestamp, border-bottom separator)
- Correct typography (14px body, 1.6 line-height)
- Add hover effects (indigo left border + subtle bg)
- Add copy message action on hover
- Improve tool card visual treatment

## Dependencies and Order
1. Typography and layout first (foundation for everything else)
2. Hover effects and icon coloring second (visual polish on top of layout)
3. Message actions third (new DOM elements, depends on layout being stable)
4. Tool card styling last (independent of message layout changes)

## Tasks

- [x] 1. Message layout and typography
  - **What**: Restyle `.message` and `.msg-layout` to use `padding: 12px; gap: 12px; border-bottom: 1px solid var(--border); border-left: 3px solid transparent`. Change `.msg-body` to `font-size: 14px; line-height: 1.6; color: var(--text)`. Remove dark-theme hardcoded colors (`#d4d4d8`, `#f4f4f5`) in favor of CSS variable references. Move timestamp to right-align within the message row (it already has `margin-left: auto` but bump font to 12px). Remove cluster-based visual differentiation (keep the prop for data but remove the CSS rules that change spacing/borders per cluster position).
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Messages render as flat rows with bottom border separator
    - Body text is 14px with 1.6 line-height
    - No hardcoded dark colors remain; uses `var(--text)` / `var(--muted)`
    - Timestamp appears right-aligned at 12px

- [x] 2. Icon coloring and sizing
  - **What**: Change `.msg-icon__svg` color to `var(--text)` for user messages and `var(--indigo)` for assistant messages. Use scoped selectors `.message--user .msg-icon__svg` and `.message--assistant .msg-icon__svg`.
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - User icon renders in `var(--text)` color
    - Bot icon renders in `var(--indigo)` color

- [x] 3. Hover effects
  - **What**: Add `.message:hover` rule: `border-left-color: var(--indigo); background: rgba(91, 110, 199, 0.03)`. Transition on border-left-color and background.
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: Task 1 (needs border-left: 3px transparent base)
  - **Acceptance**:
    - Hovering a message shows indigo left border and faint blue background
    - Transition is smooth (~150ms)

- [x] 4. Copy message action on hover
  - **What**: Add a copy button (Lucide `Copy` icon) positioned absolute top-right of `.msg-layout`. Hidden by default, visible on `.message:hover`. On click, copy `props.body` to clipboard. Show brief "Copied" tooltip.
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Copy button appears top-right on message hover
    - Clicking copies raw body text to clipboard
    - Button disappears when not hovering

- [x] 5. Tool card visual alignment
  - **What**: Add `background: var(--bg); border: 1px solid var(--border); padding: 10px 12px` to `.tool-card`. Style header with monospace 12px. Replace `rgba(255,255,255,...)` colors in `.tool-output` with `var(--bg)` / `var(--border)` references for theme compatibility.
  - **Files**: `client/src/components/session/ToolCard.vue`
  - **Depends on**: None
  - **Acceptance**:
    - Tool cards have visible border and background matching prototype
    - No hardcoded rgba white colors remain
    - Cards look correct in light theme

- [x] 6. Code block and inline code light-theme fix
  - **What**: Replace hardcoded dark colors in `.msg-body__content :deep(code)`, `:deep(pre)`, `:deep(blockquote)`, `:deep(a)` selectors with CSS variable references. Switch highlight.js import from `github-dark.css` to a light-compatible theme or use CSS variables. Inline code bg: `var(--bg)`, color: `var(--text)`.
  - **Files**: `client/src/components/session/MessageBubble.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Code blocks and inline code readable in light theme
    - No hardcoded `#818cf8`, `rgba(255,255,255,...)` colors
    - Blockquote border uses `var(--border)`

## Verification
Run `cd client && bun run dev:mock`, open `http://localhost:3002` in light theme. Visually compare conversation panel against `.weave/prototype/index.html`. Confirm:
- Flat message list with border separators
- 14px body text, right-aligned timestamps
- Indigo hover effect on messages
- Copy button appears on hover
- Tool cards have bordered card appearance
- No visual regressions in dark theme (colors degrade gracefully via variables)
