# Smart Forms (Question Tool)

## TL;DR
> **Summary**: Enable the AI to ask structured questions (single-select, multi-select, custom input) via a `question` tool call that renders an interactive form in the UI, blocks the agent until the user responds or dismisses, and persists the Q&A for replay.
> **Estimated Effort**: Large

## Context
### Original Request
Implement a "Smart Forms" / "Question Tool" feature where the AI can ask structured questions via tool calls. The user sees an interactive form, submits answers (or dismisses), and the AI continues with the response as tool output.

### Key Findings
1. **Messages use polymorphic parts** — `AccumulatedPart` union (text, reasoning, tool, file) on the frontend; `MessageEventPart` discriminated union on the backend. The question tool already exists in OpenCode's tool system but is currently **denied** via `OPENCODE_CONFIG_CONTENT = {"permission":{"question":"deny"}}`.

2. **Tool call flow**: The AI emits a `tool` part with `state: pending → running → completed`. The frontend renders these via `ToolCard.vue`. Questions are a special case of tool calls — the `tool` field will be `"question"` and the `state.input` contains the question schema.

3. **OpenCode's question tool** already emits SSE events: the tool part appears with `tool: "question"` and `state: { status: "pending", input: { ... } }`. When answered, OpenCode expects a response via its API. The question blocks the agent (session stays `busy` / `waiting_input`).

4. **No new part type needed on the backend**. Questions are tool parts (`type: "tool"`, `tool: "question"`). The existing `ToolMessageEventPart` and `AccumulatedToolPart` already carry the question payload in `state.input`. The frontend just needs to detect `tool === "question"` and render a form instead of a `ToolCard`.

5. **Answering a question** requires calling the OpenCode API to provide the tool result. This needs a new Fleet API endpoint that proxies the answer to the underlying harness.

6. **Session status**: OpenCode emits `session.status: waiting_input` (or similar) when a question is pending. The existing `sessionStatus` field on `SessionListItem` already includes `"waiting_input"`.

## Objectives
### Core Objective
Allow the AI to ask structured questions that render as interactive forms, with answers flowing back as tool output to continue the conversation.

### Deliverables
- [ ] Backend: Remove question-deny config, add answer/reject API endpoint
- [ ] Backend: Forward question answer to OpenCode harness
- [ ] Frontend: `QuestionCard.vue` component for rendering question forms
- [ ] Frontend: Wire question detection into `ActivityStream.vue` and/or `Composer.vue`
- [ ] Frontend: Submit/dismiss handlers that call the answer API
- [ ] Persistence: Questions persist as tool parts and render correctly on reload

### Definition of Done
- [ ] AI can invoke the question tool, UI renders the form
- [ ] User can select option(s) and submit → AI receives the answer and continues
- [ ] User can dismiss → AI receives rejection signal
- [ ] Page reload renders answered/rejected questions in their final state
- [ ] `dotnet build` succeeds, frontend builds without errors

### Guardrails (Must NOT)
- Must NOT introduce a new `MessageEventPart` subtype — questions ARE tool parts
- Must NOT break existing tool card rendering for non-question tools
- Must NOT allow question forms to submit multiple times
- Must NOT leave sessions stuck in `waiting_input` if user navigates away (dismiss on unmount or warn)

## TODOs

