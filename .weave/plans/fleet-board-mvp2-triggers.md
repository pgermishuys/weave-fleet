# Fleet Board MVP v2 — Backend Triggers & Automated Sync

## TL;DR
> **Summary**: Introduce a backend trigger model for board sync — scheduled triggers for periodic refresh and GitHub webhook-driven triggers for real-time issue events — with dedupe, placement rules, and a provider-agnostic architecture.
> **Estimated Effort**: Large

## Context
### Original Request
Move from manual "Sync now" to automated sync via a backend trigger model. Support scheduled triggers (cron-style) and GitHub webhook/event-driven triggers for assigned/opened/labeled issues. Prepare architecture for future providers beyond GitHub.

### Key Findings
- MVP 1.1 establishes `BoardSource` and `IBoardSyncService` with upsert-by-source-key logic. This is the foundation triggers invoke.
- Existing infrastructure: `OutboxMessage` pattern exists for async processing. NATS event substrate plan exists (`.weave/plans/nats-event-substrate.md`) — may or may not be available by v2.
- No background job infrastructure currently exists in the backend. Will need a hosted service or similar for scheduled triggers.
- GitHub webhooks require an HTTP endpoint to receive events. Fleet API already runs as an ASP.NET app — adding a webhook endpoint is straightforward.
- Webhook verification: GitHub signs payloads with HMAC-SHA256. Must verify signatures.

### Rationale
Manual sync doesn't scale. Users forget to sync, and boards go stale. Scheduled sync provides baseline freshness. Webhook-driven sync provides near-real-time updates for high-signal events (issue opened, assigned to me, labeled). The trigger model abstracts the "when to sync" from the "how to sync," making it extensible to future providers.

## Scope

