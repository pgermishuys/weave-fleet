# Inline Artifact Annotations — Phase 1

## TL;DR
Add the ability to select text or hover elements in the rendered markdown artifact viewer, open a floating annotation popover, type a comment/question, and send it as a formatted prompt to the active session's agent.

## Context
- Markdown rendering: `client/src/components/visual-renderers/MarkdownRenderer.vue` renders via `v-html` from markdown-it
- Artifact viewer state: `client/src/composables/use-artifact-viewer.ts` tracks `activeFilePath` and content
- Send prompt: `client/src/composables/use-send-prompt.ts` — `useSendPrompt(sessionId)` exposes `sendPrompt()` which reads from draft state
- The prompt API accepts arbitrary `text` in the POST body — no backend changes needed
- No existing annotation infrastructure exists

## Scope
- In scope:
  - Hover highlights on rendered markdown elements
  - Text selection capture (arbitrary ranges)
  - Floating popover with textarea + send button
  - Formatting annotation as structured prompt text
  - Sending annotation via existing prompt API
- Out of scope:
  - Persisting annotations / comment threads
  - Badge indicators on previously-commented elements
  - Queue / batch mode for annotations
  - Backend API changes
- Constraints / assumptions:
  - Phase 1 only — immediate send, no persistence
  - Max 240 chars of anchor text in the prompt
  - Must work within the existing `MarkdownRenderer.vue` `v-html` approach (post-render DOM manipulation)

## Objectives
- Users can annotate any visible element or text selection in the artifact viewer
- Annotations are sent as contextual prompts to the active session agent
- UX mirrors the prototype: hover highlight → click/select → popover → type → send

## Dependencies and Order
1. Annotation anchor types must be defined first (Task 1) as all other tasks depend on the data model.
2. The composable (Task 2) manages state that the popover component (Task 3) consumes.
3. The markdown renderer enhancement (Task 4) wires DOM events to the composable.
4. Prompt formatting (Task 5) is independent but must exist before integration.
5. Final wiring (Task 6) connects everything in the right panel.

## Tasks

- [x] 1. Define annotation anchor types
  - **What**: Create a TypeScript module with types for annotation anchors: `ElementAnchor` (CSS selector path + element text) and `TextRangeAnchor` (selected text + start/end offsets). Export a helper `extractAnchorText(anchor): string` that truncates to 240 chars.
  - **Files**: `client/src/lib/annotation-types.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Types are exported and importable
    - `extractAnchorText` truncates with ellipsis at 240 chars
    - Handles both anchor types

- [x] 2. Create `useAnnotation` composable
  - **What**: Composable managing annotation lifecycle state: `activeAnchor` (ref to current anchor or null), `isPopoverOpen`, `popoverPosition` (x/y), `openAnnotation(anchor, position)`, `closeAnnotation()`, `submitAnnotation(text)`. The `submitAnnotation` function calls a provided callback (not directly coupled to send-prompt yet).
  - **Files**: `client/src/composables/use-annotation.ts`
  - **Depends on**: Task 1
  - **Acceptance**:
    - `openAnnotation` sets anchor + position + opens popover
    - `closeAnnotation` resets state
    - `submitAnnotation` calls callback with formatted text then closes
    - Reactive state is testable in isolation

- [x] 3. Build `AnnotationPopover.vue` component
  - **What**: Floating card positioned absolute at `popoverPosition`. Contains: quoted anchor text preview (truncated), textarea for user input, Cancel + Send buttons. Emits `send(text)` and `cancel`. Styled with Tailwind matching existing panel aesthetics (check `ArtifactViewerToolbar.vue` for patterns). Closes on Escape key.
  - **Files**: `client/src/components/annotations/AnnotationPopover.vue`
  - **Depends on**: Task 1 (for anchor text display)
  - **Acceptance**:
    - Renders at provided x/y coordinates
    - Shows truncated anchor text in a blockquote-style preview
    - Textarea is auto-focused on mount
    - Send button disabled when textarea empty
    - Emits correctly on send/cancel/Escape
    - Doesn't overflow viewport (clamp position to bounds)

- [x] 4. Enhance `MarkdownRenderer.vue` with annotation event handling
  - **What**: Add a `annotatable` prop (default false). When true, attach a delegated `mouseenter`/`mouseleave` on the rendered container to highlight hovered block elements (add/remove a CSS class). On `mouseup`, detect if there's a text selection within the container — if so, build a `TextRangeAnchor`; otherwise on click of a highlighted element, build an `ElementAnchor`. Emit `annotate(anchor, position)` event.
  - **Files**: `client/src/components/visual-renderers/MarkdownRenderer.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Hover highlights block elements (headings, paragraphs, list items, code blocks) with a subtle outline/background
    - Text selection within the renderer captures selected text as a `TextRangeAnchor`
    - Click on highlighted element (without selection) emits `ElementAnchor`
    - Emits `annotate` event with anchor + mouse position
    - When `annotatable` is false, no event listeners or highlights are added

- [x] 5. Create annotation prompt formatter
  - **What**: Pure function `formatAnnotationPrompt(filePath: string, anchorText: string, userComment: string): string` that produces the structured prompt format: `[Annotation on {filePath}]\n> {anchorText}\n\n{userComment}`
  - **Files**: `client/src/lib/format-annotation-prompt.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Output matches the specified format exactly
    - Anchor text is already truncated (caller responsibility) but function handles missing filePath gracefully
    - Unit-testable pure function

- [x] 6. Wire annotations into the right panel
  - **What**: In `SessionsV2RightPanel.vue` (or the component that hosts `MarkdownRenderer` for artifacts), pass `annotatable` prop, handle the `annotate` event by calling `useAnnotation().openAnnotation(...)`, render `AnnotationPopover` when open, and on send: format with `formatAnnotationPrompt`, then call `useSendPrompt(sessionId).sendPrompt()` by setting draft text programmatically (or directly call `postPrompt` — check feasibility). The key integration: popover send → format prompt → dispatch to session.
  - **Files**: `client/src/components/sessions/SessionsV2RightPanel.vue`
  - **Depends on**: Tasks 2, 3, 4, 5
  - **Acceptance**:
    - End-to-end flow works: hover → click/select → popover → type → send → prompt appears in session
    - Popover closes after send
    - File path from `useArtifactViewer().activeFilePath` is included in the prompt
    - Works in rendered view mode only (source mode: `annotatable` is false)

## Verification
1. `cd client && bunx vue-tsc --noEmit` — no type errors
2. Manual test: open a markdown artifact, hover elements (see highlight), click one, type a comment, send — verify the prompt arrives in the conversation with correct format
3. Text selection test: select arbitrary text in the rendered artifact, verify popover opens with selected text as anchor, send works
