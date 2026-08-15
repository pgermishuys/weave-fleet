# Restore Message-Ordering Fidelity

## TL;DR
Switch all message ordering and pagination from Fleet's `created_at` (DB insertion clock) to OpenCode's monotonic `timestamp` (with `id` tiebreaker), then delete the client-side sorting workarounds that compensate for broken server ordering.

## Context
OpenCode guarantees message ordering: IDs are monotonically sortable (`msg_<hex timestamp+counter><random>`), and the `timestamp` field is the authoritative creation time. Fleet stores this in the `timestamp` column but orders everything by `created_at`, which reflects when Fleet persisted the row. Placeholder-first insert paths, buffered agent messages, and the DB default `datetime('now')` all cause `created_at` to diverge from true chronology. The client compensates with `sortMessagesByCreatedAt`, ordered insertion in `ensureMessage`, `mergeMessageUpdate` re-sort, and `mergeMessagesByTimestamp`, all keyed on the corrupted `Time.Created` value from the snapshot.

### Key files (current state)
- **Migration**: `src/WeaveFleet.Infrastructure/Migrations/002_add_messages_table.sql` (index on `session_id, timestamp DESC` already exists)
- **MessageRepository**: `src/WeaveFleet.Infrastructure/Data/Repositories/MessageRepository.cs` (3 queries use `ORDER BY m.created_at DESC, m.id DESC`)
- **SessionSnapshotBuilder**: `src/WeaveFleet.Infrastructure/Events/SessionSnapshotBuilder.cs` (cursor lookup + main query + ordering by `created_at`; emits `CreatedAt` as `Time.Created`)
- **MessagePersistenceService**: `src/WeaveFleet.Application/Services/MessagePersistenceService.cs` (sets `CreatedAt = message.Timestamp`, `MergeTimestampAndMetadata` preserves `CreatedAt`)
- **Client sorting**: `client/src/lib/event-state.ts` (sortMessagesByCreatedAt, ensureMessage ordered insert, mergeMessageUpdate re-sort)
- **Client merge**: `client/src/lib/merge-messages.ts` (mergeMessagesByTimestamp)
- **Client tests**: `client/src/lib/__tests__/message-chronological-ordering.test.ts`, `message-ordering.test.ts`, `merge-messages.test.ts`

## Scope
- In scope:
  - Change all message ORDER BY and pagination cursors from `created_at` to `timestamp` (with `id` tiebreaker)
  - Emit `timestamp` as `Time.Created` in snapshot payloads
  - DB migration to backfill `created_at` from `timestamp` for existing rows (or add index on `timestamp` if missing for new ordering)
  - Remove client-side chronological re-sorting workarounds
  - Simplify `mergeMessagesByTimestamp` to append-only (server order is trusted)
  - Update/delete affected client tests
- Out of scope:
  - Changing the `id` generation scheme (OpenCode owns this)
  - Modifying the `use-send-prompt.ts` optimistic message `createdAt` (client clock is fine for optimistic display; reconciliation replaces it)
  - Changing delegation ordering (already uses `created_at ASC` which is fine for delegations)
  - Session-level `created_at` ordering (unrelated to message ordering)
- Constraints / assumptions:
  - The existing index `idx_messages_session_timestamp ON messages(session_id, timestamp DESC)` already exists (migration 002), so no new index is needed for the ordering switch
  - `timestamp` is always populated with OpenCode's ISO 8601 string (verified in `MessagePersistenceService.ToPersistedMessage`)
  - Working tree has in-flight changes; plan reads current file state, not just committed code

## Objectives
- Server snapshot and pagination return messages in OpenCode's original chronological order
- `Time.Created` reflects the authoritative message timestamp, not Fleet's persistence clock
- Client-side sorting workarounds are removed (net code deletion)
- No regression in optimistic prompt reconciliation

## Dependencies and Order
1. **Phase 1 (Backend ordering)** must land first: switch queries + snapshot emission. This makes server responses trustworthy.
2. **Phase 2 (Data migration)** can run in parallel with Phase 1 since it only backfills `created_at` for audit correctness and does not change query behavior (queries will use `timestamp` after Phase 1).
3. **Phase 3 (Client cleanup)** depends on Phase 1. Only safe to delete sorting workarounds once server ordering is correct.
4. **Phase 4 (Verification)** depends on all prior phases.

## Tasks

- [x] 1. Switch MessageRepository queries to order by timestamp
  - **What**: In `MessageRepository.cs`, change all three `ORDER BY m.created_at DESC, m.id DESC` clauses to `ORDER BY m.timestamp DESC, m.id DESC`. Update the cursor-based pagination queries that resolve `cursorCreatedAt` to resolve `cursorTimestamp` instead (lookup the `timestamp` column for the cursor message, filter by `m.timestamp < @CursorTimestamp OR (m.timestamp = @CursorTimestamp AND m.id < @CursorId)`).
  - **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/MessageRepository.cs`
  - **Depends on**: None
  - **Acceptance**:
    - All `ORDER BY` in message queries use `m.timestamp DESC, m.id DESC`
    - Cursor pagination filters on `timestamp` not `created_at`
    - Compiles without errors

- [x] 2. Switch SessionSnapshotBuilder to order/cursor by timestamp
  - **What**: In `SessionSnapshotBuilder.cs`, change the cursor resolution query (line ~58) to select `m.timestamp` instead of `m.created_at`. Change `@CursorCreatedAt` to `@CursorTimestamp`. Change the main message query's WHERE clause and ORDER BY from `created_at` to `timestamp`. Rename the `cursorCreatedAt` variable to `cursorTimestamp`.
  - **Files**: `src/WeaveFleet.Infrastructure/Events/SessionSnapshotBuilder.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Snapshot message query orders by `m.timestamp DESC, m.id DESC`
    - Cursor lookup resolves `m.timestamp`
    - Compiles without errors

