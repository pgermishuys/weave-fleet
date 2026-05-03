# Smart Links — Provider-Abstracted URL Enrichment

## TL;DR
> **Summary**: Add a smart links system that auto-detects URLs in session messages, enriches them via pluggable providers (GitHub, Linear, etc.), persists them server-side per session, and renders them in a sidebar panel with live status polling and user dismissal.
> **Estimated Effort**: Large

## Context
### Original Request
Implement "Smart Links" — detect URLs in session messages, enrich with live status (PR merged, issue closed), render in a sidebar panel. Must be provider-agnostic, server-persisted, support dismissal, and poll for live status.

### Key Findings
- **Plugin system** already supports `contextResolvers`, `sidebarPanels`, `sidebarItems` contributions — smart links fits naturally as a new built-in plugin or cross-cutting feature
- **GitHub plugin** has full auth, API proxy (`/api/integrations/github/...`), composables for issues/PRs — can be leveraged for the GitHub smart link provider
- **Backend** uses clean architecture: Domain entities → Repository interfaces → Dapper implementations → minimal API endpoints. SQLite with DbUp migrations (currently at `018_*`)
- **Composable pattern**: `shallowRef` state, `requestId` for dedup, `apiFetch()`, `onUnmounted` cleanup
- **Pinia stores**: simple `defineStore` with `ref`/`shallowRef`, mutation functions
- **User scoping**: repositories filter by `user_id` from `IUserContext` (see `DapperSessionSourceUsageRepository`)

## Objectives
### Core Objective
Build a provider-agnostic smart links system that detects, enriches, persists, and displays URL metadata from session messages.

### Deliverables
- [ ] SmartLink domain entity and repository interface
- [ ] SQLite migration for `smart_links` table
- [ ] Dapper repository implementation
- [ ] CRUD + dismiss API endpoints
- [ ] Frontend `SmartLinkProvider` interface
- [ ] URL detection utility
- [ ] Pinia store for smart links
- [ ] Composable for detection, dedup, polling
- [ ] GitHub smart link provider (PR/issue status)
- [ ] Sidebar panel Vue component
- [ ] Plugin registration (sidebar panel contribution)

### Definition of Done
- [ ] `dotnet build` passes with no errors
- [ ] `npm run build` passes in `client/`
- [ ] `dotnet test` passes all tests
- [ ] `npm run test` passes in `client/`
- [ ] Smart links auto-detected from session messages, shown in sidebar with status badges
- [ ] Dismissed links persist and don't reappear
- [ ] Status polling updates link state (and stops for terminal states)

### Guardrails (Must NOT)
- Must NOT break existing plugin system or GitHub plugin functionality
- Must NOT store API tokens in smart_links table — use existing credential system
- Must NOT poll dismissed or terminal-state links
- Must NOT couple core detection/persistence to any specific provider

## TODOs

### Phase 1: Core Types & Provider Interface

- [x] 1. **Domain entity: SmartLink**
  **What**: Create `SmartLink` entity with fields: `Id`, `SessionId`, `Url`, `ProviderId`, `ResourceType` (pr/issue/etc.), `ResourceId`, `Title`, `Status`, `StatusLabel`, `MetadataJson`, `IsDismissed`, `IsTerminal`, `CreatedAt`, `UpdatedAt`, `UserId`
  **Files**: `src/WeaveFleet.Domain/Entities/SmartLink.cs`
  **Acceptance**: Entity compiles, follows existing entity conventions (public sealed class, string properties with defaults)

- [x] 2. **Repository interface: ISmartLinkRepository**
  **What**: CRUD interface — `ListBySessionIdAsync(sessionId)`, `UpsertAsync(SmartLink)`, `DismissAsync(id)`, `GetBySessionIdAndUrlAsync(sessionId, url)`, `ListActiveBySessionIdAsync(sessionId)` (excludes dismissed)
  **Files**: `src/WeaveFleet.Domain/Repositories/ISmartLinkRepository.cs`
  **Acceptance**: Interface compiles, follows existing repo patterns (returns `Task<>`, uses entity types)

- [x] 3. **SQLite migration: create smart_links table**
  **What**: Migration `019_add_smart_links_table.sql` creating `smart_links` table with columns matching entity. Index on `(session_id, user_id)` and unique constraint on `(session_id, url, user_id)`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/019_add_smart_links_table.sql`
  **Acceptance**: Migration applies cleanly. Table created with expected columns and indexes.

- [x] 4. **Dapper repository: DapperSmartLinkRepository**
  **What**: Implement `ISmartLinkRepository` using Dapper, following `DapperSessionSourceUsageRepository` pattern (constructor injection of `IDbConnectionFactory` + `IUserContext`, user-scoped queries).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSmartLinkRepository.cs`
  **Acceptance**: Compiles, registered in DI container

