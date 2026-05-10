# Observability Enrichment

## TL;DR
> **Summary**: Enrich backend logs/spans with `SessionId` for session-level filtering, and add a fire-and-forget UI action telemetry endpoint so client interactions are visible in Aspire Dashboard.
> **Estimated Effort**: Medium

## Context
### Original Request
Add observability plumbing: (1) enrich all backend session-scoped logs/spans with a `SessionId` tag, (2) add a `POST /api/telemetry/actions` endpoint for the Vue client to report UI actions as structured logs flowing through the OTLP pipeline.

### Key Findings
- **Telemetry infra exists**: `TelemetryExtensions.cs` already configures OTLP export (traces, metrics, logs) conditionally on `OTEL_EXPORTER_OTLP_ENDPOINT`. Logging includes scopes (`IncludeScopes = true`).
- **`FleetInstrumentation`** in `WeaveFleet.Application.Diagnostics` holds the shared `ActivitySource` and `Meter`. Good home for tag-name constants.
- **`SessionOrchestrator`** has an `ILogger<SessionOrchestrator>` injected but currently has zero log calls. It has session context (session ID) in every method. Uses `partial class` — source-generated logging can be added.
- **`SessionService`** similarly operates on sessions — needs the same scope enrichment.
- **Endpoint patterns**: All endpoints live in `WeaveFleet.Api/Endpoints/`, registered via `EndpointExtensions.MapFleetEndpoints()`. Each endpoint class has a `MapXxxEndpoints` extension method on `IEndpointRouteBuilder`. Request/response records are `internal sealed record`.
- **Client**: `apiFetch` in `client/src/lib/api-client.ts` is the standard HTTP wrapper. Session actions are in `use-session-actions.ts` (V2) and `use-session-actions-v1.ts` (V1). Actions that need instrumentation: create, stop, resume, abort, delete, fork, archive, unarchive, send prompt, navigate to detail.

## Objectives
### Core Objective
Make session-level observability first-class: any log or span produced while handling a session carries `SessionId`, and key UI interactions are captured as structured log entries visible in Aspire Dashboard.

### Deliverables
- [ ] `SessionId` logger scope on all `SessionOrchestrator` and `SessionService` methods that have session context
- [ ] `SessionId` tag on `Activity` spans in `SessionOrchestrator`
- [ ] `POST /api/telemetry/actions` endpoint with structured logging
- [ ] Client-side `trackAction()` utility and instrumentation of key session actions (V1 + V2)

### Definition of Done
- [ ] `dotnet build` succeeds with no new warnings
- [ ] Aspire Dashboard shows `SessionId` on log entries when a session operation executes
- [ ] UI actions appear as structured log entries in Aspire Dashboard
- [ ] `bun run build` succeeds in `client/`

### Guardrails (Must NOT)
- Must NOT persist UI action data to the database
- Must NOT add new NuGet packages (OTLP deps already present)
- Must NOT break existing OTLP-disabled behavior (endpoint logs to console, no crash)
- Must NOT use optional parameters in C# (project convention)

## TODOs

### Phase 1 — Backend SessionId Enrichment

- [x] 1. Add SessionId tag constants to FleetInstrumentation
  **What**: Add `public const string SessionIdTag = "session.id"` to `FleetInstrumentation` for consistent tag naming across logs and spans.
  **Files**: `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs`
  **Acceptance**: Constant compiles; referenced in subsequent tasks.

- [x] 2. Add SessionId logger scopes to SessionOrchestrator
  **What**: In each public method of `SessionOrchestrator` that has a session ID (or creates one), wrap the method body with `using var scope = logger.BeginScope(new Dictionary<string, object> { [FleetInstrumentation.SessionIdTag] = sessionId });`. Also set `Activity.Current?.SetTag(FleetInstrumentation.SessionIdTag, sessionId)` so spans get the tag too. Key methods: `CreateSessionAsync`, `ResumeSessionAsync`, `PromptSessionAsync`, `AbortSessionAsync`, `ForkSessionAsync`, `GetSessionMessagesAsync`, `CommandSessionAsync`, `AddSourceToSessionAsync`, `PreviewAddSourceToSessionAsync`, `GetCommittedEventsAsync`.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Every public method that operates on a session ID enriches both logger scope and current Activity with `session.id`.

- [x] 3. Add SessionId logger scopes to SessionService
  **What**: Same pattern as task 2 for `SessionService` methods: `GetSessionAsync`, `StopSessionAsync`, `DeleteSessionAsync`, `UpdateRetentionAsync`, `UpdateSessionTitleAsync`, `MoveSessionToProjectAsync`, `ListSessionsAsync` (skip — no single session context).
  **Files**: `src/WeaveFleet.Application/Services/SessionService.cs`
  **Acceptance**: Session-scoped methods carry `session.id` in logger scope.

