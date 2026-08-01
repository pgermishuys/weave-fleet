# Rich Tool Result Rendering

## TL;DR
Add visual rendering of tool results containing typed JSON payloads (`$type` discriminator) — supporting Mermaid diagrams, sanitized HTML, and rendered Markdown — inline in ToolCard and expandable in the content panel.

## Context
Tool results currently render as `<pre><code>` text in `ToolCard.vue`. The `activity-stream-tool-card.ts` extracts output via `getToolOutput()` and passes it as a string. We want to detect JSON payloads with a `$type` field and route them to specialized renderers. `markdown-it` is already installed. DOMPurify and Mermaid need adding.

**End-to-end flow**: User asks agent for a diagram → agent calls the `visualize` tool (producer) → tool returns `$type`-discriminated JSON → harness relays opaquely through Foundry → SignalR → client `getToolOutput()` extracts the JSON string → `parseVisualPayload()` detects `$type` → registry resolves renderer component → renders inline in ToolCard. Neither Foundry nor the weave-fleet server inspect or transform the payload; detection is purely client-side.

**Foundry reference**: This pattern is proven in `C:\source\Foundry\.opencode\tools\visualize.ts` + `Foundry.Web\src\utils\visualRenderers.ts`. We are porting the same architecture.

Key existing files:
- `client/src/components/session/activity-stream-tool-card.ts` — builds `ToolCardItem` from `AccumulatedToolPart`
- `client/src/components/session/ToolCard.vue` — renders tool output (line 135-139: `<pre>` block)
- `client/src/lib/markdown-renderer.ts` — existing markdown-it setup with highlight.js

## Scope
- In scope: `visualize` opencode plugin tool (producer); `visual/sequence` (Mermaid), `html`, `markdown` type renderers; sanitization layer; inline + content panel rendering; parse/detect logic
- Out of scope: `visual/flow` renderer (Vue Flow — tool emits it but client falls back to raw JSON until a future plan); server-side changes; new event types
- Constraints / assumptions:
  - All HTML output MUST pass through a single DOMPurify-based sanitization gateway
  - Prototype pollution protection on JSON parse
  - Mermaid strict mode, SVG sanitization, 50KB limit

## Objectives
- Detect `$type` discriminator in tool output JSON
- Render Mermaid diagrams, HTML, and Markdown inline
- Provide "open in panel" to push content to the `[content]` panel
- Enforce security requirements from Warp audit at every render path

## Dependencies and Order
0. Visualize tool (producer) — independent, no client deps. Can be done in parallel with 1-3.
1. Sanitization module first — all renderers depend on it.
2. Parse/detect logic second — renderers and ToolCard integration depend on it.
3. Individual renderers (parallel) — they only depend on (1) and (2).
4. ToolCard integration — depends on (2) and (3).
5. Content panel integration — depends on (4).

## Tasks

- [x] 0. Port `visualize` tool from Foundry
  - **What**: Create an opencode plugin tool that agents can call to produce structured visual payloads. This is the **producer** that emits `$type` discriminated JSON for the client renderers to consume. Supports two modes: `sequence` (Mermaid DSL) and `flow` (interactive node-edge JSON). The tool validates flow JSON and returns `{ "$type": "visual/{type}", "content": ..., "title?": ... }`.
  - **Files**: `.opencode/tools/visualize.ts`
  - **Depends on**: None
  - **Source reference**: `C:\source\Foundry\.opencode\tools\visualize.ts`
  - **Interface**:
    ```typescript
    args: {
      type: "sequence" | "flow"
      content: string  // Mermaid DSL or JSON {nodes, edges, direction}
      title?: string
    }
    // Returns: JSON string with $type, content, title
    ```
  - **Acceptance**:
    - Tool is registered and callable by the agent
    - `type: "sequence"` returns `{ "$type": "visual/sequence", "content": "<mermaid DSL>", "title": "..." }`
    - `type: "flow"` parses JSON content into object and returns `{ "$type": "visual/flow", "content": { nodes, edges, direction }, "title": "..." }`
    - Invalid flow JSON returns an error response

