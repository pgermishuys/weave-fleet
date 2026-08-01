# Vue Flow Renderer for `visual/flow`

## TL;DR
Port the `VueFlowRenderer` from Foundry to render interactive node-edge flow diagrams inline in tool results, using `@vue-flow/core` and `@dagrejs/dagre` for auto-layout.

## Context
The `visualize` tool already emits `visual/flow` payloads but `parseVisualPayload` doesn't recognize the type, so it falls through to raw JSON `<pre>` display. Foundry's `VueFlowRenderer.vue` uses `@vue-flow/core` + `@dagrejs/dagre` + `html-to-image` for rendering and PNG export.

Key files to modify:
- `client/src/lib/visual-payload.ts` — add `visual/flow` to valid types
- `client/src/lib/visual-renderer-registry.ts` — register new component
- New: `client/src/components/visual-renderers/VueFlowRenderer.vue`

Source reference: `C:\source\Foundry\src\Foundry.Web\src\components\VueFlowRenderer.vue`

## Tasks

- [x] 1. Install `@vue-flow/core`, `@dagrejs/dagre`, `html-to-image`
  - **What**: Add `@vue-flow/core`, `@dagrejs/dagre`, and `html-to-image` to client dependencies. Add `@types/dagre` to devDependencies (if it exists, otherwise skip).
  - **Files**: `client/package.json`
  - **Depends on**: None
  - **Acceptance**:
    - `bun install` succeeds
    - Packages resolve in imports

- [x] 2. Add `visual/flow` to `parseVisualPayload`
  - **What**: Update `VisualPayload` interface to include `'visual/flow'` as a valid `$type`. Update `VALID_TYPES` set. For `visual/flow`, `content` can be a string OR an object (the tool pre-parses it), so add a `FlowVisualPayload` variant or make content `string | object` for that type. Simplest approach: add a separate `FlowVisualPayload` interface and make the return type a union, OR keep content as `unknown` for flow and let the renderer handle parsing.
  - **Files**: `client/src/lib/visual-payload.ts`, `client/src/lib/__tests__/visual-payload.test.ts`
  - **Depends on**: None
  - **Interface change**:
    ```typescript
    export interface VisualPayload {
      $type: 'visual/sequence' | 'visual/flow' | 'html' | 'markdown'
      content: string | Record<string, unknown>
      title?: string
    }
    ```
    For `visual/flow`, content may be an object (pre-parsed by the tool) or a string. Accept both.
  - **Acceptance**:
    - `parseVisualPayload` recognizes `visual/flow` with string content
    - `parseVisualPayload` recognizes `visual/flow` with object content
    - Existing tests still pass
    - New tests added for flow type

- [x] 3. Create `VueFlowRenderer.vue`
  - **What**: Port `VueFlowRenderer.vue` from Foundry. Component accepts `content: string | Record<string, unknown>` and optional `title` prop. Features: dagre auto-layout, source toggle, PNG export via `html-to-image`. Adapt CSS variables to match weave-fleet's design tokens (`var(--border)`, `var(--panel-bg)`, `var(--muted)`, `var(--text)`, `var(--font-mono-stack)`).
  - **Files**: `client/src/components/visual-renderers/VueFlowRenderer.vue`
  - **Depends on**: Task 1
  - **Source reference**: `C:\source\Foundry\src\Foundry.Web\src\components\VueFlowRenderer.vue` (port directly, adapt styling)
  - **Acceptance**:
    - Renders nodes and edges from flow content
    - Auto-layouts using dagre
    - Source toggle shows raw JSON
    - PNG export button works
    - CSS uses weave-fleet design tokens

- [x] 4. Register in renderer registry
  - **What**: Import `VueFlowRenderer` and add `'visual/flow': VueFlowRenderer` to the registry.
  - **Files**: `client/src/lib/visual-renderer-registry.ts`
  - **Depends on**: Tasks 2, 3
  - **Acceptance**:
    - `getVisualRenderer('visual/flow')` returns the component
    - Existing renderers still resolve

- [x] 5. Verification
  - **What**: Run typecheck, lint, and tests.
  - **Depends on**: All previous
  - **Acceptance**:
    - `bun run typecheck` passes (no new errors)
    - `bun run lint` passes
    - `bun run test` passes (no new failures)
