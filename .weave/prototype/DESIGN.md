# Weave Fleet Design System

Reference for agents working on the prototype UI at `.weave/prototype/index.html`.

## Visual Language

| Token | Value | Usage |
|-------|-------|-------|
| `--bg` | `#FAF9F7` | Page/panel background |
| `--surface` | `#FFFFFF` | Card, panel, input backgrounds |
| `--border` | `#E8E6E3` | All borders, dividers |
| `--text` | `#1A1918` | Primary text |
| `--muted` | `#6E6A65` | Secondary text, icons, metadata |
| `--coral` | `#D95A3A` | Active nav, brand accent |
| `--indigo` | `#5B6EC7` | Interactive elements, agent color |
| `--radius` | `0px` | All corners are sharp (no rounding) |
| `--transition` | `180ms ease-out` | All state transitions |
| `--font` | Inter | Body text |
| `--mono` | Courier New | Code, paths, badges |

## Principles

1. **Sharp geometry** - 0px border-radius everywhere. No rounded corners.
2. **Quiet chrome** - 1px borders, subtle backgrounds. Content is the focus.
3. **Monospace for machine output** - file paths, code, tool names, badges all use `--mono`.
4. **Muted until hovered** - icons and secondary text use `--muted`, darken on hover.
5. **Indigo = agent/interactive** - agent avatar, active toggles, focused inputs.
6. **Coral = navigation/brand** - active tab indicators, nav highlights.
7. **Green (#7BAF6E) = success/new** - new file dots, added lines in diffs.

## Layout Structure

```
[Icon Rail 48px] [Context Panel 260px] [Center Content flex:1] [Gutter 8px] [Right Panel 420px]
```

All panels have `margin: 8px` gap between them, individual `border: 1px solid var(--border)`.

## Tool Call Rendering

Tool calls in the conversation view use rich, interpreted cards (`.tool-call` class). Each has:

### Read
- Icon: `file-text` (lucide, 16px, muted)
- Header: **Read** + file path (mono, muted) + external-link button
- Preview: single line showing content summary, e.g. `└ 1 ### Next Steps ... (16 lines)`

### Skill
- Icon: `layers` (lucide, 16px, muted)
- Header: **Skill** + `skill: "name"` (mono, muted)
- Preview: `Launching skill: name`

### Write
- Icon: `pencil` (lucide, 16px, muted)
- Header: **Write** + description + external-link button
- Body: diff-style code with line numbers (muted) and green `+` prefix for additions

### Diagram (Mermaid)
- Rendered inline via mermaid.js CDN (theme: neutral)
- Wrapped in `.tool-call` card
- Small expand button (maximize-2) in top-right corner
- Use for sequence diagrams (sequenceDiagram syntax)

### Diagram (Flow/Interactive)
- For non-sequence diagrams: flowcharts, architecture, state machines
- Uses node+edge JSON format (auto-layout)
- Nodes: `{id, label, type?, group?}`
- Edges: `{id, source, target, label?, animated?}`
- Direction: TB, LR, BT, RL

### Legacy Collapsible (still supported)
- `.tool-block` with `.tool-header` (click to toggle) + `.tool-body`
- Used for simple grep/shell output

## Message Structure

```html
<div class="msg">
  <i data-lucide="ICON" class="msg-icon TYPE"></i>
  <div class="msg-body">
    <div class="msg-meta">NAME · TIME</div>
    <div class="msg-text">Content...</div>
    <!-- tool calls go here -->
  </div>
  <div class="msg-actions">...</div>
</div>
```

Icon types: `user` (user icon, --text), `agent` (bot icon, --indigo), `thinking` (brain icon, --muted).

## Hover States

- Messages: left border turns indigo, faint indigo background
- Action buttons appear on message hover
- Tooltips use fixed positioning, dark background, white text

## Dependencies

- **Lucide Icons** - `https://unpkg.com/lucide@latest` (call `lucide.createIcons()` after DOM changes)
- **Mermaid** - `https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js` (theme: neutral, startOnLoad: true)
- **Google Fonts** - Inter (400, 500, 600)