- [x] 1. Install dependencies
  - **What**: Add `dompurify` and `mermaid` to client package. Add `@types/dompurify` to devDependencies.
  - **Files**: `client/package.json`
  - **Depends on**: None
  - **Acceptance**:
    - `bun install` succeeds
    - `import DOMPurify from 'dompurify'` resolves without error

- [x] 2. Create sanitization gateway
  - **What**: Single `sanitizeHtml(raw: string): string` function using DOMPurify with the config below. Protocol allowlist `^(https?|mailto):`. Add a DOMPurify `afterSanitizeAttributes` hook to force `rel="noopener noreferrer"` on all `<a>` tags with `target` attribute. Export from a dedicated module.
  - **Files**: `client/src/lib/sanitize-html.ts`
  - **Depends on**: Task 1
  - **Config**:
    ```typescript
    const SANITIZE_CONFIG = {
      ALLOWED_TAGS: [
        // Block
        'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'div', 'blockquote', 'pre', 'hr', 'br',
        // Inline
        'a', 'strong', 'em', 'code', 'span', 'img',
        // Lists
        'ul', 'ol', 'li',
        // Tables
        'table', 'thead', 'tbody', 'tr', 'th', 'td',
        // SVG (for Mermaid output)
        'svg', 'g', 'path', 'rect', 'circle', 'ellipse', 'line', 'polyline', 'polygon',
        'text', 'tspan', 'defs', 'clipPath', 'marker', 'foreignObject', 'use',
      ],
      ALLOWED_ATTR: [
        // HTML
        'href', 'title', 'target', 'rel', 'class', 'src', 'alt', 'width', 'height',
        // SVG
        'd', 'fill', 'stroke', 'stroke-width', 'transform', 'viewBox', 'xmlns',
        'x', 'y', 'cx', 'cy', 'r', 'rx', 'ry', 'x1', 'y1', 'x2', 'y2',
        'points', 'font-size', 'font-family', 'text-anchor', 'dominant-baseline',
        'clip-path', 'marker-end', 'marker-start', 'id', 'style',
      ],
      ALLOWED_URI_REGEXP: /^(?:(?:https?|mailto):)/i,
      ALLOW_UNKNOWN_PROTOCOLS: false,
    }
    ```
  - **Acceptance**:
    - Strips `<script>`, `<iframe>`, `javascript:` URIs
    - Allows only specified tags/attrs
    - Forces `rel="noopener noreferrer"` on `<a>` tags with `target` attribute
    - Rejects `<input>` elements
    - Unit tests pass in `client/src/lib/__tests__/sanitize-html.test.ts`

- [x] 3. Create `parseVisualPayload` utility
  - **What**: Parse raw string as JSON, check for `$type` field matching `visual/sequence`, `html`, or `markdown`. Strip `__proto__` and `constructor` keys recursively. Return `VisualPayload | null` if not a visual payload.
  - **Files**: `client/src/lib/visual-payload.ts`
  - **Depends on**: None (no runtime deps on sanitizer)
  - **Interface**:
    ```typescript
    export interface VisualPayload {
      $type: 'visual/sequence' | 'html' | 'markdown'
      content: string
      title?: string
    }
    ```
  - **Acceptance**:
    - Returns `VisualPayload` for valid payloads
    - Returns `null` for plain text, invalid JSON, unknown `$type`
    - Strips prototype pollution keys
    - Unit tests in `client/src/lib/__tests__/visual-payload.test.ts`

- [x] 4. Create visual renderer registry
  - **What**: Map `$type` string → Vue component. Export `getVisualRenderer(type: string): Component | null`. Registry is a plain `Record<string, Component>`.
  - **Files**: `client/src/lib/visual-renderer-registry.ts`
  - **Depends on**: Tasks 5, 6, 7 (but can stub initially)
  - **Acceptance**:
    - Returns correct component for each registered type
    - Returns `null` for unknown types