- [x] 5. **DI registration**
  **What**: Register `DapperSmartLinkRepository` as `ISmartLinkRepository` in the infrastructure DI setup
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (or equivalent registration file)
  **Acceptance**: Repository resolves from DI

- [x] 6. **Frontend SmartLink types**
  **What**: Define TypeScript types: `SmartLink` (id, sessionId, url, providerId, resourceType, resourceId, title, status, statusLabel, metadata, isDismissed, isTerminal, createdAt, updatedAt), `SmartLinkProvider` interface (`id: string`, `canHandle(url: string): boolean`, `resolve(url: string): Promise<SmartLinkResolution | null>`), `SmartLinkResolution` (providerId, resourceType, resourceId, title, status, statusLabel, isTerminal, metadata)
  **Files**: `client/src/plugins/builtin/smart-links/types.ts`
  **Acceptance**: Types compile with no errors

- [x] 7. **URL detection utility**
  **What**: Function `extractUrls(text: string): string[]` — regex-based extraction of HTTP(S) URLs from message text content. Deduplicate results. Unit-testable pure function.
  **Files**: `client/src/plugins/builtin/smart-links/utils/extract-urls.ts`
  **Acceptance**: Extracts URLs from mixed text, deduplicates, handles edge cases (trailing punctuation, markdown links)

- [x] 8. **Unit tests for URL detection**
  **What**: Vitest tests for `extractUrls` — plain URLs, markdown links, duplicates, no URLs, edge cases
  **Files**: `client/src/plugins/builtin/smart-links/__tests__/extract-urls.test.ts`
  **Acceptance**: All tests pass via `npm run test`

### Phase 2: Backend Persistence API

- [x] 9. **Application service: SmartLinkService**
  **What**: Service layer with methods: `ListBySessionIdAsync`, `UpsertAsync`, `DismissAsync`, `BulkUpsertAsync`. Validates session ownership. Returns `Result<T>`.
  **Files**: `src/WeaveFleet.Application/Services/SmartLinkService.cs`
  **Acceptance**: Compiles, follows existing service patterns (constructor DI, FleetError returns)

- [x] 10. **DTOs for smart links API**
  **What**: Request/response records: `SmartLinkDto`, `UpsertSmartLinkRequest`, `DismissSmartLinkRequest`, `SmartLinkListResponse`
  **Files**: `src/WeaveFleet.Application/DTOs/SmartLinkDtos.cs`
  **Acceptance**: Records compile, JSON-serializable

- [x] 11. **API endpoints: SmartLinkEndpoints**
  **What**: Minimal API endpoints under `/api/sessions/{sessionId}/smart-links`:
  - `GET /` → list non-dismissed smart links for session
  - `POST /` → upsert a smart link (used by detection)
  - `POST /bulk` → bulk upsert multiple links
  - `PATCH /{linkId}/dismiss` → mark link as dismissed
  - `GET /all` → list all including dismissed (for dedup)
  **Files**: `src/WeaveFleet.Api/Endpoints/SmartLinkEndpoints.cs`
  **Acceptance**: Endpoints compile, registered in `Program.cs`, return correct status codes

- [x] 12. **Register endpoints in Program.cs**
  **What**: Add `app.MapSmartLinkEndpoints()` call alongside existing endpoint registrations
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: Endpoints accessible at runtime

- [x] 13. **Backend integration tests**
  **What**: Tests for SmartLinkEndpoints — CRUD operations, dismiss behavior, user scoping
  **Files**: `tests/WeaveFleet.Api.Tests/SmartLinkEndpointTests.cs` (or follow existing test structure)
  **Acceptance**: Tests pass via `dotnet test`

### Phase 3: Frontend Store & Composable

- [x] 14. **Pinia store: useSmartLinksStore**
  **What**: Store managing smart links per session. State: `Map<sessionId, SmartLink[]>`, `dismissedUrls: Map<sessionId, Set<string>>`. Actions: `setLinks`, `upsertLink`, `dismissLink`, `isUrlDismissed`.
  **Files**: `client/src/stores/smart-links.ts`
  **Acceptance**: Store compiles, integrates with Pinia devtools

- [x] 15. **Smart links composable: useSmartLinks**
  **What**: Composable accepting `sessionId` ref. On session change: fetches links from API, scans session messages for URLs, runs each URL through registered providers, upserts new links to API. Polling: refreshes non-terminal link statuses every 30s (visibility-aware). Dedup: skips already-known and dismissed URLs. Cleanup on unmount.
  **Files**: `client/src/plugins/builtin/smart-links/composables/use-smart-links.ts`
  **Acceptance**: Composable follows existing patterns (requestId dedup, shallowRef, onUnmounted cleanup)

