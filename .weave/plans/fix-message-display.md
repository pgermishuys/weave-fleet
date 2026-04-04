# Fix Message Display: Fleet-Native Message Contract

## TL;DR
> **Summary**: Rewrite the message display pipeline so the API returns Fleet domain types (`HarnessMessage`/`MessagePart` with polymorphic serialization), the frontend parses Fleet's shape (flat `id`/`role`/`parts`, not nested `info`), and WebSocket event payloads are translated from OpenCode IDs to Fleet IDs at the harness boundary.
> **Estimated Effort**: Medium

## Context
### Original Request
Messages (user prompts + assistant responses) don't display in Fleet's UI. Two root causes: (1) WebSocket events carry OpenCode session IDs while the frontend compares against Fleet session IDs, and (2) the REST `/messages` endpoint returns `MessagePage` with abstract `MessagePart` records that lack `[JsonPolymorphic]`, so derived fields are lost.

### Key Findings

**Architectural Principle**: Fleet defines its own message contract. Harnesses adapt to it. The frontend only knows Fleet types.

**Current REST API flow** (`GET /api/sessions/{id}/messages`):
- `SessionEndpoints` → `SessionOrchestrator.GetSessionMessagesAsync` → `IHarnessInstance.GetMessagesAsync` → `OpenCodeHarnessInstance` which calls `OpenCodeMapper.ToHarnessMessages()` → returns `MessagePage(IReadOnlyList<HarnessMessage>, bool HasMore)`.
- `Results.Ok(page)` serializes `MessagePage` directly. ASP.NET Core minimal APIs use `System.Text.Json` with default camelCase policy (no custom options configured in `Program.cs`).
- **Problem**: `MessagePart` is `abstract record MessagePart(MessagePartKind Kind)` with NO `[JsonPolymorphic]` attribute → only `Kind` (an int) serializes; `TextPart.Text`, `ToolUsePart.ToolName` etc. are lost.
- **Problem**: Frontend expects `{ messages: [{ info: { id, sessionID, ... }, parts: [{ id, type, text, ... }] }], pagination: {...} }` (the `SDKMessage` shape), but gets `{ messages: [{ id, role, parts: [{ kind: 0 }], ... }], hasMore: false }`.

**Current WebSocket flow**:
- `HarnessEventRelay.PumpAsync()` resolves `fleetSessionId` and routes to topic `session:{fleetSessionId}`.
- But `evt.Payload` is the raw OpenCode `JsonElement` (from `OpenCodeMapper.ToHarnessEvent` line 98: `Payload = evt.Properties`), containing OpenCode's `sessionId`/`sessionID`, not Fleet's ID.
- Frontend's `message.part.updated` handler checks `part.sessionID !== sessionId` → OpenCode ID ≠ Fleet ID → drops all parts.
- Frontend's `message.part.delta` handler checks `sessionID !== sessionId` → same mismatch → drops all deltas.

**JSON casing details** (critical for this plan):
- ASP.NET Core default `JsonNamingPolicy.CamelCase` converts PascalCase property names:
  - `Id` → `id`, `Role` → `role`, `Parts` → `parts`, `HasMore` → `hasMore`, `Timestamp` → `timestamp`
  - `TextContent` → `textContent`, `ToolCallId` → `toolCallId`, `ToolName` → `toolName`
  - `MessagePartKind` enum → serializes as integer (0, 1, 2) by default
- With `[JsonPolymorphic]` on `MessagePart`, the discriminator `"type"` property will be added alongside the record's properties.
- For positional record parameters like `TextPart(string Text)`, the property name is `Text` → serializes as `text` in camelCase.

**So `MessagePage` will serialize as:**
```json
{
  "messages": [{
    "id": "msg-1",
    "role": "assistant",
    "parts": [
      { "type": "text", "kind": 0, "text": "Hello world" },
      { "type": "tool", "kind": 1, "toolCallId": "call-1", "toolName": "bash", "arguments": {...}, "state": 2 }
    ],
    "timestamp": "2025-01-01T00:00:00Z",
    "textContent": "Hello world"
  }],
  "hasMore": false
}
```

This is Fleet's contract. The frontend must adapt to this shape.

## Objectives
### Core Objective
Make messages display in the Fleet UI by: (1) fixing backend polymorphic serialization, (2) adapting the frontend to Fleet's message shape, and (3) translating WebSocket event payloads to Fleet IDs at the harness boundary.

### Deliverables
- [ ] REST: `MessagePart` serializes polymorphically with all derived fields
- [ ] REST: Frontend parses Fleet's `MessagePage` shape (flat `HarnessMessage`, not nested `SDKMessage.info`)
- [ ] REST: Pagination metadata added to API response
- [ ] WebSocket: Event payloads contain Fleet session IDs, not OpenCode IDs
- [ ] WebSocket: Frontend session ID checks work correctly (Fleet ID vs Fleet ID)
- [ ] Tests cover polymorphic serialization, API response shape, event translation, and frontend parsing

### Definition of Done
- [ ] `cd client && npx vitest run` — all frontend tests pass
- [ ] `dotnet test` — all backend tests pass
- [ ] Manual: navigate to a session with messages → user prompts and assistant responses display
- [ ] Manual: send a new prompt → streaming text appears in real-time

