# Snapshot Part Buffer — Fix Missing In-Flight Tool Parts on Re-Subscribe

## TL;DR
Add a `MessagePartBuffer` (mirroring `TextDeltaBuffer`) that captures in-flight `message.part.updated`, `message.created`, and `message.updated` event payloads so `BuildAtomicSnapshotAsync` can merge them into the snapshot when a client re-subscribes before the async projection persists them.

## Context
`TextDeltaBuffer` solves the identical problem for streaming text deltas: it buffers fragments in a `ConcurrentDictionary` singleton, the snapshot builder merges them onto persisted messages, and the projection clears them after persistence. Tool parts (`message.part.updated`) and full message snapshots (`message.created`/`message.updated`) lack an equivalent buffer, so re-subscribing during active streaming yields stale snapshots missing tool cards and other non-text parts.

### Key files
- `TextDeltaBuffer.cs` — reference pattern (Application layer, singleton)
- `StreamingStateProvider.cs` — composes buffers into a unified snapshot
- `InProcessFanOutService.cs` — intercepts events for buffering (line 92-97 for text deltas)
- `SessionEventsHub.cs` — `BuildAtomicSnapshotAsync` merges buffered state onto persisted messages
- `HarnessEventPersistenceService.cs` — clears buffers after durable persistence
- `DependencyInjection.cs` — singleton registration

## Scope
- In scope:
  - New `MessagePartBuffer` singleton buffering `message.part.updated` payloads by (sessionId, messageId, partId)
  - New `MessageSnapshotBuffer` singleton buffering `message.created`/`message.updated` payloads by (sessionId, messageId)
  - Extend `StreamingStateProvider` to expose both new buffers
  - Extend `InProcessFanOutService` to populate both buffers
  - Extend `BuildAtomicSnapshotAsync` to merge buffered parts onto the snapshot
  - Extend `HarnessEventPersistenceService` to clear buffers after persistence
  - Unit tests for each new buffer and the merge logic
- Out of scope:
  - Changing the async projection architecture
  - Client-side changes
  - `message.part.delta` (already handled by `TextDeltaBuffer`)
- Constraints / assumptions:
  - All buffers must be singletons with `ConcurrentDictionary` for thread safety
  - Buffer stores the deserialized `MessageEventPart` (for part updates) and `MessageLifecyclePayload` (for message snapshots) — not raw JSON
  - Clear-after-persist semantics must mirror `TextDeltaBuffer` exactly

## Objectives
- Re-subscribing to an active session shows all in-flight tool parts, file parts, step parts
- Re-subscribing shows messages that were created but not yet persisted
- No regression to existing text delta buffering

## Dependencies and Order
1. **MessagePartBuffer** first — standalone, no dependencies
2. **MessageSnapshotBuffer** second — standalone, no dependencies (parallel with 1)
3. **StreamingStateProvider** — depends on 1 & 2 being defined
4. **InProcessFanOutService** — depends on buffer APIs from 1 & 2
5. **BuildAtomicSnapshotAsync merge** — depends on 3 (StreamingStateProvider exposing buffers)
6. **HarnessEventPersistenceService clear** — depends on 1 & 2 being registered
7. **DI registration** — depends on 1 & 2 existing
8. **Integration test** — depends on all above

## Tasks

- [x] 1. Create `MessagePartBuffer` with tests
  - **What**: A singleton `ConcurrentDictionary<(string SessionId, string MessageId, string PartId), MessageEventPart>` mirroring `TextDeltaBuffer`. Methods: `Set(sessionId, messageId, partId, part)`, `SnapshotSession(sessionId)` returning `IReadOnlyDictionary<(string MessageId, string PartId), MessageEventPart>`, `ClearPart(sessionId, messageId, partId)`, `ClearMessage(sessionId, messageId)`.
  - **Files**:
    - `src/WeaveFleet.Application/Services/MessagePartBuffer.cs`
    - `tests/WeaveFleet.Application.Tests/Services/MessagePartBufferTests.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `Set` stores the latest part (last-write-wins, not append)
    - `SnapshotSession` returns only entries for the given session
    - `ClearPart` removes a single entry; `ClearMessage` removes all parts for that message
    - Tests pass

- [x] 2. Create `MessageSnapshotBuffer` with tests
  - **What**: A singleton `ConcurrentDictionary<(string SessionId, string MessageId), MessageLifecyclePayload>` that stores full message snapshots from `message.created`/`message.updated` events. Methods: `Set(sessionId, messageId, payload)`, `SnapshotSession(sessionId)` returning `IReadOnlyDictionary<string MessageId, MessageLifecyclePayload>`, `Clear(sessionId, messageId)`.
  - **Files**:
    - `src/WeaveFleet.Application/Services/MessageSnapshotBuffer.cs`
    - `tests/WeaveFleet.Application.Tests/Services/MessageSnapshotBufferTests.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `Set` overwrites previous entry for the same (sessionId, messageId)
    - `SnapshotSession` scoped to one session
    - `Clear` removes a single entry
    - Tests pass