- [x] 16. **Provider registry: useSmartLinkProviders**
  **What**: Composable/singleton that holds registered `SmartLinkProvider` instances. Methods: `register(provider)`, `findProvider(url): SmartLinkProvider | null` (first provider where `canHandle` returns true).
  **Files**: `client/src/plugins/builtin/smart-links/composables/use-smart-link-providers.ts`
  **Acceptance**: Registry works, providers resolved by URL

### Phase 4: GitHub Provider

- [x] 17. **GitHub smart link provider**
  **What**: Implements `SmartLinkProvider`. `canHandle`: matches `github.com/{owner}/{repo}/pull/{number}` and `github.com/{owner}/{repo}/issues/{number}`. `resolve`: calls existing GitHub API proxy endpoints to fetch PR/issue status. Maps GitHub states to `SmartLinkResolution` (open/closed/merged, terminal detection for merged PRs and closed issues).
  **Files**: `client/src/plugins/builtin/smart-links/providers/github-smart-link-provider.ts`
  **Acceptance**: Correctly parses GitHub URLs, returns enriched status via existing API proxy

- [x] 18. **Register GitHub provider**
  **What**: In the smart-links plugin init or GitHub plugin contributions, register the GitHub smart link provider with the provider registry
  **Files**: `client/src/plugins/builtin/smart-links/index.ts`
  **Acceptance**: GitHub URLs detected in messages get resolved with status

- [x] 19. **Tests for GitHub provider**
  **What**: Unit tests for URL pattern matching and resolution mapping
  **Files**: `client/src/plugins/builtin/smart-links/__tests__/github-smart-link-provider.test.ts`
  **Acceptance**: Tests pass

### Phase 5: Sidebar Panel

- [x] 20. **SmartLinksPanel Vue component**
  **What**: Sidebar panel showing smart links for the active session. Groups by provider. Each link shows: favicon/icon, title, status badge (color-coded: green=open, purple=merged, red=closed), external link icon, dismiss "×" button. Empty state when no links. Uses TailwindCSS + Reka UI components consistent with existing panels (e.g., `GitHubPanel.vue`).
  **Files**: `client/src/plugins/builtin/smart-links/SmartLinksPanel.vue`
  **Acceptance**: Renders links with status, dismiss works, matches existing UI style

- [x] 21. **SmartLinkItem Vue component**
  **What**: Individual link row component — icon, title, status badge, dismiss button. Emits `dismiss` event.
  **Files**: `client/src/plugins/builtin/smart-links/SmartLinkItem.vue`
  **Acceptance**: Renders correctly, dismiss emits event

- [x] 22. **Plugin manifest & registration**
  **What**: Create smart-links plugin manifest with `sidebarItems` (icon: `Link` from lucide) and `sidebarPanels` contribution. Register in `client/src/main.ts` plugin list.
  **Files**: `client/src/plugins/builtin/smart-links/index.ts`, `client/src/main.ts`
  **Acceptance**: Smart Links panel appears in sidebar, clickable, shows panel

- [x] 23. **Component tests for SmartLinksPanel**
  **What**: Vitest + Vue Test Utils tests for panel rendering, dismiss interaction, empty state
  **Files**: `client/src/plugins/builtin/smart-links/__tests__/SmartLinksPanel.test.ts`
  **Acceptance**: Tests pass

### Phase 6: Linear Provider (Stretch)

- [ ] 24. **Linear smart link provider**
  **What**: Implements `SmartLinkProvider`. `canHandle`: matches `linear.app/{workspace}/issue/{id}`. `resolve`: calls Linear API (will need backend proxy endpoint). Maps Linear states to resolution.
  **Files**: `client/src/plugins/builtin/smart-links/providers/linear-smart-link-provider.ts`
  **Acceptance**: Linear URLs detected and enriched (requires Linear API integration on backend)

- [ ] 25. **Linear backend proxy endpoint**
  **What**: API endpoint to proxy Linear API calls for issue status lookup, similar to GitHub proxy pattern
  **Files**: `src/WeaveFleet.Api/Endpoints/LinearEndpoints.cs`, `src/WeaveFleet.Infrastructure/Linear/LinearService.cs`
  **Acceptance**: Endpoint returns Linear issue status

## Verification
- [ ] `dotnet build WeaveFleet.slnx` compiles with no errors or warnings
- [ ] `dotnet test` — all tests pass including new SmartLink tests
- [ ] `npm run build` in `client/` — no TypeScript errors
- [ ] `npm run test` in `client/` — all tests pass including new smart-links tests
- [ ] Manual: open a session with GitHub PR/issue URLs in messages → links appear in sidebar with correct status
- [ ] Manual: dismiss a link → it disappears and doesn't reappear on refresh
- [ ] Manual: merged PR shows "Merged" badge and stops polling
- [ ] No regressions in existing GitHub plugin or session functionality