### Phase 2 — UI Action Telemetry Endpoint

- [x] 4. Create TelemetryEndpoints with POST /api/telemetry/actions
  **What**: New file `TelemetryEndpoints.cs` in `WeaveFleet.Api/Endpoints/`. Register via `MapTelemetryEndpoints()`. Endpoint accepts `UiActionRequest { Action: string, SessionId: string?, Metadata: JsonElement? }`. Handler uses `ILogger<TelemetryEndpoints>` to emit a structured log: `logger.LogInformation("UI action: {Action}", request.Action)` inside a `BeginScope` that includes `session.id` (when present) and flattened metadata keys. Returns `204 No Content`. Request record is `internal sealed record` with `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]`.
  **Files**: `src/WeaveFleet.Api/Endpoints/TelemetryEndpoints.cs`
  **Acceptance**: `POST /api/telemetry/actions` with `{ "action": "session.create" }` returns 204 and emits a structured log visible in console output.

- [x] 5. Register TelemetryEndpoints in EndpointExtensions
  **What**: Add `apiScope.MapTelemetryEndpoints();` call in `MapFleetEndpoints()` alongside the other endpoint registrations.
  **Files**: `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: Endpoint is reachable after app startup.

### Phase 3 — Client-Side Action Tracking

- [x] 6. Create trackAction utility
  **What**: New file `client/src/lib/track-action.ts`. Exports `trackAction(action: string, sessionId?: string, metadata?: Record<string, unknown>): void`. Implementation: fire-and-forget `apiFetch("/api/telemetry/actions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ action, sessionId, metadata }) }).catch(() => {})`. No awaiting, no error surfacing — pure telemetry. The function should be a plain export, not a composable.
  **Files**: `client/src/lib/track-action.ts`
  **Acceptance**: Function exists, calls apiFetch, swallows errors.

- [x] 7. Instrument V2 session actions (use-session-actions.ts)
  **What**: Import `trackAction` and add calls after successful API responses in: `useCreateSession` (action: `session.create`, sessionId from response), `useDeleteSession` (`session.delete`), `useResumeSession` (`session.resume`), `useAbortSession` (`session.abort`), `useArchiveSession` (`session.archive`), `useUnarchiveSession` (`session.unarchive`), `useForkSession` (`session.fork`). For `sendPrompt` — instrument in the prompt composable if separate, or here if inline.
  **Files**: `client/src/composables/use-session-actions.ts`
  **Acceptance**: Each action function calls `trackAction` after success with the correct action name and sessionId.

- [x] 8. Instrument V1 session actions (use-session-actions-v1.ts)
  **What**: Same pattern as task 7 for V1 equivalents. Import `trackAction`, add calls after successful API responses for create, stop, resume, abort, delete actions.
  **Files**: `client/src/composables/use-session-actions-v1.ts`
  **Acceptance**: V1 actions also emit telemetry.

- [x] 9. Instrument session detail navigation
  **What**: Find the component/router guard that navigates to session detail and add `trackAction("session.view", sessionId)`. This is likely in the session list item click handler or router navigation. Check `client/src/views/` or `client/src/router/` for the session detail route. Add the tracking call at the point of navigation (e.g., `router.push` call site or an `onMounted` in the detail view).
  **Files**: Investigate — likely `client/src/views/SessionDetailView.vue` or equivalent
  **Acceptance**: Navigating to a session detail page fires a `session.view` telemetry action.

- [x] 10. Instrument send-prompt action
  **What**: Find the prompt submission handler (likely in a session detail composable or component) and add `trackAction("session.prompt", sessionId)` after successful prompt send.
  **Files**: Investigate — likely in a composable or the session detail component
  **Acceptance**: Sending a prompt fires a `session.prompt` telemetry action.

### Phase 4 — Verification

- [x] 11. Build verification
  **What**: Run `dotnet build` for the backend and `bun run build` in `client/` to confirm no compilation errors or TypeScript errors.
  **Acceptance**: Both commands exit 0 with no new warnings.

- [ ] 12. Manual smoke test with Aspire Dashboard
  **What**: Start the app with `OTEL_EXPORTER_OTLP_ENDPOINT` pointing at Aspire. Create a session via the UI. Verify in Aspire: (a) backend log entries for CreateSessionAsync carry `session.id`, (b) a `UI action: session.create` log entry appears with `session.id`. Stop, resume, delete — verify each action appears.
  **Acceptance**: Session-level filtering in Aspire Dashboard shows correlated backend + UI action logs.

## Verification
- [ ] `dotnet build` passes with zero new warnings
- [ ] `bun run build` passes in `client/`
- [ ] No regressions — existing session CRUD still works
- [ ] Aspire Dashboard shows `session.id` on session-scoped log entries
- [ ] UI actions appear as structured logs in Aspire Dashboard