- [x] 3. Emit timestamp as Time.Created in snapshot payloads
  - **What**: In `SessionSnapshotBuilder.ToMessageLifecyclePayload` (line ~275), change `ParseUnixTimeMilliseconds(message.CreatedAt)` to `ParseUnixTimeMilliseconds(message.Timestamp)`. In `MessagePersistenceService.BuildCommittedMessagePayloadParts` (line ~192), change the `CommittedMessageTime` construction to parse `persisted.Timestamp` instead of `persisted.CreatedAt`.
  - **Files**: `src/WeaveFleet.Infrastructure/Events/SessionSnapshotBuilder.cs`, `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `Time.Created` in all client-facing payloads reflects the OpenCode `timestamp`, not `created_at`
    - Compiles without errors

- [x] 4. Write a DB migration to backfill created_at from timestamp
  - **What**: Add a new migration (next sequential number) that runs `UPDATE messages SET created_at = timestamp WHERE created_at != timestamp`. This ensures existing rows have consistent data for any audit queries. The `created_at` column remains but is no longer used for ordering.
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/` (new file, next number)
  - **Depends on**: None
  - **Acceptance**:
    - Migration runs idempotently
    - After migration, `created_at` matches `timestamp` for all existing rows
    - Migration is registered in the migration runner

- [x] 5. Remove client-side sortMessagesByCreatedAt and ordered insertion
  - **What**: In `event-state.ts`: (a) Delete the `sortMessagesByCreatedAt` function entirely. (b) In `ensureMessage`, replace the ordered-insertion logic (lines ~66-81) with a simple append (`return [...prev, newMsg]`); server order is now trusted. (c) In `mergeMessageUpdate`, remove the `hasNewCreatedAt` re-sort branch (lines ~152-154); just return `updated` directly.
  - **Files**: `client/src/lib/event-state.ts`
  - **Depends on**: Tasks 1-3 (server ordering must be correct first)
  - **Acceptance**:
    - `sortMessagesByCreatedAt` no longer exists
    - `ensureMessage` appends without insertion-sort
    - `mergeMessageUpdate` does not re-sort on `createdAt` backfill
    - `bun run test` in `client/` passes (after test updates in Task 7)

- [x] 6. Simplify mergeMessagesByTimestamp
  - **What**: In `merge-messages.ts`, simplify `mergeMessagesByTimestamp` to a simple concat: delivered messages first (in server order), then optimistic messages. The timestamp-based sorting is no longer needed since server order is authoritative. Keep the function signature stable so callers don't need changes.
  - **Files**: `client/src/lib/merge-messages.ts`
  - **Depends on**: Tasks 1-3
  - **Acceptance**:
    - Function returns `[...delivered, ...optimistic]`
    - No timestamp-based sorting logic remains
    - `bun run test` in `client/` passes (after test updates in Task 7)

- [x] 7. Update or delete client ordering tests
  - **What**: Review and update these test files for the new behavior: (a) `message-chronological-ordering.test.ts`: tests for insertion-sort behavior should be deleted or rewritten to verify append-only behavior. (b) `message-ordering.test.ts`: keep tests that verify messages appear in server-provided order; delete tests that assert client-side re-sorting. (c) `merge-messages.test.ts`: simplify tests to verify concat behavior (delivered then optimistic).
  - **Files**: `client/src/lib/__tests__/message-chronological-ordering.test.ts`, `client/src/lib/__tests__/message-ordering.test.ts`, `client/src/lib/__tests__/merge-messages.test.ts`
  - **Depends on**: Tasks 5, 6
  - **Acceptance**:
    - All tests reflect the new server-trusting model
    - `bun run test` in `client/` passes with no failures
    - No tests assert client-side chronological re-sorting

- [x] 8. Add a SignalR contract test for snapshot message ordering
  - **What**: Add a test in `tests/WeaveFleet.IntegrationTests/Sessions/` that persists messages with deliberately out-of-order `created_at` values but correct `timestamp` values, builds a snapshot, and asserts the messages arrive in `timestamp` order. This prevents regression.
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/` (new or existing test file)
  - **Depends on**: Tasks 1-3
  - **Acceptance**:
    - Test persists 3+ messages where `created_at` order differs from `timestamp` order
    - Snapshot returns messages sorted by `timestamp ASC`
    - `Time.Created` values in the payload match `timestamp`, not `created_at`
    - Test passes: `dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~MessageOrdering"`

## Verification
1. **Backend**: `dotnet build` succeeds. `dotnet test tests/WeaveFleet.IntegrationTests -c Debug` passes, including the new ordering test.
2. **Client**: `cd client && bun run test` passes with no failures.
3. **E2E** (optional, confirms full stack): `cd client && bun run build && dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"` passes.
4. **Manual smoke test**: Open a session with multiple messages, verify chronological order matches OpenCode's order. Send a new prompt and verify the optimistic message appears at the bottom, then gets reconciled without re-ordering jumps.