- [x] 5. Create MermaidRenderer component
  - **What**: Vue component accepting `content: string` prop. Validates length ≤ 50KB. Renders Mermaid with `securityLevel: 'strict'`, `htmlLabels: false`. Sanitizes SVG output via `sanitizeHtml` before inserting with `v-html`.
  - **Files**: `client/src/components/visual-renderers/MermaidRenderer.vue`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Renders valid sequence diagram SVG
    - Rejects input > 50KB with fallback to `<pre>`
    - SVG is sanitized (no inline scripts)
    - Uses `v-html` only with sanitized output

- [x] 6. Create HtmlRenderer component
  - **What**: Vue component accepting `content: string` prop. Passes through `sanitizeHtml()` then renders via `v-html`.
  - **Files**: `client/src/components/visual-renderers/HtmlRenderer.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Renders allowed HTML tags correctly
    - Strips dangerous content
    - Same sanitization as all other paths

- [x] 7. Create MarkdownRenderer component
  - **What**: Vue component accepting `content: string` prop. Renders via existing `markdown-renderer.ts` then sanitizes HTML output via `sanitizeHtml()` before `v-html`.
  - **Files**: `client/src/components/visual-renderers/MarkdownRenderer.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Renders markdown to HTML correctly
    - Output is sanitized
    - Code blocks get syntax highlighting (existing hljs setup)

- [x] 8. Integrate into ToolCard
  - **What**: In `ToolCard.vue`, import `parseVisualPayload` and the registry. Before the `<pre>` block, check if `output` is a visual payload. If so, render via `<component :is="renderer" :content="payload.content" />` instead of `<pre>`. Add an "expand" button that emits to open in content panel.
  - **Files**: `client/src/components/session/ToolCard.vue`
  - **Depends on**: Tasks 3, 4, 5, 6, 7
  - **Acceptance**:
    - Visual payloads render with appropriate component
    - Non-visual output still renders as `<pre><code>`
    - Expand button visible on visual results

- [x] 9. Wire content panel expansion
  - **What**: When "expand" is clicked in ToolCard, push the visual payload to the right panel. Modify `SessionsV2RightPanel.vue` to add a new tab "Visual" (alongside "Artifacts" and "Info") that displays when a visual payload is active. Add `visualPayload` state to `useSidebarStore()` or create a new composable `use-visual-panel.ts` to manage the active visual payload. The visual tab should render the same component with the same props (ensuring identical sanitization). Add a close/back button to clear the visual payload and return to the previous tab.
  - **Files**: 
    - `client/src/components/session/ToolCard.vue` (emit expand event)
    - `client/src/components/sessions/SessionsV2RightPanel.vue` (add visual tab)
    - `client/src/stores/sidebar.ts` (add visualPayload state) OR `client/src/composables/use-visual-panel.ts` (new composable)
  - **Depends on**: Task 8
  - **Acceptance**:
    - Clicking expand shows content in right panel "Visual" tab
    - Same renderer component used (no divergent sanitization)
    - Panel shows close/back affordance that clears the visual payload
    - Right panel auto-expands when visual payload is set

- [x] 10. End-to-end verification
  - **What**: Run typecheck, lint, and unit tests. Manually verify with mock tool output containing each `$type`.
  - **Depends on**: All previous tasks
  - **Acceptance**:
    - `bun run typecheck` passes
    - `bun run lint` passes
    - `bun run test` passes
    - Visual rendering works in `dev:mock` mode

## Verification
```bash
cd client
bun install
bun run typecheck
bun run lint
bun run test
bun run dev:mock
# Manually trigger tool results with $type payloads and verify rendering
```
All commands exit 0. Visual payloads render inline with correct components. Expand button opens content panel with same render.