### In Scope
- `BoardTrigger` entity: configurable trigger per board source
- Scheduled trigger type: interval-based (e.g., every 15 min, every hour)
- Webhook trigger type: GitHub webhook events → board sync
- GitHub webhook endpoint: receive, verify signature, route to board sync
- Trigger processing: dedupe (don't re-sync if nothing changed), rate limiting
- Placement rules: configurable per-source rules for where synced cards land (e.g., "labeled:bug → Bug lane", default → inbox)
- Provider abstraction: `IBoardSyncProvider` interface so future providers plug in without changing trigger/sync infrastructure
- Background hosted service for scheduled trigger execution

### Out of Scope
- Non-GitHub providers (architecture supports them, but only GitHub is implemented)
- Write-back to GitHub
- Real-time WebSocket push to frontend (board refreshes on next poll/navigation)
- Complex workflow automation (auto-archive, auto-move on close)
- Multi-board triggers (triggers are per-source, per-board)
- UI for webhook setup (documented manual GitHub webhook configuration; future: auto-setup via GitHub App)

## Domain Model Additions

```
BoardTrigger
  Id              string (ULID)
  BoardSourceId   string (FK → BoardSources)
  TriggerType     string ("scheduled" | "webhook")
  Config          string (JSON)
    scheduled: { "intervalMinutes": 15 }
    webhook:   { "events": ["issues.opened", "issues.assigned", "issues.labeled"] }
  Enabled         bool
  LastTriggeredAt string? (ISO 8601)
  CreatedAt       string (ISO 8601)
  UpdatedAt       string (ISO 8601)

BoardPlacementRule
  Id              string (ULID)
  BoardSourceId   string (FK → BoardSources)
  Condition       string (JSON: { "type": "label", "value": "bug" })
  TargetLaneId    string (FK → BoardLanes)
  Priority        int (lower = higher priority; first match wins)
  CreatedAt       string (ISO 8601)

IBoardSyncProvider (interface)
  ProviderType    string
  FetchItems(config) → SyncItem[]
  ValidateWebhookPayload(headers, body) → WebhookEvent?
```

**Key decisions:**
- Triggers are per-source, not per-board. A board with 3 GitHub repo sources can have different trigger configs per repo.
- Placement rules are evaluated on card creation only (not on re-sync of existing cards). Existing cards keep their lane.
- `IBoardSyncProvider` abstracts the provider. `GitHubBoardSyncProvider` is the first implementation. Future: Linear, Jira, etc.
- Webhook events are filtered by `BoardTrigger.Config.events`. Only matching events trigger a sync.
- Dedupe: if `BoardSource.LastSyncAt` is within a configurable cooldown (e.g., 60s), skip scheduled trigger. Webhook triggers bypass cooldown.

## API Additions

```
# Trigger management
GET    /api/boards/{boardId}/sources/{sourceId}/triggers     → BoardTrigger[]
POST   /api/boards/{boardId}/sources/{sourceId}/triggers     → BoardTrigger { triggerType, config, enabled }
PATCH  /api/boards/{boardId}/sources/{sourceId}/triggers/{id} → BoardTrigger { config?, enabled? }
DELETE /api/boards/{boardId}/sources/{sourceId}/triggers/{id} → 204

# Placement rules
GET    /api/boards/{boardId}/sources/{sourceId}/rules        → BoardPlacementRule[]
POST   /api/boards/{boardId}/sources/{sourceId}/rules        → BoardPlacementRule { condition, targetLaneId, priority }
PATCH  /api/boards/{boardId}/sources/{sourceId}/rules/{id}   → BoardPlacementRule { condition?, targetLaneId?, priority? }
DELETE /api/boards/{boardId}/sources/{sourceId}/rules/{id}   → 204

# GitHub webhook receiver (not user-facing, called by GitHub)
POST   /api/webhooks/github                                  → 200/204
```

## Deliverables
- [ ] `BoardTrigger` and `BoardPlacementRule` entities + migration
- [ ] `IBoardSyncProvider` interface and `GitHubBoardSyncProvider` implementation
- [ ] Refactor `BoardSyncService` to use provider abstraction
- [ ] Placement rule evaluation in sync upsert flow
- [ ] Scheduled trigger hosted service (`BoardTriggerHostedService`)
- [ ] GitHub webhook endpoint with HMAC-SHA256 signature verification
- [ ] Webhook → trigger routing (match event type to configured triggers)
- [ ] Trigger and placement rule CRUD API endpoints
- [ ] Dedupe / rate limiting for trigger processing
- [ ] Backend tests for triggers, placement rules, webhook verification, provider abstraction
- [ ] Frontend: trigger configuration UI per source
- [ ] Frontend: placement rule configuration UI per source

### Definition of Done
- [ ] Scheduled trigger fires and syncs board on configured interval
- [ ] GitHub webhook event triggers sync for matching board sources
- [ ] Placement rules route new cards to correct lanes
- [ ] Existing cards are not moved by placement rules on re-sync
- [ ] Webhook signature verification rejects invalid payloads
- [ ] Dedupe prevents redundant syncs within cooldown
- [ ] `dotnet test` passes
- [ ] Frontend tests pass

### Guardrails (Must NOT)
- Must NOT implement non-GitHub providers (only the abstraction)
- Must NOT auto-setup GitHub webhooks (manual configuration for now)
- Must NOT write back to GitHub
- Must NOT add real-time WebSocket push for board updates
- Must NOT modify MVP 1/1.1 card movement or manual sync behavior

## TODOs

- [ ] 1. **Migration: Triggers & Placement Rules**
  **What**: Add `board_triggers` and `board_placement_rules` tables. Indexes on `board_triggers(board_source_id)`, `board_triggers(trigger_type, enabled)`, `board_placement_rules(board_source_id, priority)`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/017_add_board_triggers.sql`
  **Acceptance**: Migration applies cleanly.

- [ ] 2. **Domain Entities**
  **What**: Add `BoardTrigger.cs` and `BoardPlacementRule.cs`.
  **Files**: `src/WeaveFleet.Domain/Entities/BoardTrigger.cs`, `src/WeaveFleet.Domain/Entities/BoardPlacementRule.cs`
  **Acceptance**: Entities compile, match migration schema.

- [ ] 3. **Provider Abstraction**
  **What**: Define `IBoardSyncProvider` interface with `FetchItems` and `ValidateWebhookPayload` methods. Create `GitHubBoardSyncProvider` extracting GitHub-specific logic from `BoardSyncService`. Register providers via DI keyed by `providerType`.
  **Files**: `src/WeaveFleet.Application/Services/IBoardSyncProvider.cs`, `src/WeaveFleet.Infrastructure/Services/GitHubBoardSyncProvider.cs`
  **Acceptance**: Existing sync behavior unchanged. Provider is resolved by `BoardSource.ProviderType`.

- [ ] 4. **Refactor BoardSyncService for Provider + Placement Rules**
  **What**: `BoardSyncService` resolves provider from source, calls `FetchItems`, evaluates placement rules for new cards (first matching rule → target lane, else inbox), upserts. Existing cards skip placement rules.
  **Files**: `src/WeaveFleet.Infrastructure/Services/BoardSyncService.cs`
  **Acceptance**: Placement rules work for new cards. Existing sync behavior preserved.

- [ ] 5. **Trigger & Rule Repository Methods**
  **What**: Add CRUD methods for triggers and placement rules to repository.
  **Files**: `src/WeaveFleet.Domain/Repositories/IBoardRepository.cs`, `src/WeaveFleet.Infrastructure/Repositories/BoardRepository.cs`
  **Acceptance**: All CRUD operations work.

- [ ] 6. **Scheduled Trigger Hosted Service**
  **What**: `BoardTriggerHostedService` (IHostedService) that periodically checks for due scheduled triggers and invokes sync. Runs on a timer (e.g., every 60s), queries triggers where `Enabled = true AND TriggerType = 'scheduled' AND (LastTriggeredAt IS NULL OR LastTriggeredAt + IntervalMinutes < now)`. Dedupe: skip if source was synced within cooldown.
  **Files**: `src/WeaveFleet.Infrastructure/Services/BoardTriggerHostedService.cs`
  **Acceptance**: Scheduled triggers fire at configured intervals. Dedupe prevents redundant syncs.

- [ ] 7. **GitHub Webhook Endpoint**
  **What**: `POST /api/webhooks/github` endpoint. Verify HMAC-SHA256 signature from `X-Hub-Signature-256` header. Parse event type from `X-GitHub-Event` header. Route to matching board sources/triggers. Invoke sync for matched sources.
  **Files**: `src/WeaveFleet.Api/Endpoints/WebhookEndpoints.cs`
  **Acceptance**: Valid webhooks trigger sync. Invalid signatures return 401. Unmatched events return 200 (no-op).

- [ ] 8. **Trigger & Rule API Endpoints**
  **What**: CRUD endpoints for triggers and placement rules as defined in API surface.
  **Files**: `src/WeaveFleet.Api/Endpoints/BoardEndpoints.cs`
  **Acceptance**: All endpoints callable with correct status codes.

- [ ] 9. **Backend Tests: Provider & Sync**
  **What**: Test provider abstraction, placement rule evaluation, sync with rules, provider resolution.
  **Files**: `tests/WeaveFleet.Tests/Services/BoardSyncServiceTests.cs`, `tests/WeaveFleet.Tests/Services/GitHubBoardSyncProviderTests.cs`
  **Acceptance**: All sync + placement scenarios covered.

- [ ] 10. **Backend Tests: Triggers & Webhooks**
  **What**: Test scheduled trigger due-date logic, dedupe, webhook signature verification, event routing.
  **Files**: `tests/WeaveFleet.Tests/Services/BoardTriggerHostedServiceTests.cs`, `tests/WeaveFleet.Tests/Endpoints/WebhookEndpointTests.cs`
  **Acceptance**: Trigger timing, dedupe, and webhook security tested.

- [ ] 11. **Frontend: Trigger Configuration UI**
  **What**: Per-source trigger config: enable/disable scheduled sync, set interval. Show webhook URL + secret for manual GitHub setup. Show last triggered time.
  **Files**: `client/src/components/board/BoardTriggerConfig.vue`
  **Acceptance**: Can configure scheduled trigger, see webhook setup instructions.

- [ ] 12. **Frontend: Placement Rule UI**
  **What**: Per-source rule list: add rules (condition type + value → target lane), reorder priority, delete. Preview which lane a new issue would land in.
  **Files**: `client/src/components/board/BoardPlacementRules.vue`
  **Acceptance**: Can add/edit/delete placement rules.

- [ ] 13. **Frontend API Client Extensions**
  **What**: Add trigger and placement rule CRUD client functions.
  **Files**: `client/src/lib/board-api.ts`
  **Acceptance**: All new endpoints have typed client functions.

- [ ] 14. **Frontend Tests**
  **What**: Tests for trigger and placement rule management.
  **Files**: `client/src/stores/__tests__/board.test.ts`
  **Acceptance**: Tests pass.

## Verification
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [ ] `dotnet test` — all tests pass
- [ ] Frontend builds without errors
- [ ] Frontend tests pass
- [ ] Manual smoke test: configure scheduled trigger → wait for interval → board has new cards
- [ ] Manual smoke test: send test webhook payload → matching cards appear/update
- [ ] Manual smoke test: placement rules route new cards to correct lanes
- [ ] Webhook with invalid signature is rejected