### Guardrails (Must NOT)
- Must NOT create `ApiMessageResponse` / `SDKMessage`-shaped backend DTOs — frontend adapts to Fleet types
- Must NOT modify `OpenCodeModels.cs` — those are for deserialization of external OpenCode data
- Must NOT change WebSocket topic routing (it's correct)
- Must NOT change `HarnessEventRelay.PumpAsync()`'s session resolution logic (it correctly finds Fleet session ID)

## TODOs

### Phase 1: Backend — Fix Polymorphic Serialization

- [x] 1. **Add `[JsonPolymorphic]` to `MessagePart`**
  **What**: The `MessagePart` abstract record lacks polymorphic serialization. Add `[JsonPolymorphic]` and `[JsonDerivedType]` attributes so `TextPart.Text`, `ToolUsePart.ToolName`, etc. serialize properly.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
  **Change**:
  ```csharp
  using System.Text.Json.Serialization;

  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  [JsonDerivedType(typeof(TextPart), "text")]
  [JsonDerivedType(typeof(ToolUsePart), "tool")]
  [JsonDerivedType(typeof(ToolResultPart), "tool-result")]
  public abstract record MessagePart(MessagePartKind Kind);
  ```
  **Note**: The `Kind` property still serializes as an integer (`kind: 0`). The `type` discriminator is what the frontend will use. Both coexist harmlessly.
  **Acceptance**: `JsonSerializer.Serialize<MessagePart>(new TextPart("hi"))` outputs `{"type":"text","kind":0,"text":"hi"}`.

- [x] 2. **Add pagination wrapper to `GET /{id}/messages` response**
  **What**: The frontend's `use-message-pagination.ts` expects `{ messages: [...], pagination: { hasMore, oldestMessageId, totalCount } }`. Currently the endpoint returns `MessagePage` directly, which serializes as `{ messages: [...], hasMore: boolean }` — no pagination wrapper. Add the wrapper at the API level.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Change**: Update the `GetSessionMessages` handler (lines 113-122):
  ```csharp
  group.MapGet("/{id}/messages", async (string id, int? limit, string? before, SessionOrchestrator orchestrator) =>
  {
      var query = (limit is not null || before is not null)
          ? new MessageQuery(limit, before)
          : null;
      var result = await orchestrator.GetSessionMessagesAsync(id, query);
      return result.Match(
          page =>
          {
              var oldest = page.Messages.Count > 0 ? page.Messages[0].Id : null;
              return Results.Ok(new
              {
                  messages = page.Messages,
                  pagination = new
                  {
                      hasMore = page.HasMore,
                      oldestMessageId = oldest,
                      totalCount = page.Messages.Count // best we can do — harness doesn't expose total
                  }
              });
          },
          err => err.ToSessionApiResult());
  })
  ```
  **Note**: `page.Messages` is `IReadOnlyList<HarnessMessage>` — the polymorphic attributes from TODO 1 ensure parts serialize correctly. The anonymous type wrapping ensures camelCase properties: `messages`, `pagination.hasMore`, `pagination.oldestMessageId`, `pagination.totalCount`.
  **Acceptance**: `GET /api/sessions/{id}/messages?limit=50` returns `{ messages: [{ id, role, parts: [{ type: "text", text: "..." }], timestamp, textContent }], pagination: { hasMore, oldestMessageId, totalCount } }`.

- [x] 3. **Add backend serialization test for `MessagePart` polymorphism**
  **What**: Verify `TextPart` and `ToolUsePart` serialize with derived fields and type discriminator.
  **Files**: Create `tests/WeaveFleet.Domain.Tests/Harnesses/HarnessTypesSerializationTests.cs` (or add to an existing test file if there's already a Domain test project)
  **Change**:
  ```csharp
  [Fact]
  public void TextPart_Serializes_With_Type_Discriminator_And_Text()
  {
      MessagePart part = new TextPart("Hello world");
      var json = JsonSerializer.Serialize(part);
      using var doc = JsonDocument.Parse(json);
      Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
      Assert.Equal("Hello world", doc.RootElement.GetProperty("text").GetString());
  }

  [Fact]
  public void ToolUsePart_Serializes_With_All_Fields()
  {
      MessagePart part = new ToolUsePart("call-1", "bash", JsonSerializer.SerializeToElement(new { cmd = "ls" }), ToolUseState.Running);
      var json = JsonSerializer.Serialize(part);
      using var doc = JsonDocument.Parse(json);
      Assert.Equal("tool", doc.RootElement.GetProperty("type").GetString());
      Assert.Equal("call-1", doc.RootElement.GetProperty("toolCallId").GetString());
      Assert.Equal("bash", doc.RootElement.GetProperty("toolName").GetString());
  }

  [Fact]
  public void MessagePage_Serializes_With_Parts_Fully_Populated()
  {
      var page = new MessagePage(
          new[] {
              new HarnessMessage {
                  Id = "msg-1",
                  Role = "assistant",
                  Parts = new MessagePart[] { new TextPart("Hi") },
                  Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
              }
          },
          false);
      var json = JsonSerializer.Serialize(page);
      using var doc = JsonDocument.Parse(json);
      var firstPart = doc.RootElement.GetProperty("messages")[0].GetProperty("parts")[0];
      Assert.Equal("text", firstPart.GetProperty("type").GetString());
      Assert.Equal("Hi", firstPart.GetProperty("text").GetString());
  }
  ```
  **Acceptance**: All 3 tests pass, proving polymorphic serialization works end-to-end.

### Phase 2: Backend — Translate WebSocket Event Payloads

- [x] 4. **Translate event payloads to Fleet IDs in `HarnessEventRelay.PumpAsync()`**
  **What**: Currently `PumpAsync()` passes `evt.Payload` (raw OpenCode `JsonElement`) to `BroadcastAsync`. The payload contains OpenCode session IDs (e.g. `{ "info": { "sessionId": "opencode-abc" }, "part": { "sessionId": "opencode-abc" } }`). The frontend expects Fleet session IDs. We need to rewrite session IDs in the payload before broadcasting.
  **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  **Change**: Add a helper method that rewrites `sessionId`/`sessionID` fields in the payload, and call it in the pump loop:
  ```csharp
  await foreach (var evt in instance.SubscribeAsync(ct).ConfigureAwait(false))
  {
      object payload = evt.Payload.HasValue
          ? RewriteSessionIds(evt.Payload.Value, fleetSessionId)
          : JsonSerializer.SerializeToElement(new { });
      await _broadcaster.BroadcastAsync(topic, evt.Type, payload, ct).ConfigureAwait(false);
  }
  ```
  The `RewriteSessionIds` method traverses the `JsonElement` and replaces any `sessionId` or `sessionID` string values with `fleetSessionId`. This handles all event types generically without needing per-event-type mapping.
  
  **Implementation approach for `RewriteSessionIds`**:
  ```csharp
  /// <summary>
  /// Rewrites sessionId/sessionID string fields in a JSON payload to use the Fleet session ID.
  /// Works recursively on nested objects. Returns a new JsonElement.
  /// </summary>
  private static JsonElement RewriteSessionIds(JsonElement source, string fleetSessionId)
  {
      if (source.ValueKind != JsonValueKind.Object)
          return source;

      using var stream = new MemoryStream();
      using (var writer = new Utf8JsonWriter(stream))
      {
          WriteRewritten(writer, source, fleetSessionId);
      }
      return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
  }

  private static void WriteRewritten(Utf8JsonWriter writer, JsonElement element, string fleetSessionId)
  {
      switch (element.ValueKind)
      {
          case JsonValueKind.Object:
              writer.WriteStartObject();
              foreach (var prop in element.EnumerateObject())
              {
                  writer.WritePropertyName(prop.Name);
                  if ((prop.Name == "sessionId" || prop.Name == "sessionID")
                      && prop.Value.ValueKind == JsonValueKind.String)
                  {
                      writer.WriteStringValue(fleetSessionId);
                  }
                  else
                  {
                      WriteRewritten(writer, prop.Value, fleetSessionId);
                  }
              }
              writer.WriteEndObject();
              break;
          case JsonValueKind.Array:
              writer.WriteStartArray();
              foreach (var item in element.EnumerateArray())
                  WriteRewritten(writer, item, fleetSessionId);
              writer.WriteEndArray();
              break;
          default:
              element.WriteTo(writer);
              break;
      }
  }
  ```
  
  **Why generic rewriting instead of per-event-type mapping**: OpenCode emits many event types (`message.updated`, `message.part.updated`, `message.part.delta`, `session.status`, etc.) with `sessionId`/`sessionID` at varying nesting depths. A generic recursive rewrite handles all current and future events without needing to maintain a mapping for each type.
  
  **Acceptance**: When a `message.part.updated` event with `{ "part": { "sessionID": "opencode-abc", ... } }` is broadcast, the frontend receives `{ "part": { "sessionID": "fleet-123", ... } }`.

- [x] 5. **Add test for event payload session ID rewriting**
  **What**: Test that `RewriteSessionIds` correctly replaces session IDs at multiple nesting levels.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs` (or a new test file for the static helper)
  **Change**: Since `RewriteSessionIds` is private, test it indirectly through `PumpAsync` behavior, OR make it `internal` with `[InternalsVisibleTo]`. Alternatively, add a test that creates a mock `IHarnessInstance` that yields events with OpenCode session IDs and verify the `IEventBroadcaster` receives Fleet session IDs in the payload.
  **Acceptance**: Test proves that `sessionId`/`sessionID` values in event payloads are rewritten to Fleet session IDs.

### Phase 3: Frontend — Adapt to Fleet Message Shape

- [x] 6. **Replace `SDKMessage` types with Fleet-aligned types in `pagination-utils.ts`**
  **What**: Replace `SDKMessage`, `SDKMessageInfo`, `SDKMessagePart` with types that match Fleet's serialized `HarnessMessage` shape. Rewrite `convertSDKMessageToAccumulated` to parse Fleet's flat shape.
  **Files**: `client/src/lib/pagination-utils.ts`
  **Change**:
  
  Replace the type definitions:
  ```typescript
  // OLD: SDKMessage shape (nested info/parts with OpenCode field names)
  // NEW: Fleet message shape (flat fields, camelCase from ASP.NET Core)

  /** Fleet HarnessMessage as serialized by ASP.NET Core (camelCase). */
  export interface FleetMessage {
    id: string;
    role: string;
    parts: FleetMessagePart[];
    timestamp: string;     // ISO 8601 DateTimeOffset
    textContent: string;   // convenience: concatenated text parts
  }

  /** Polymorphic message part — discriminated by "type" field. */
  export interface FleetMessagePart {
    type: string;          // "text" | "tool" | "tool-result"
    kind: number;          // MessagePartKind enum (0=Text, 1=ToolUse, 2=ToolResult) — ignored by frontend
    // TextPart fields
    text?: string;
    // ToolUsePart fields
    toolCallId?: string;
    toolName?: string;
    arguments?: unknown;
    state?: number;        // ToolUseState enum: 0=Pending, 1=Running, 2=Completed, 3=Error
  }
  ```

  **Important casing note**: ASP.NET Core's default camelCase policy converts:
  - `ToolCallId` → `toolCallId` (not `toolCallID`)
  - `ToolName` → `toolName`
  - `Arguments` → `arguments`
  - `State` (ToolUseState enum) → `state` (serialized as integer by default)
  - `Text` → `text`
  - `Timestamp` → `timestamp`
  - `TextContent` → `textContent`

  Rewrite `convertSDKMessageToAccumulated`:
  ```typescript
  export function convertFleetMessageToAccumulated(msg: FleetMessage): AccumulatedMessage {
    const parts: AccumulatedPart[] = [];

    for (const part of msg.parts) {
      if (part.type === "text") {
        // Fleet TextPart has no ID — generate a stable one from message ID + index
        parts.push({ partId: `${msg.id}-text-${parts.length}`, type: "text", text: part.text ?? "" });
      } else if (part.type === "tool") {
        parts.push({
          partId: part.toolCallId ?? `${msg.id}-tool-${parts.length}`,
          type: "tool",
          tool: part.toolName ?? "",
          callId: part.toolCallId ?? "",
          state: mapToolState(part.state),
        });
      }
      // "tool-result" parts are not rendered by the frontend — skip
    }

    // Parse ISO timestamp to Unix ms
    const createdAt = msg.timestamp ? new Date(msg.timestamp).getTime() : undefined;

    return {
      messageId: msg.id,
      sessionId: "",  // Fleet HarnessMessage doesn't carry sessionId — set from context
      role: msg.role === "user" ? "user" : "assistant",
      parts,
      createdAt,
    };
  }

  function mapToolState(state?: number): unknown {
    // ToolUseState enum: 0=Pending, 1=Running, 2=Completed, 3=Error
    const statusMap: Record<number, string> = { 0: "pending", 1: "running", 2: "completed", 3: "error" };
    return state != null ? { status: statusMap[state] ?? "pending" } : { status: "pending" };
  }
  ```

  Keep the old `convertSDKMessageToAccumulated` as a deprecated alias or remove it — but note all call sites need updating.

  Also update `sliceMessages` generic constraint from `{ info: { id: string } }` to `{ id: string }` since Fleet messages have flat `id`:
  ```typescript
  export function sliceMessages<T extends { id: string }>(
    allMessages: T[],
    { limit, before }: SliceOptions,
  ): SliceResult<T> {
    // ... update internal references from m.info.id to m.id
  }
  ```

  **Acceptance**: `convertFleetMessageToAccumulated({ id: "msg-1", role: "assistant", parts: [{ type: "text", kind: 0, text: "Hello" }], timestamp: "2025-01-01T00:00:00Z", textContent: "Hello" })` returns a valid `AccumulatedMessage`.

- [x] 7. **Update `use-message-pagination.ts` to parse Fleet response shape**
  **What**: This hook expects `{ messages: SDKMessage[], pagination: {...} }`. Change it to expect `{ messages: FleetMessage[], pagination: {...} }` and call the renamed converter.
  **Files**: `client/src/hooks/use-message-pagination.ts`
  **Change**:
  - Change the import from `SDKMessage` + `convertSDKMessageToAccumulated` to `FleetMessage` + `convertFleetMessageToAccumulated`.
  - Update the response type cast in both `loadInitialMessages` and `loadOlderMessages`:
    ```typescript
    const data = (await response.json()) as {
      messages: FleetMessage[];
      pagination: {
        hasMore: boolean;
        oldestMessageId: string | null;
        totalCount: number;
      };
    };
    ```
  - Change `convertSDKMessageToAccumulated` calls to `convertFleetMessageToAccumulated`.
  **Acceptance**: Initial message load works with Fleet-shaped responses.

- [x] 8. **Update `use-session-events.ts` to use Fleet types and fix session ID checks**
  **What**: Multiple changes needed in this file:
  1. Replace `SDKMessage` import + `convertSDKMessageToAccumulated` with Fleet types.
  2. Fix `loadAllMessages` URL (currently fetches `/api/sessions/${id}` which returns a Session entity, not messages).
  3. Fix `message.part.updated` handler — remove the broken session ID check OR keep it now that payloads contain Fleet IDs (TODO 4 fixes the data, so the check will now work correctly). Best approach: simplify the check since topic routing already scopes events.
  4. Fix `message.part.delta` handler — same as above.

  **Files**: `client/src/hooks/use-session-events.ts`
  **Changes**:

  (a) Update imports:
  ```typescript
  import { prependMessages, convertFleetMessageToAccumulated } from "@/lib/pagination-utils";
  import type { FleetMessage } from "@/lib/pagination-utils";
  ```

  (b) Fix `loadAllMessages` (line 131) — change URL and response parsing:
  ```typescript
  const url = `/api/sessions/${encodeURIComponent(sessionId)}/messages`;
  // ...
  const data = await response.json() as { messages?: FleetMessage[] };
  if (!data.messages?.length) return;
  const accumulated = data.messages.map(convertFleetMessageToAccumulated);
  ```

  (c) Fix `loadMessagesSince` (line 168) — same response shape change:
  ```typescript
  const data = await response.json() as { messages?: FleetMessage[] };
  if (!data.messages?.length) return;
  const accumulated = data.messages.map(convertFleetMessageToAccumulated);
  ```

  (d) Simplify `message.part.updated` handler (lines 400-414). After TODO 4, event payloads contain Fleet session IDs. The topic routing already scopes events to the correct session. Remove the explicit session ID check to be safe (it's redundant with topic routing):
  ```typescript
  if (type === "message.part.updated") {
    const part = properties?.part;
    if (!part?.messageID) return;
    setMessages((prev) => applyPartUpdate(prev, { ...part, sessionID: sessionId }));
    // ... agent switch logic unchanged
    return;
  }
  ```

  (e) Simplify `message.part.delta` handler (lines 417-424). Same reasoning:
  ```typescript
  if (type === "message.part.delta") {
    const { messageID, partID, field, delta } = properties ?? {};
    if (field !== "text" || !messageID || !partID) return;
    setMessages((prev) =>
      applyTextDelta(prev, messageID, partID, sessionId, delta ?? "")
    );
    return;
  }
  ```

  **Note**: We pass the Fleet `sessionId` (from the hook parameter) to `applyPartUpdate` and `applyTextDelta`, overriding whatever is in the payload. This is defensive — even with TODO 4's rewriting, using the local Fleet ID is safest.

  **Acceptance**: Messages display via both REST load and WebSocket streaming paths.

- [x] 9. **Update `event-state.ts` — `ensureMessage` field names for Fleet events**
  **What**: `ensureMessage` reads `info.sessionID`, `info.time.created`, `info.agent`, `info.modelID`, `info.parentID` from the event data. After TODO 4's session ID rewriting, the event payloads still have the OpenCode event structure (nested `info` object with OpenCode field names like `sessionID` not `sessionId`). The casing comes from OpenCode's original JSON — `RewriteSessionIds` only replaces values, not property names. So `info.sessionID` remains `sessionID` (capital ID). This is fine — `ensureMessage` already handles it correctly. **No change needed for field name parsing.**

  However, review `isRelevantToSession` — it checks `properties?.part?.sessionID`, `properties?.info?.sessionID`, etc. After TODO 4, these will now contain Fleet IDs, so the comparisons against `sessionId` (Fleet ID) will now **pass** instead of **fail**. This is correct behavior — no code change needed, but the function now works correctly rather than being broken.

  **Files**: `client/src/lib/event-state.ts` — **no code changes needed**.
  **Acceptance**: Existing behavior is preserved; `ensureMessage` parses event data correctly.

### Phase 4: Frontend Tests

- [x] 10. **Update `pagination-utils.test.ts` for Fleet message shape**
  **What**: All tests that use `SDKMessage` / `makeFullSDKMsg()` need to use `FleetMessage` shape instead. `sliceMessages` tests need to change from `m.info.id` to `m.id`.
  **Files**: `client/src/lib/__tests__/pagination-utils.test.ts`
  **Changes**:
  - Replace `makeSDKMsg` helper: `{ info: { id }, parts: [] }` → `{ id, role: "assistant", parts: [], timestamp: "", textContent: "" }`
  - Update `sliceMessages` tests: `m.info.id` → `m.id`
  - Replace `makeFullSDKMsg` helper with Fleet shape:
    ```typescript
    function makeFleetMsg(overrides: Partial<FleetMessage> = {}): FleetMessage {
      return {
        id: "msg-1",
        role: "assistant",
        parts: [],
        timestamp: "2025-01-01T00:00:00Z",
        textContent: "",
        ...overrides,
      };
    }
    ```
  - Update all `convertSDKMessageToAccumulated` tests to use `convertFleetMessageToAccumulated` with Fleet shapes:
    ```typescript
    it("converts text parts correctly", () => {
      const msg = makeFleetMsg({
        parts: [{ type: "text", kind: 0, text: "Hello world" }],
      });
      const result = convertFleetMessageToAccumulated(msg);
      expect(result.parts).toHaveLength(1);
      expect(result.parts[0]).toMatchObject({ type: "text", text: "Hello world" });
    });
    ```
  - Test tool part conversion including `mapToolState`:
    ```typescript
    it("converts tool parts with enum state", () => {
      const msg = makeFleetMsg({
        parts: [{ type: "tool", kind: 1, toolCallId: "call-1", toolName: "bash", state: 2 }],
      });
      const result = convertFleetMessageToAccumulated(msg);
      expect(result.parts[0]).toMatchObject({
        type: "tool",
        tool: "bash",
        callId: "call-1",
        state: { status: "completed" },
      });
    });
    ```
  **Acceptance**: `npx vitest run pagination-utils` passes.

- [x] 11. **Update `use-session-events.test.ts` for divergent session ID scenarios**
  **What**: Add tests that prove events with different payload session IDs still work (testing the redundant-check removal). Also update any tests that depend on `SDKMessage` types.
  **Files**: `client/src/hooks/__tests__/use-session-events.test.ts`
  **Changes**:
  - Add test: `"applies text part when event sessionID differs from fleet sessionID"` — create harness with `"fleet-abc"`, dispatch events with `sessionID: "opencode-xyz"` → parts should still be applied.
  - Add test: `"applies text delta when event sessionID differs from fleet sessionID"` — same pattern.
  - The existing `"ignores part updates for a different session"` test: this now tests that missing `messageID` prevents processing (which is still correct). Rename for clarity.
  **Acceptance**: `npx vitest run use-session-events` passes.

- [x] 12. **Update `event-state.test.ts` if needed**
  **What**: Review existing tests. The `ensureMessage` tests use `{ id, sessionID, role }` shape (OpenCode event properties). After TODO 4, these events will still have the same property names — only the *values* change. Tests should still pass as-is.
  **Files**: `client/src/lib/__tests__/event-state.test.ts`
  **Change**: Likely no changes needed. Verify tests pass after the other changes.
  **Acceptance**: `npx vitest run event-state` passes.

### Phase 5: Activity Stream — Verify Compatibility

- [x] 13. **Verify `activity-stream-v1.tsx` compatibility with unchanged `AccumulatedMessage`**
  **What**: The rendering component consumes `AccumulatedMessage` / `AccumulatedPart` which are defined in `api-types.ts`. These types are NOT being changed — only how they're populated. The tool state shape changes from OpenCode's `{ status: "completed", input: {...}, output: {...} }` to Fleet's `{ status: "completed" }` (just the status string). Need to verify:
  1. `ToolCardRouter` and tool state rendering still work with the simplified state shape from REST.
  2. WebSocket events still provide the rich OpenCode tool state (since we only rewrite session IDs, not restructure the payload).
  
  **Files**: `client/src/components/session/activity-stream-v1.tsx` — likely no changes needed.
  **Risk**: Tool state from REST will be `{ status: "completed" }` (mapped from enum), but from WebSocket it'll be `{ status: "completed", input: {...}, output: {...} }`. The frontend reads `part.state` opaquely and accesses `state?.status`, `state?.input`, `state?.output`. REST-loaded messages won't have `input`/`output` in the tool state since `ToolUseState` is an enum that only carries status. This is a **known data loss** for REST-loaded messages but is acceptable because:
  - WebSocket streaming provides the rich state during live sessions
  - The tool card rendering falls back gracefully when input/output are missing
  - Adding full tool state to REST would require changes to `HarnessMessage`/`MessagePart` domain types (future enhancement)
  
  **Acceptance**: Manual verification — tool cards render for both REST-loaded and WebSocket-streamed messages.

### Phase 6: End-to-End Contract Tests

**Motivation**: Backend and frontend test their own layers in isolation, but nothing verifies the full pipeline: OpenCode JSON → mapper → domain types → API serialization → JSON the frontend receives → frontend parsing → valid `AccumulatedMessage`. When either side drifts (e.g., a `[JsonPolymorphic]` discriminator name changes, a field gets renamed to camelCase differently, a new part type is added), a contract test should break immediately rather than requiring manual QA.

**Approach**: Shared JSON fixture files define the **contract** — the exact JSON shape that the Fleet API returns. Backend tests verify that domain types serialize to match this shape. Frontend tests verify that this shape parses into valid `AccumulatedMessage` objects. If either side changes independently, the fixture mismatch causes a test failure.

- [x] 14. **Create shared contract fixture files**
  **What**: Create JSON fixture files that represent the agreed-upon contract between backend and frontend. These live in `tests/contracts/` at the repo root so both `dotnet test` and `vitest` can access them. Four fixture files cover the two contract boundaries (OpenCode→Fleet backend, Fleet API→Frontend).
  **Files**:
  - Create `tests/contracts/opencode-to-fleet-messages.json` — input/expected pairs for REST message mapping
  - Create `tests/contracts/opencode-to-fleet-events.json` — input/expected pairs for WebSocket event translation
  - Create `tests/contracts/fleet-api-messages.json` — Fleet API response JSON that the frontend must parse
  - Create `tests/contracts/fleet-api-events.json` — Fleet WebSocket event payloads that the frontend must handle

  **Content for `tests/contracts/opencode-to-fleet-messages.json`** (derived from `OpenCodeModelsSerializationTests.cs` and `OpenCodeMapperTests.cs` payloads):
  ```json
  {
    "$comment": "Contract: OpenCode message payloads → Fleet API response shape after OpenCodeMapper + System.Text.Json serialization",
    "cases": [
      {
        "name": "user_message_with_text_part",
        "opencode_input": {
          "info": {
            "role": "user",
            "id": "msg-user-1",
            "sessionId": "opencode-sess-1",
            "time": { "created": 1000000 },
            "agent": "default"
          },
          "parts": [
            {
              "type": "text",
              "id": "part-txt-1",
              "sessionId": "opencode-sess-1",
              "messageId": "msg-user-1",
              "text": "Hello world"
            }
          ]
        },
        "expected_fleet_message": {
          "id": "msg-user-1",
          "role": "user",
          "parts": [
            { "type": "text", "kind": 0, "text": "Hello world" }
          ],
          "timestamp": "1970-01-01T00:00:01+00:00",
          "textContent": "Hello world"
        }
      },
      {
        "name": "assistant_message_with_tool_part_completed",
        "opencode_input": {
          "info": {
            "role": "assistant",
            "id": "msg-asst-1",
            "sessionId": "opencode-sess-1",
            "time": { "created": 2000000 },
            "modelId": "gpt-4o",
            "providerId": "openai"
          },
          "parts": [
            {
              "type": "text",
              "id": "part-txt-2",
              "sessionId": "opencode-sess-1",
              "messageId": "msg-asst-1",
              "text": "I'll run a command for you."
            },
            {
              "type": "tool",
              "id": "part-tool-1",
              "sessionId": "opencode-sess-1",
              "messageId": "msg-asst-1",
              "callId": "call-1",
              "tool": "bash",
              "state": { "status": "completed", "input": { "command": "ls" }, "output": { "result": "file.txt" } }
            }
          ]
        },
        "expected_fleet_message": {
          "id": "msg-asst-1",
          "role": "assistant",
          "parts": [
            { "type": "text", "kind": 0, "text": "I'll run a command for you." },
            { "type": "tool", "kind": 1, "toolCallId": "call-1", "toolName": "bash", "arguments": { "command": "ls" }, "state": 2 }
          ],
          "timestamp": "1970-01-01T00:00:02+00:00",
          "textContent": "I'll run a command for you."
        }
      },
      {
        "name": "assistant_message_with_tool_part_pending",
        "opencode_input": {
          "info": {
            "role": "assistant",
            "id": "msg-asst-2",
            "sessionId": "opencode-sess-1",
            "time": { "created": 3000000 }
          },
          "parts": [
            {
              "type": "tool",
              "id": "part-tool-2",
              "sessionId": "opencode-sess-1",
              "messageId": "msg-asst-2",
              "callId": "call-2",
              "tool": "read_file",
              "state": { "status": "pending", "input": { "path": "/tmp/data.txt" } }
            }
          ]
        },
        "expected_fleet_message": {
          "id": "msg-asst-2",
          "role": "assistant",
          "parts": [
            { "type": "tool", "kind": 1, "toolCallId": "call-2", "toolName": "read_file", "arguments": { "path": "/tmp/data.txt" }, "state": 0 }
          ],
          "timestamp": "1970-01-01T00:00:03+00:00",
          "textContent": ""
        }
      },
      {
        "name": "assistant_message_with_reasoning_part",
        "opencode_input": {
          "info": {
            "role": "assistant",
            "id": "msg-asst-3",
            "sessionId": "opencode-sess-1",
            "time": { "created": 4000000 }
          },
          "parts": [
            {
              "type": "reasoning",
              "id": "part-reason-1",
              "sessionId": "opencode-sess-1",
              "messageId": "msg-asst-3",
              "text": "Let me think about this"
            }
          ]
        },
        "expected_fleet_message": {
          "id": "msg-asst-3",
          "role": "assistant",
          "parts": [
            { "type": "text", "kind": 0, "text": "[reasoning] Let me think about this" }
          ],
          "timestamp": "1970-01-01T00:00:04+00:00",
          "textContent": "[reasoning] Let me think about this"
        }
      },
      {
        "name": "assistant_message_empty_parts",
        "opencode_input": {
          "info": {
            "role": "assistant",
            "id": "msg-asst-4",
            "sessionId": "opencode-sess-1",
            "time": { "created": 5000000 }
          },
          "parts": []
        },
        "expected_fleet_message": {
          "id": "msg-asst-4",
          "role": "assistant",
          "parts": [],
          "timestamp": "1970-01-01T00:00:05+00:00",
          "textContent": ""
        }
      }
    ]
  }
  ```

  **Content for `tests/contracts/opencode-to-fleet-events.json`**:
  ```json
  {
    "$comment": "Contract: OpenCode SSE event payloads → Fleet WebSocket event payloads after session ID rewriting",
    "cases": [
      {
        "name": "message_part_updated_text",
        "fleet_session_id": "fleet-sess-abc",
        "opencode_event": {
          "type": "message.part.updated",
          "properties": {
            "sessionID": "opencode-sess-1",
            "part": {
              "id": "part-1",
              "sessionID": "opencode-sess-1",
              "messageID": "msg-1",
              "type": "text",
              "text": "Hello"
            }
          }
        },
        "expected_fleet_event_payload": {
          "sessionID": "fleet-sess-abc",
          "part": {
            "id": "part-1",
            "sessionID": "fleet-sess-abc",
            "messageID": "msg-1",
            "type": "text",
            "text": "Hello"
          }
        }
      },
      {
        "name": "message_part_updated_tool",
        "fleet_session_id": "fleet-sess-abc",
        "opencode_event": {
          "type": "message.part.updated",
          "properties": {
            "sessionID": "opencode-sess-1",
            "part": {
              "id": "tool-part-1",
              "sessionID": "opencode-sess-1",
              "messageID": "msg-2",
              "type": "tool",
              "tool": "bash",
              "callID": "call-1",
              "state": { "status": "running" }
            }
          }
        },
        "expected_fleet_event_payload": {
          "sessionID": "fleet-sess-abc",
          "part": {
            "id": "tool-part-1",
            "sessionID": "fleet-sess-abc",
            "messageID": "msg-2",
            "type": "tool",
            "tool": "bash",
            "callID": "call-1",
            "state": { "status": "running" }
          }
        }
      },
      {
        "name": "message_part_delta",
        "fleet_session_id": "fleet-sess-abc",
        "opencode_event": {
          "type": "message.part.delta",
          "properties": {
            "sessionID": "opencode-sess-1",
            "messageID": "msg-1",
            "partID": "part-1",
            "field": "text",
            "delta": " world"
          }
        },
        "expected_fleet_event_payload": {
          "sessionID": "fleet-sess-abc",
          "messageID": "msg-1",
          "partID": "part-1",
          "field": "text",
          "delta": " world"
        }
      },
      {
        "name": "message_updated",
        "fleet_session_id": "fleet-sess-abc",
        "opencode_event": {
          "type": "message.updated",
          "properties": {
            "info": {
              "id": "msg-1",
              "sessionID": "opencode-sess-1",
              "role": "assistant",
              "time": { "created": 2000000 }
            }
          }
        },
        "expected_fleet_event_payload": {
          "info": {
            "id": "msg-1",
            "sessionID": "fleet-sess-abc",
            "role": "assistant",
            "time": { "created": 2000000 }
          }
        }
      },
      {
        "name": "session_status_idle",
        "fleet_session_id": "fleet-sess-abc",
        "opencode_event": {
          "type": "session.status",
          "properties": {
            "sessionId": "opencode-sess-1",
            "status": { "type": "idle" }
          }
        },
        "expected_fleet_event_payload": {
          "sessionId": "fleet-sess-abc",
          "status": { "type": "idle" }
        }
      }
    ]
  }
  ```

  **Content for `tests/contracts/fleet-api-messages.json`** (this is the pivot — both sides validate against it):
  ```json
  {
    "$comment": "Contract: Fleet API message response shape — backend must produce this, frontend must consume it",
    "messages_response": {
      "messages": [
        {
          "id": "msg-user-1",
          "role": "user",
          "parts": [
            { "type": "text", "kind": 0, "text": "Hello world" }
          ],
          "timestamp": "1970-01-01T00:00:01+00:00",
          "textContent": "Hello world"
        },
        {
          "id": "msg-asst-1",
          "role": "assistant",
          "parts": [
            { "type": "text", "kind": 0, "text": "I'll run a command for you." },
            { "type": "tool", "kind": 1, "toolCallId": "call-1", "toolName": "bash", "arguments": { "command": "ls" }, "state": 2 }
          ],
          "timestamp": "1970-01-01T00:00:02+00:00",
          "textContent": "I'll run a command for you."
        }
      ],
      "pagination": {
        "hasMore": false,
        "oldestMessageId": "msg-user-1",
        "totalCount": 2
      }
    },
    "expected_accumulated": [
      {
        "messageId": "msg-user-1",
        "sessionId": "",
        "role": "user",
        "parts": [
          { "partId": "msg-user-1-text-0", "type": "text", "text": "Hello world" }
        ],
        "createdAt": 1000
      },
      {
        "messageId": "msg-asst-1",
        "sessionId": "",
        "role": "assistant",
        "parts": [
          { "partId": "msg-asst-1-text-0", "type": "text", "text": "I'll run a command for you." },
          { "partId": "call-1", "type": "tool", "tool": "bash", "callId": "call-1", "state": { "status": "completed" } }
        ],
        "createdAt": 2000
      }
    ]
  }
  ```

  **Content for `tests/contracts/fleet-api-events.json`** (WebSocket events as received by the frontend):
  ```json
  {
    "$comment": "Contract: Fleet WebSocket event payloads as received by the frontend (after session ID rewriting + envelope unwrapping)",
    "cases": [
      {
        "name": "text_part_update_creates_message_and_part",
        "session_id": "fleet-sess-abc",
        "events": [
          {
            "type": "message.updated",
            "properties": {
              "info": { "id": "msg-1", "sessionID": "fleet-sess-abc", "role": "assistant" }
            }
          },
          {
            "type": "message.part.updated",
            "properties": {
              "sessionID": "fleet-sess-abc",
              "part": { "id": "part-1", "sessionID": "fleet-sess-abc", "messageID": "msg-1", "type": "text", "text": "Hello" }
            }
          }
        ],
        "expected_messages": [
          {
            "messageId": "msg-1",
            "sessionId": "fleet-sess-abc",
            "role": "assistant",
            "parts": [
              { "partId": "part-1", "type": "text", "text": "Hello" }
            ]
          }
        ]
      },
      {
        "name": "text_delta_accumulates",
        "session_id": "fleet-sess-abc",
        "events": [
          {
            "type": "message.part.delta",
            "properties": {
              "sessionID": "fleet-sess-abc",
              "messageID": "msg-1",
              "partID": "part-1",
              "field": "text",
              "delta": "Hello"
            }
          },
          {
            "type": "message.part.delta",
            "properties": {
              "sessionID": "fleet-sess-abc",
              "messageID": "msg-1",
              "partID": "part-1",
              "field": "text",
              "delta": " world"
            }
          }
        ],
        "expected_messages": [
          {
            "messageId": "msg-1",
            "sessionId": "fleet-sess-abc",
            "role": "assistant",
            "parts": [
              { "partId": "part-1", "type": "text", "text": "Hello world" }
            ]
          }
        ]
      },
      {
        "name": "tool_part_update_with_state",
        "session_id": "fleet-sess-abc",
        "events": [
          {
            "type": "message.part.updated",
            "properties": {
              "sessionID": "fleet-sess-abc",
              "part": {
                "id": "tool-1",
                "sessionID": "fleet-sess-abc",
                "messageID": "msg-2",
                "type": "tool",
                "tool": "bash",
                "callID": "call-1",
                "state": { "status": "running" }
              }
            }
          }
        ],
        "expected_messages": [
          {
            "messageId": "msg-2",
            "sessionId": "fleet-sess-abc",
            "role": "assistant",
            "parts": [
              { "partId": "tool-1", "type": "tool", "tool": "bash", "callId": "call-1", "state": { "status": "running" } }
            ]
          }
        ]
      }
    ]
  }
  ```

  **Acceptance**: All four JSON files are valid JSON and parseable by both `System.Text.Json` (C#) and `JSON.parse` (TypeScript).

- [x] 15. **Backend contract tests: OpenCode → Fleet API message shape**
  **What**: Load the `opencode-to-fleet-messages.json` fixture, deserialize the OpenCode input payloads, run them through `OpenCodeMapper.ToHarnessMessage()`, serialize the result with the same `System.Text.Json` settings as the API (default camelCase), and assert the JSON matches the `expected_fleet_message` in the fixture.
  **Files**: Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeToFleetContractTests.cs`
  **Change**:
  ```csharp
  using System.Text.Json;
  using WeaveFleet.Domain.Harnesses;
  using WeaveFleet.Infrastructure.Harnesses.OpenCode;

  namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

  /// <summary>
  /// Contract tests verifying that OpenCode message payloads, when mapped through
  /// OpenCodeMapper and serialized with System.Text.Json (API settings), produce
  /// the exact JSON shape defined in the shared contract fixtures.
  /// </summary>
  public sealed class OpenCodeToFleetContractTests
  {
      /// <summary>
      /// Serialization options matching what ASP.NET Core minimal APIs use by default.
      /// Default camelCase naming, no custom converters.
      /// </summary>
      private static readonly JsonSerializerOptions ApiSerializerOptions = new()
      {
          PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      };

      /// <summary>
      /// Options for deserializing OpenCode input payloads from fixtures.
      /// Uses the same options as the real OpenCode HTTP client.
      /// </summary>
      private static readonly JsonSerializerOptions OpenCodeOptions = OpenCodeJsonOptions.Default;

      private static JsonDocument LoadFixture(string filename)
      {
          // Navigate from bin/<config>/<tfm>/ to tests/contracts/
          var binDir = AppContext.BaseDirectory;
          var testsRoot = Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", ".."));
          var path = Path.Combine(testsRoot, "contracts", filename);
          var json = File.ReadAllText(path);
          return JsonDocument.Parse(json);
      }

      [Fact]
      public void All_Message_Cases_Match_Expected_Fleet_Shape()
      {
          using var doc = LoadFixture("opencode-to-fleet-messages.json");
          var cases = doc.RootElement.GetProperty("cases");

          foreach (var testCase in cases.EnumerateArray())
          {
              var name = testCase.GetProperty("name").GetString();
              var openCodeInput = testCase.GetProperty("opencode_input").GetRawText();
              var expectedJson = testCase.GetProperty("expected_fleet_message").GetRawText();

              // Deserialize OpenCode payload
              var msgWithParts = JsonSerializer.Deserialize<OpenCodeMessageWithParts>(
                  openCodeInput, OpenCodeOptions)!;

              // Map through OpenCodeMapper
              var harnessMessage = OpenCodeMapper.ToHarnessMessage(msgWithParts);

              // Serialize with API settings (same as minimal API)
              var actualJson = JsonSerializer.Serialize(harnessMessage, ApiSerializerOptions);

              // Parse both for structural comparison
              using var expectedDoc = JsonDocument.Parse(expectedJson);
              using var actualDoc = JsonDocument.Parse(actualJson);

              AssertJsonEqual(expectedDoc.RootElement, actualDoc.RootElement,
                  $"Contract mismatch for case '{name}'");
          }
      }

      /// <summary>
      /// Deep-compare two JsonElements, ignoring property order.
      /// Asserts structural equality.
      /// </summary>
      private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string context)
      {
          Assert.Equal(expected.ValueKind, actual.ValueKind);

          switch (expected.ValueKind)
          {
              case JsonValueKind.Object:
                  var expectedProps = new Dictionary<string, JsonElement>();
                  foreach (var prop in expected.EnumerateObject())
                      expectedProps[prop.Name] = prop.Value;

                  var actualProps = new Dictionary<string, JsonElement>();
                  foreach (var prop in actual.EnumerateObject())
                      actualProps[prop.Name] = prop.Value;

                  // Check that expected props exist in actual
                  foreach (var (key, expectedVal) in expectedProps)
                  {
                      Assert.True(actualProps.ContainsKey(key),
                          $"{context}: missing property '{key}' in actual. Actual keys: [{string.Join(", ", actualProps.Keys)}]");
                      AssertJsonEqual(expectedVal, actualProps[key], $"{context}.{key}");
                  }
                  break;

              case JsonValueKind.Array:
                  var expectedArr = expected.EnumerateArray().ToList();
                  var actualArr = actual.EnumerateArray().ToList();
                  Assert.Equal(expectedArr.Count, actualArr.Count);
                  for (int i = 0; i < expectedArr.Count; i++)
                      AssertJsonEqual(expectedArr[i], actualArr[i], $"{context}[{i}]");
                  break;

              case JsonValueKind.String:
                  Assert.Equal(expected.GetString(), actual.GetString());
                  break;

              case JsonValueKind.Number:
                  Assert.Equal(expected.GetRawText(), actual.GetRawText());
                  break;

              case JsonValueKind.True:
              case JsonValueKind.False:
                  Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                  break;
          }
      }
  }
  ```

  **Note on fixture access**: The `LoadFixture` helper navigates from the test project's `bin/Debug/net9.0/` directory up to `tests/contracts/`. Alternatively, add `<Content Include="..\..\contracts\**" CopyToOutputDirectory="PreserveNewest" Link="contracts\%(RecursiveDir)%(Filename)%(Extension)" />` to the `.csproj` to copy fixtures into the output directory.

  **Note on `OpenCodeMapper` and `OpenCodeJsonOptions` visibility**: `OpenCodeMapper` is `internal` and `OpenCodeJsonOptions.Default` is `internal`. The test project already has access because `WeaveFleet.Infrastructure.Tests` references `WeaveFleet.Infrastructure` and the infrastructure project presumably has `[InternalsVisibleTo]` (or uses a shared `Directory.Build.props` that enables it). If not, add `[assembly: InternalsVisibleTo("WeaveFleet.Infrastructure.Tests")]` to the infrastructure project.

  **Run**: `dotnet test --filter "FullyQualifiedName~OpenCodeToFleetContractTests"`
  **Acceptance**: All fixture cases pass — the serialized Fleet API JSON matches the expected shape in the contract.

- [x] 16. **Backend contract tests: Event payload session ID rewriting**
  **What**: Load the `opencode-to-fleet-events.json` fixture, feed the OpenCode event payloads through `RewriteSessionIds` (or the full `PumpAsync` path via mocks), and assert the output matches the expected Fleet event payload shape.
  **Files**: Create `tests/WeaveFleet.Infrastructure.Tests/Services/EventRewriteContractTests.cs`
  **Change**:
  ```csharp
  using System.Text.Json;

  namespace WeaveFleet.Infrastructure.Tests.Services;

  /// <summary>
  /// Contract tests verifying that OpenCode SSE event payloads, after session ID
  /// rewriting, match the expected Fleet event payload shape.
  ///
  /// Since RewriteSessionIds is a private method on HarnessEventRelay, these tests
  /// use the same algorithm inline. If/when RewriteSessionIds is extracted to a
  /// static helper (TODO 4), these tests should call it directly.
  /// </summary>
  public sealed class EventRewriteContractTests
  {
      private static JsonDocument LoadFixture(string filename)
      {
          var binDir = AppContext.BaseDirectory;
          var testsRoot = Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", ".."));
          var path = Path.Combine(testsRoot, "contracts", filename);
          return JsonDocument.Parse(File.ReadAllText(path));
      }

      /// <summary>
      /// Mirrors the RewriteSessionIds logic from HarnessEventRelay (TODO 4).
      /// Recursively replaces sessionId/sessionID string values.
      /// </summary>
      private static JsonElement RewriteSessionIds(JsonElement source, string fleetSessionId)
      {
          if (source.ValueKind != JsonValueKind.Object)
              return source;

          using var stream = new MemoryStream();
          using (var writer = new Utf8JsonWriter(stream))
          {
              WriteRewritten(writer, source, fleetSessionId);
          }
          return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
      }

      private static void WriteRewritten(Utf8JsonWriter writer, JsonElement element, string fleetSessionId)
      {
          switch (element.ValueKind)
          {
              case JsonValueKind.Object:
                  writer.WriteStartObject();
                  foreach (var prop in element.EnumerateObject())
                  {
                      writer.WritePropertyName(prop.Name);
                      if ((prop.Name == "sessionId" || prop.Name == "sessionID")
                          && prop.Value.ValueKind == JsonValueKind.String)
                      {
                          writer.WriteStringValue(fleetSessionId);
                      }
                      else
                      {
                          WriteRewritten(writer, prop.Value, fleetSessionId);
                      }
                  }
                  writer.WriteEndObject();
                  break;
              case JsonValueKind.Array:
                  writer.WriteStartArray();
                  foreach (var item in element.EnumerateArray())
                      WriteRewritten(writer, item, fleetSessionId);
                  writer.WriteEndArray();
                  break;
              default:
                  element.WriteTo(writer);
                  break;
          }
      }

      [Fact]
      public void All_Event_Cases_Match_Expected_Fleet_Payload()
      {
          using var doc = LoadFixture("opencode-to-fleet-events.json");
          var cases = doc.RootElement.GetProperty("cases");

          foreach (var testCase in cases.EnumerateArray())
          {
              var name = testCase.GetProperty("name").GetString();
              var fleetSessionId = testCase.GetProperty("fleet_session_id").GetString()!;
              var openCodePayload = testCase.GetProperty("opencode_event")
                  .GetProperty("properties");
              var expectedPayloadJson = testCase.GetProperty("expected_fleet_event_payload").GetRawText();

              // Apply session ID rewriting
              var rewritten = RewriteSessionIds(openCodePayload, fleetSessionId);
              var actualJson = JsonSerializer.Serialize(rewritten);

              using var expectedDoc = JsonDocument.Parse(expectedPayloadJson);
              using var actualDoc = JsonDocument.Parse(actualJson);

              // Deep structural comparison
              Assert.Equal(
                  JsonSerializer.Serialize(expectedDoc.RootElement),
                  JsonSerializer.Serialize(actualDoc.RootElement));
          }
      }
  }
  ```

  **Note**: This test duplicates the `RewriteSessionIds` logic because it's private on `HarnessEventRelay`. When TODO 4 is implemented, if the method is made `internal static`, the test should be updated to call it directly. The contract test still verifies the fixture expectations regardless.

  **Run**: `dotnet test --filter "FullyQualifiedName~EventRewriteContractTests"`
  **Acceptance**: All 5 event fixture cases pass.

- [x] 17. **Frontend contract tests: Fleet API → AccumulatedMessage**
  **What**: Load the `fleet-api-messages.json` fixture, pass each message through `convertFleetMessageToAccumulated()` (from Phase 3), and assert the result matches `expected_accumulated` in the fixture. This is the frontend half of the contract — it proves the frontend can parse what the backend produces.
  **Files**: Create `client/src/lib/__tests__/fleet-message-contract.test.ts`
  **Change**:
  ```typescript
  import { describe, it, expect } from "vitest";
  import { readFileSync } from "fs";
  import { resolve } from "path";
  import { convertFleetMessageToAccumulated } from "@/lib/pagination-utils";
  import type { FleetMessage } from "@/lib/pagination-utils";

  // Load shared contract fixtures from tests/contracts/ (relative to repo root)
  const fixturesDir = resolve(__dirname, "../../../../tests/contracts");

  function loadFixture(filename: string) {
    const raw = readFileSync(resolve(fixturesDir, filename), "utf-8");
    return JSON.parse(raw);
  }

  describe("Fleet API → AccumulatedMessage contract", () => {
    const fixture = loadFixture("fleet-api-messages.json");
    const messages: FleetMessage[] = fixture.messages_response.messages;
    const expectedAccumulated = fixture.expected_accumulated;

    it("has matching message count", () => {
      expect(messages.length).toBe(expectedAccumulated.length);
    });

    messages.forEach((msg: FleetMessage, i: number) => {
      it(`converts message "${msg.id}" to expected AccumulatedMessage shape`, () => {
        const actual = convertFleetMessageToAccumulated(msg);
        const expected = expectedAccumulated[i];

        expect(actual.messageId).toBe(expected.messageId);
        expect(actual.role).toBe(expected.role);
        expect(actual.sessionId).toBe(expected.sessionId);
        expect(actual.parts.length).toBe(expected.parts.length);

        for (let p = 0; p < expected.parts.length; p++) {
          const actualPart = actual.parts[p];
          const expectedPart = expected.parts[p];
          expect(actualPart.partId).toBe(expectedPart.partId);
          expect(actualPart.type).toBe(expectedPart.type);

          if (expectedPart.type === "text") {
            expect((actualPart as { text: string }).text).toBe(expectedPart.text);
          }
          if (expectedPart.type === "tool") {
            expect((actualPart as { tool: string }).tool).toBe(expectedPart.tool);
            expect((actualPart as { callId: string }).callId).toBe(expectedPart.callId);
            expect((actualPart as { state: unknown }).state).toEqual(expectedPart.state);
          }
        }
      });
    });
  });
  ```

  **Run**: `cd client && npx vitest run fleet-message-contract`
  **Acceptance**: All generated test cases pass.

- [x] 18. **Frontend contract tests: Fleet WebSocket events → AccumulatedMessage state**
  **What**: Load the `fleet-api-events.json` fixture, replay each event sequence through `handleEvent()` (from `use-session-events.ts`), and assert the resulting `AccumulatedMessage[]` matches the `expected_messages` in the fixture. This verifies the full WebSocket event → state pipeline.
  **Files**: Create `client/src/lib/__tests__/fleet-event-contract.test.ts`
  **Change**:
  ```typescript
  import { describe, it, expect, vi } from "vitest";
  import { readFileSync } from "fs";
  import { resolve } from "path";
  import type React from "react";
  import type { AccumulatedMessage, SSEEvent } from "@/lib/api-types";
  import { handleEvent } from "@/hooks/use-session-events";

  const fixturesDir = resolve(__dirname, "../../../../tests/contracts");

  function loadFixture(filename: string) {
    const raw = readFileSync(resolve(fixturesDir, filename), "utf-8");
    return JSON.parse(raw);
  }

  /**
   * Minimal state harness — same pattern as use-session-events.test.ts
   * but driven by contract fixture data.
   */
  function createStateHarness(sessionId: string) {
    let messages: AccumulatedMessage[] = [];
    const setMessages = (update: React.SetStateAction<AccumulatedMessage[]>) => {
      messages = typeof update === "function"
        ? (update as (prev: AccumulatedMessage[]) => AccumulatedMessage[])(messages)
        : update;
    };
    const setStatus = vi.fn();
    const setSessionStatus = vi.fn();
    const setError = vi.fn();
    const onAgentSwitchRef: React.MutableRefObject<((agent: string) => void) | undefined> = {
      current: vi.fn(),
    };
    const lastMessageIdRef: React.MutableRefObject<string | null> = { current: null };

    const dispatch = (event: SSEEvent) => {
      handleEvent(
        event,
        sessionId,
        setMessages,
        setStatus,
        setSessionStatus,
        setError,
        onAgentSwitchRef,
        lastMessageIdRef,
      );
    };

    return { dispatch, getMessages: () => messages };
  }

  describe("Fleet WebSocket events → AccumulatedMessage state contract", () => {
    const fixture = loadFixture("fleet-api-events.json");

    for (const testCase of fixture.cases) {
      it(`handles "${testCase.name}"`, () => {
        const harness = createStateHarness(testCase.session_id);

        // Replay all events in sequence
        for (const event of testCase.events) {
          harness.dispatch(event as SSEEvent);
        }

        const messages = harness.getMessages();
        expect(messages.length).toBe(testCase.expected_messages.length);

        for (let m = 0; m < testCase.expected_messages.length; m++) {
          const actual = messages[m];
          const expected = testCase.expected_messages[m];

          expect(actual.messageId).toBe(expected.messageId);
          expect(actual.sessionId).toBe(expected.sessionId);
          expect(actual.role).toBe(expected.role);
          expect(actual.parts.length).toBe(expected.parts.length);

          for (let p = 0; p < expected.parts.length; p++) {
            expect(actual.parts[p].partId).toBe(expected.parts[p].partId);
            expect(actual.parts[p].type).toBe(expected.parts[p].type);

            if (expected.parts[p].type === "text") {
              expect((actual.parts[p] as { text: string }).text).toBe(
                expected.parts[p].text
              );
            }
            if (expected.parts[p].type === "tool") {
              expect((actual.parts[p] as { tool: string }).tool).toBe(
                expected.parts[p].tool
              );
              expect((actual.parts[p] as { callId: string }).callId).toBe(
                expected.parts[p].callId
              );
              expect((actual.parts[p] as { state: unknown }).state).toEqual(
                expected.parts[p].state
              );
            }
          }
        }
      });
    }
  });
  ```

  **Run**: `cd client && npx vitest run fleet-event-contract`
  **Acceptance**: All 3 event sequence cases pass.

## Implementation Order

```
Phase 1 (Backend serialization):       TODO 1 → TODO 2 → TODO 3
Phase 2 (Backend event translation):   TODO 4 → TODO 5
Phase 3 (Frontend adaptation):         TODO 6 → TODO 7 → TODO 8 → TODO 9
Phase 4 (Frontend tests):              TODO 10 → TODO 11 → TODO 12
Phase 5 (Rendering verification):      TODO 13
Phase 6 (E2E contract tests):          TODO 14 → TODO 15 + TODO 16 (parallel) → TODO 17 + TODO 18 (parallel)

Dependencies:
- Phase 3 depends on Phase 1 (frontend needs to know the new API shape)
- Phase 3.TODO 8 benefits from Phase 2 (events have Fleet IDs) but is designed to work either way (overrides sessionID with local Fleet ID)
- Phase 4 depends on Phase 3 (tests validate the new types)
- Phase 5 depends on Phase 3 (needs working messages to verify rendering)
- Phase 1 and Phase 2 are independent and can be done in parallel
- Phase 6 depends on Phase 1 (needs [JsonPolymorphic] for backend contract tests to produce correct JSON)
- Phase 6 depends on Phase 2 (needs RewriteSessionIds for event contract tests)
- Phase 6 depends on Phase 3 (needs convertFleetMessageToAccumulated for frontend contract tests)
- TODO 14 (fixtures) must be done first, then TODO 15+16 (backend) and TODO 17+18 (frontend) can run in parallel
- Phase 6 can be deferred until after Phases 1-5 are complete and verified — it is a safety net, not a blocker
```

## Verification

- [ ] `dotnet test` — all backend tests pass (including contract tests)
- [ ] `cd client && npx vitest run` — all frontend tests pass (including contract tests)
- [ ] Backend: `TextPart` serializes with `"type": "text"` and `"text": "..."` fields
- [ ] Backend: `GET /api/sessions/{id}/messages` returns `{ messages: [...], pagination: {...} }`
- [ ] Backend: WebSocket event payloads contain Fleet session IDs, not OpenCode IDs
- [ ] Frontend: `convertFleetMessageToAccumulated` correctly parses Fleet message shape
- [ ] Frontend: session ID checks in `use-session-events.ts` no longer drop events
- [ ] Manual: open a session → messages display correctly (both initial load and real-time streaming)
- [ ] Contract: `dotnet test --filter "FullyQualifiedName~ContractTests"` — all backend contract tests pass
- [ ] Contract: `cd client && npx vitest run fleet-message-contract` — Fleet API → AccumulatedMessage fixture matches
- [ ] Contract: `cd client && npx vitest run fleet-event-contract` — Fleet WebSocket events → AccumulatedMessage state matches
- [ ] Contract: modifying a fixture field (e.g., rename `toolCallId` to `toolcallid`) causes at least one backend OR frontend contract test to fail

## Risks & Considerations

1. **Tool state data loss on REST path**: `ToolUsePart.State` is a `ToolUseState` enum (Pending/Running/Completed/Error). It serializes as an integer. The frontend maps this to `{ status: "pending"|"running"|"completed"|"error" }`, but loses the rich `input`/`output`/`metadata` data that OpenCode provides. This means REST-loaded tool parts won't show tool inputs/outputs. WebSocket-streamed tool parts (from `message.part.updated`) still carry the full OpenCode state. **Mitigation**: Future enhancement — extend `ToolUsePart` to carry `Input`/`Output` as `JsonElement?` fields.

2. **Part IDs**: `HarnessMessage.Parts` elements don't have IDs. The frontend uses `partId` for React keys and delta accumulation. The converter generates synthetic IDs like `{msgId}-text-{index}`. These are stable for REST loads but won't match the part IDs in WebSocket events (which use OpenCode's original part IDs). **This is fine** because REST and WebSocket paths serve different roles: REST loads complete snapshots (no delta accumulation), WebSocket provides live updates with OpenCode's part IDs. The `applyPartUpdate` and `applyTextDelta` functions in `event-state.ts` use part IDs from the event payload, not from REST data.

3. **`RewriteSessionIds` performance**: The recursive JSON rewriting allocates a `MemoryStream` and `Utf8JsonWriter` per event. For high-frequency events (like `message.part.delta` during streaming), this adds GC pressure. **Mitigation**: The allocations are small (most payloads are < 1KB) and short-lived. If profiling shows issues, we can pool `MemoryStream`/`Utf8JsonWriter` instances.

4. **`loadAllMessages` URL fix**: This function currently calls `GET /api/sessions/${id}` (session detail, not messages). The fix in TODO 8 changes it to `/api/sessions/${id}/messages`. This also means removing the `instanceId` query parameter since the messages endpoint doesn't use it (the orchestrator resolves the instance from the session).

5. **`sliceMessages` type change**: This function is used in `pagination-utils.ts` and its tests. Changing the generic constraint from `{ info: { id: string } }` to `{ id: string }` is a breaking change for any code passing `SDKMessage`-shaped objects. Since we're replacing all call sites, this is fine.

6. **`ToolUseState` enum serialization**: By default, `System.Text.Json` serializes enums as integers. `ToolUseState.Completed` → `2`. The frontend's `mapToolState` converts `2` → `{ status: "completed" }`. If we later add `[JsonConverter(typeof(JsonStringEnumConverter))]` to `ToolUseState`, the enum would serialize as `"Completed"` (PascalCase string), breaking the frontend mapping. **Recommendation**: Leave as integer for now; the explicit mapping in the frontend is clear.

7. **`sessionId` vs `sessionID` in event payloads**: OpenCode events use both `sessionId` and `sessionID` (inconsistently). The `RewriteSessionIds` method handles both. The frontend also reads both (`part.sessionID`, `properties.sessionID`). After rewriting, both variants contain the Fleet session ID.

8. **`isRelevantToSession` in `event-state.ts`**: This function is exported but only used in tests (the old SSE proxy path is gone). After TODO 4, it will start *working correctly* instead of always failing — `properties?.part?.sessionID` will now be the Fleet ID, matching the passed `sessionId`. No code change needed, but behavior changes from "broken" to "correct".
