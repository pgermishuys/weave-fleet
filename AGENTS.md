# Weave Fleet — Agent Instructions

## Toolchain

- **Package manager / script runner:** Always use `bun`. Never use `npm` or `npx` directly.
  - Run scripts: `bun run <script>`
  - Execute binaries: `bunx <package>` (e.g., `bunx vue-tsc --noEmit`)
  - Install packages: `bun install`
  - If you truly need npm CLI behaviour, use `bunx npm <args>`

## Layout Vocabulary

The application shell is a composable layout built from named panels:

```
[rail][context]
```

- **Rail** — Always-visible icon navigation fixed to the left edge. Controls which view fills the context area.
- **Context** — Everything to the right of the rail. Its composition changes per route.

### Route Compositions

| Route        | Context layout                              |
|--------------|---------------------------------------------|
| Sessions     | `[session-list][conversation][content]`     |
| Settings     | `[settings-menu][settings-detail]`          |
| Automations  | `[automations-list]`                        |

### Panel Definitions

| Panel             | Description                                                                 |
|-------------------|-----------------------------------------------------------------------------|
| `rail`            | Icon-only vertical nav bar (left edge, always visible)                      |
| `session-list`    | List of sessions for the current workspace (collapsible)                    |
| `conversation`    | The message/activity stream for the active session                          |
| `content`         | Right-side artifact viewer (rendered markdown, HTML preview, raw source)     |
| `settings-menu`   | Left nav for settings categories                                            |
| `settings-detail` | Detail pane for the selected settings category                              |
| `automations-list`| List/card view of configured automations                                    |

### Resize Gutters

Resize gutters exist only between specific panel pairs:

- `[conversation]` ↔ `[content]`

No gutter between rail and context, or between session-list and conversation.

## Diagnostics and Testing

This section describes how to quickly diagnose and verify issues at each layer without guessing.

### Principle: Interrogate, Don't Guess

Never speculate about what's happening on the wire or in state. Build a test that proves the actual behaviour, then fix from evidence. The codebase has layered test infrastructure for exactly this purpose.

### Layer 1: API (Server-side)

**SignalR hub contract tests** (`tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`):
- Boots a real Kestrel server with SignalR hub (no browser, no frontend build)
- Connects a .NET `HubConnection`, subscribes to a session, broadcasts events via `IEventBroadcaster`
- Asserts the exact JSON shape received by the client
- Run: `dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests"`
- Use when: events aren't arriving, wrong shape, serialization issues, hub pump problems

**Diagnostic pattern for unknown issues:**
1. Add a `_hub.On<string, long, JsonElement>("Event", ...)` handler and log `data.GetRawText()`
2. Add a `_hub.Closed += ex => ...` handler to catch connection kills (e.g., "Error reading JSON")
3. Check `InMemoryEventBroadcaster.SubscriberCount` (internal property) to verify the pump is subscribed
4. Broadcast with `userId: "local-user"` in test mode (matches `LocalUserContext`)

**Other API tests:**
- Unit tests: `tests/WeaveFleet.Api.Tests/` (hub logic, snapshot merge)
- Application tests: `tests/WeaveFleet.Application.Tests/` (orchestrator, streaming state)
- Infrastructure tests: `tests/WeaveFleet.Infrastructure.Tests/` (event bus, persistence)

### Layer 2: Client (Frontend)

**Unit tests** (`client/src/composables/__tests__/`):
- Test composables in isolation with mocked socket/API
- Run: `bun run test` (from `client/`)
- Use when: state management, event handling logic, message accumulation bugs

**Key composables:**
- `use-signalr-socket.ts` — SignalR connection lifecycle, topic dispatch
- `use-session-events.ts` — Event-to-state reducer, message accumulation, idle fallback
- `use-weave-socket.ts` — Re-exports from signalr-socket (the active transport)

**Diagnostic pattern for client issues:**
1. Check browser console for SignalR errors ("Error reading JSON", connection closed)
2. Use `window.__WEAVE_SOCKET_TEST_API` in devtools:
   - `.hasOpenSocket()` — is SignalR connected?
   - `.hasV2Subscriptions()` — are topics registered?
   - `.hasV2Snapshot(topic)` — did the snapshot arrive?
   - `.v2SnapshotHasText(topic, text)` — does snapshot contain expected text?
3. Add `diagLog()` calls (from `@/lib/message-diagnostics`) to trace event flow

### Layer 3: End-to-End (Full Stack)

**Playwright E2E tests** (`tests/WeaveFleet.E2E/`):
- Full browser against real Kestrel server with TestHarness
- Run: `dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"`
- SignalR-specific: `dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~SignalRTransportTests"`
- Use when: verifying the user-visible behaviour end-to-end

**E2E requires frontend build:**
```
cd client && bun install && bun run build
```

Skip rebuild on iteration: `dotnet test tests/WeaveFleet.E2E -p:SkipFrontendBuild=true`

### Decision Tree: Where to Start

| Symptom | Start here |
|---------|-----------|
| Events not arriving at client | Layer 1: SignalR contract tests |
| Events arrive but wrong shape | Layer 1: assert `data.GetRawText()` |
| Events arrive but UI doesn't update | Layer 2: unit test `handleEvent` with real payload |
| Connection drops/reconnect issues | Layer 2: `use-signalr-socket` tests + devtools API |
| Everything works in tests but not in browser | Layer 3: E2E with headed mode (`$env:HEADED=1`) |
| Flaky behaviour | Add `diagLog()` + check idle fallback timer (2500ms) |