- [x] 1. Enable the Question Tool in OpenCode Config
  **What**: Remove (or make configurable) the `{"permission":{"question":"deny"}}` config in `OpenCodeProcessManager.cs`. Change it to `"allow"` so the question tool can be invoked by the agent.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeProcessManager.cs`
  **Acceptance**: OpenCode starts with question tool enabled; agent can invoke it without denial.

- [x] 2. Add Question Answer/Reject API Endpoint
  **What**: Add `POST /api/sessions/{id}/question-answer` endpoint that accepts `{ toolCallId: string, answer: string[][] }` for answering, and `POST /api/sessions/{id}/question-reject` for rejecting. These proxy to the OpenCode harness. The harness session interface (`IHarnessSession`) needs a new method like `AnswerQuestionAsync(toolCallId, answer)` and `RejectQuestionAsync(toolCallId)`.
  **Files**:
    - `src/WeaveFleet.Domain/Harnesses/IHarnessSession.cs` — add `AnswerQuestionAsync` and `RejectQuestionAsync` methods
    - `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` — add the two REST endpoints
    - `src/WeaveFleet.Api/JsonContext.cs` — register new request DTOs
    - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` — add orchestration methods
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs` — implement against OpenCode API
    - `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs` — add HTTP call to OpenCode's question answer endpoint
    - `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarnessSession.cs` — stub with `NotSupportedException`
    - `src/WeaveFleet.TestHarness/TestHarnessSession.cs` — stub implementation
  **Acceptance**: `POST /api/sessions/{id}/question-answer` with valid payload returns 200; OpenCode receives the answer and continues the conversation.

- [x] 3. Add Frontend Question Types
  **What**: Define TypeScript types for question tool input/output. Add a helper to detect whether an `AccumulatedToolPart` is a question. Define the answer request type.
  **Files**:
    - `client/src/lib/question-types.ts` — new file with `QuestionToolInput`, `QuestionOption`, `QuestionAnswerRequest`, `isQuestionPart()` helper
    - `client/src/lib/api-types.ts` — no changes needed (questions use existing `AccumulatedToolPart`)
  **Acceptance**: `isQuestionPart(part)` correctly identifies question tool parts by checking `part.tool === "question"`.

- [x] 4. Build `QuestionCard.vue` Component
  **What**: Create the interactive question form component. Renders based on the question schema from `state.input`:
    - **Single-select (≤4 options, no descriptions)**: Button row (pill buttons)
    - **Single-select (>4 options or has descriptions)**: Radio group with labels+descriptions
    - **Multi-select**: Checkbox group with labels+descriptions
    - **Custom input**: Optional text input when `custom: true` (default)
    - **States**: `pending` (interactive form), `answered` (collapsed showing selection), `rejected` (collapsed showing "Dismissed")
    
    Use Reka UI primitives (`RadioGroup`, `Checkbox`) for accessible form controls. Style consistently with `ToolCard.vue`.
  **Files**:
    - `client/src/components/session/QuestionCard.vue` — new component (~200-300 lines)
  **Acceptance**: Component renders all form variants; emits `submit` with `string[][]` and `dismiss` events.

- [x] 5. Wire QuestionCard into ActivityStream
  **What**: In `ActivityStream.vue`, when building the `tools` array for a `MessageBubble`, detect question tool parts and render `QuestionCard` instead of `ToolCard`. The question card needs the session ID and instance ID to call the answer API.
    
    Approach: In the template section of `MessageBubble.vue`, add a conditional branch: if the tool item is a question, render `<QuestionCard>` instead of `<ToolCard>`. Pass a `sessionId` prop down from `ActivityStream`.
  **Files**:
    - `client/src/components/session/ActivityStream.vue` — pass question metadata through to bubble
    - `client/src/components/session/MessageBubble.vue` — add conditional rendering for question vs regular tool
  **Acceptance**: When the AI invokes the question tool, a form appears inline in the message stream instead of a generic tool card.

- [x] 6. Implement Answer Submission Flow
  **What**: Create a composable `useQuestionAnswer(sessionId)` that:
    - Calls `POST /api/sessions/{id}/question-answer` with the selected options
    - Calls `POST /api/sessions/{id}/question-reject` on dismiss
    - Tracks loading/error state
    - Prevents double-submission
    
    Wire this into `QuestionCard.vue`'s submit/dismiss handlers.
  **Files**:
    - `client/src/composables/use-question-answer.ts` — new composable
    - `client/src/components/session/QuestionCard.vue` — integrate composable
  **Acceptance**: Clicking submit sends the answer; AI receives it and continues. Clicking dismiss sends rejection. Both are idempotent.

- [x] 7. Handle Question Lifecycle in Event State
  **What**: The question tool's state transitions (`pending` → `running` → `completed`/`error`) already flow through `applyPartUpdate` in `event-state.ts` as normal tool parts. No changes needed for basic state tracking.
    
    However, add a reactive store or composable to track "active questions" (tool parts where `tool === "question"` and state is `pending` or `running`) so the Composer can show a visual indicator or the UI can pin the active question.
  **Files**:
    - `client/src/composables/use-active-questions.ts` — new composable that derives active questions from messages
  **Acceptance**: Active questions are reactively tracked; when the tool state transitions to `completed`, the question is no longer active.

- [x] 8. Add `waiting_input` Session Status Indicator
  **What**: When a question is pending, the session status transitions to `waiting_input`. Update the Composer and session list UI to reflect this state — e.g., show "Waiting for your input" instead of "Thinking..." in the status indicator, and show a distinct badge in the session list.
  **Files**:
    - `client/src/components/session/Composer.vue` — handle `waiting_input` in status indicator logic
    - `client/src/components/sidebar/` — update session list item to show waiting indicator
  **Acceptance**: When a question is pending, the Composer shows "Waiting for your input" and the session list shows appropriate status.

- [x] 9. Add `isRelevantToSession` Filter for Question Events
  **What**: Ensure any question-specific SSE events (if OpenCode emits `question.asked`, `question.replied`, `question.rejected` alongside standard tool events) are included in the relevance filter in `event-state.ts`. Since questions flow as standard `message.part.updated` events with tool type, this may require no changes — but verify.
  **Files**:
    - `client/src/lib/event-state.ts` — add `question.*` to relevance filter if needed
  **Acceptance**: Question events are not dropped by the session filter.

- [x] 10. Persistence & Reload Verification
  **What**: Verify that questions persist correctly through the existing message persistence pipeline. On page reload, the `GET /api/sessions/{id}/messages` endpoint returns messages with tool parts. The frontend's `convertFleetMessageToAccumulated` already maps tool parts. Ensure the question's `state.input` (containing the question schema) and `state.output` (containing the answer) are preserved and the `QuestionCard` renders in the correct final state (answered/rejected).
  **Acceptance**: Refresh the page after answering a question — the question card renders in its answered state showing the selected option(s).

## Verification
- [ ] `dotnet build` succeeds with no errors
- [ ] `cd client && npm run build` succeeds with no errors
- [ ] `cd client && npm run typecheck` passes
- [ ] Manual test: AI invokes question tool → form renders → submit answer → AI continues
- [ ] Manual test: AI invokes question tool → dismiss → AI receives rejection
- [ ] Manual test: Reload page → answered question renders in collapsed/answered state
- [ ] Manual test: Multi-select question works correctly
- [ ] Manual test: Custom text input works when `custom: true`
- [ ] No regressions in existing tool card rendering