- [x] 3. Register new buffers in DI
  - **What**: Add `services.AddSingleton<MessagePartBuffer>()` and `services.AddSingleton<MessageSnapshotBuffer>()` next to the existing `TextDeltaBuffer` registration.
  - **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Both singletons resolvable from the container
    - Existing `TextDeltaBuffer` registration unchanged

- [x] 4. Extend `StreamingStateProvider` to include new buffers
  - **What**: Add `MessagePartBuffer` and `MessageSnapshotBuffer` as constructor dependencies. Extend `StreamingStateSnapshot` record to include `BufferedParts` (`IReadOnlyDictionary<string, IReadOnlyDictionary<string, MessageEventPart>>` keyed messageId → partId → part) and `BufferedMessages` (`IReadOnlyDictionary<string, MessageLifecyclePayload>` keyed by messageId). Populate both in `GetStreamingState`.
  - **Files**:
    - `src/WeaveFleet.Application/Services/StreamingStateProvider.cs`
  - **Depends on**: Tasks 1, 2, 3
  - **Acceptance**:
    - `StreamingStateSnapshot` exposes `BufferedParts` and `BufferedMessages`
    - Existing `BufferedDeltas` field unchanged
    - Compiles without breaking existing callers

- [x] 5. Buffer events in `InProcessFanOutService`
  - **What**: After the existing text-delta buffering block (line 92-97), add buffering for:
    - `message.part.updated`: deserialize the `MessageEventPart` from `evt.Payload` and call `MessagePartBuffer.Set`. The part payload structure matches `MessagePartUpdatedPayload` — extract `part` property, deserialize as `MessageEventPart`.
    - `message.created` / `message.updated` (assistant role only, after the user-echo skip): deserialize `MessageLifecyclePayload` from `evt.Payload` and call `MessageSnapshotBuffer.Set`. Reuse the existing `IsUserMessageEcho` guard — buffering happens only for events that pass it.
  - **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessFanOutService.cs`
  - **Depends on**: Tasks 1, 2, 3
  - **Acceptance**:
    - `message.part.updated` events populate `MessagePartBuffer`
    - `message.created`/`message.updated` (non-user) events populate `MessageSnapshotBuffer`
    - Existing text-delta buffering untouched
    - User message echoes are NOT buffered

- [x] 6. Merge buffered parts in `BuildAtomicSnapshotAsync`
  - **What**: In `SessionEventsHub.BuildAtomicSnapshotAsync`, after `ApplyStreamingDeltas`:
    1. Apply `BufferedParts`: for each persisted message, overlay any buffered `MessageEventPart` entries (add missing parts, update existing parts by partId).
    2. Apply `BufferedMessages`: for messages present in `BufferedMessages` but NOT in persisted messages, append them to the result. For messages present in both, merge parts from the buffer that are missing from the persisted version.
  - **Files**: `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
  - **Depends on**: Task 4
  - **Acceptance**:
    - A tool part buffered but not yet persisted appears in the snapshot
    - A message buffered but not yet persisted appears in the snapshot
    - Persisted messages are not duplicated
    - Existing text-delta merge still works

- [x] 7. Clear buffers after persistence in `HarnessEventPersistenceService`
  - **What**:
    - In `TryPersistPartAsync`: after the durable write, call `_partBuffer.ClearPart(sessionId, messageId, partId)`.
    - In `TryPersistMessageAsync`: after the durable write, call `_snapshotBuffer.Clear(sessionId, messageId)`.
    - Add `MessagePartBuffer` and `MessageSnapshotBuffer` as constructor dependencies.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`
  - **Depends on**: Tasks 1, 2, 3
  - **Acceptance**:
    - After `TryPersistPartAsync` succeeds, the part is no longer in `MessagePartBuffer`
    - After `TryPersistMessageAsync` succeeds, the message is no longer in `MessageSnapshotBuffer`
    - Legacy test-only constructor still compiles (pass `new MessagePartBuffer()` and `new MessageSnapshotBuffer()`)

- [x] 8. Add SignalR contract test for snapshot with in-flight parts
  - **What**: In the existing SignalR contract test suite, add a test that:
    1. Broadcasts a `message.part.updated` with a tool part payload
    2. Before the projection persists it, calls `SubscribeToSessionAsync`
    3. Asserts the snapshot contains the tool part
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs` (or a new file in same directory)
  - **Depends on**: Tasks 1–7
  - **Acceptance**:
    - Test subscribes and receives a snapshot containing the buffered tool part
    - Test passes in CI

## Verification
```bash
# Unit tests for new buffers
dotnet test tests/WeaveFleet.Application.Tests -c Debug --filter "FullyQualifiedName~MessagePartBufferTests"
dotnet test tests/WeaveFleet.Application.Tests -c Debug --filter "FullyQualifiedName~MessageSnapshotBufferTests"

# Integration test
dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests"

# Full build to catch any compilation errors
dotnet build -c Release
```
