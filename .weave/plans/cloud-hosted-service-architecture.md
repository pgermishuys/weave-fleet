# Weave Fleet Cloud Hosted Service — Architectural Analysis

> Analysis of what it would take to provide a cloud-hosted Weave Fleet service
> with user accounts, secure sign-in, resource sharing, and multi-user projects/sessions.

## Context

The [Constitution](../CONSTITUTION.md) establishes that Weave Fleet is **"local-first, cloud-ready"** (Principle #4) and that **"auth is a first-class citizen"** (Principle #5). This document analyzes the current codebase against those promises and identifies every architectural change required to deliver a cloud-hosted multi-user service.

---

## Current Architecture Strengths

The codebase is well-positioned for this evolution:

| Existing Pattern | Why It Helps |
|-----------------|--------------|
| **Clean architecture** (Domain → Application → Infrastructure → API) | New cloud concerns slot into the right layer without cross-contamination |
| **Repository interfaces** in Application, Dapper implementations in Infrastructure | Swap SQLite → PostgreSQL without touching business logic |
| **`IHarness` / `IHarnessInstance` abstraction** | Can wrap containers instead of local processes |
| **`IEventBroadcaster` interface** | Can swap `InMemoryEventBroadcaster` for Redis/NATS |
| **Scoped DI for all services** | Per-request user context injection is straightforward |
| **`Result<T>` error model** with `FleetError.Unauthorized` | Auth error path already exists in the domain model (currently unused) |
| **OpenTelemetry built in** | Observability for a distributed cloud deployment is already instrumented |

---

## Current Architecture Gaps

Despite Principle #5 ("Auth is not bolted on"), authentication/authorization infrastructure has **not been implemented**:

- **No `User` entity** — no user model exists anywhere in the domain
- **No auth interfaces** — no `IAuthService`, `IIdentityProvider`, or `IUserContext`
- **No auth middleware** — `Program.cs` has no `UseAuthentication()` / `UseAuthorization()`
- **No `UserId` on any entity** — projects, sessions, workspaces, and instances have no ownership
- **No sharing model** — no concept of granting access to resources
- **All endpoints are public** — zero authentication guards on any of the 17 endpoint files
- **WebSocket events are unscoped** — all subscribers see all events
- **In-memory event broadcaster** — doesn't work across multiple server instances
- **Plaintext token storage** — GitHub integration tokens stored unencrypted in `~/.weave/integrations.json`

---

## The 8 Architectural Pillars

### 1. Identity & Account Management

Accounts live **outside Weave Fleet** in an external Identity Provider (IdP).

**Approach:**
- External IdP handles account creation, login, MFA, password reset (Auth0, Azure AD B2C, AWS Cognito, or Keycloak)
- Weave Fleet is an **OIDC Relying Party** — validates JWTs, doesn't issue them
- A **shadow `User` record** in Fleet stores Fleet-specific metadata (preferences, quotas, roles), synced on first login

**What to build:**
- [ ] `User` entity in Domain: `Id`, `ExternalId`, `Email`, `DisplayName`, `Status`, `CreatedAt`, `LastLoginAt`
- [ ] `IUserRepository` interface in Domain + `DapperUserRepository` in Infrastructure
- [ ] `IIdentityProvider` interface in Application (verify tokens, get user info)
- [ ] `NullIdentityProvider` for local mode (returns default user, bypasses auth transparently)
- [ ] `OidcIdentityProvider` for cloud mode (validates JWT against IdP JWKS endpoint)
- [ ] Database migration: `users` table
- [ ] User sync logic: on first token validation, create/update shadow record

**Configuration:**
```json
{
  "Fleet": {
    "Auth": {
      "Enabled": false,
      "Authority": "https://your-idp.com",
      "Audience": "weave-fleet-api",
      "ClientId": "..."
    }
  }
}
```

### 2. Authentication Middleware & Token Flow

**What to build:**
- [ ] ASP.NET Core JWT Bearer authentication scheme
- [ ] Token validation against external IdP's JWKS endpoint
- [ ] `IUserContext` scoped service — extracts `UserId`, `Email`, `Roles` from `HttpContext.User.Claims`
- [ ] Conditional bypass: when `Auth.Enabled = false`, auto-inject default user context (local mode)
- [ ] WebSocket authentication — validate token in query string or first message before accepting upgrade
- [ ] SSE authentication — validate Bearer token in header
- [ ] Add `UseAuthentication()` + `UseAuthorization()` to `Program.cs` middleware pipeline
- [ ] Add `[Authorize]` or `.RequireAuthorization()` to all endpoint groups

**Files impacted:**
- `src/WeaveFleet.Api/Program.cs` (middleware pipeline)
- All 17 files in `src/WeaveFleet.Api/Endpoints/` (add auth requirements)
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs` (token validation before upgrade)
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (register auth services)

### 3. Multi-Tenancy & Resource Ownership

The most cross-cutting change. Every user-facing entity needs ownership.

**Entity changes:**

| Entity | Add Column | Purpose |
|--------|-----------|---------|
| `Project` | `user_id TEXT REFERENCES users(id)` | Who owns this project |
| `Session` | `user_id TEXT REFERENCES users(id)` | Who created this session |
| `Workspace` | `user_id TEXT REFERENCES users(id)` | Who owns this workspace |
| `Instance` | `user_id TEXT REFERENCES users(id)` | Who spawned this instance |

**Repository changes:**
- [ ] Every `ListAsync()` becomes `ListByUserAsync(string userId)`
- [ ] Every `GetByIdAsync()` adds ownership verification
- [ ] Every `InsertAsync()` requires `userId` parameter
- [ ] Update all 6 repository interfaces + 6 Dapper implementations

**Database migration:**
- [ ] `ALTER TABLE` for all entity tables adding `user_id` column
- [ ] Add indexes on `user_id` columns
- [ ] Backfill strategy for existing data (assign to a default local user)

**Known authorization gaps introduced by the task delegation feature** (must be closed before multi-tenancy):

The sub-agent/task delegation feature (`ToolUsePart.ChildSessionId`, `OnComplete` callbacks, ancestor breadcrumbs) was built correctly for a local single-user context, but introduces the following attack surfaces that must be addressed when user identity is added:

| Gap | Location | Risk |
|-----|----------|------|
| `OnComplete.NotifySessionId` accepted from untrusted client with no ownership check | `SessionEndpoints.cs` → `SessionOrchestrator.cs` | Attacker creates a child session with `notifySessionId` pointing to another user's session; callback fires a prompt into that session on completion |
| Ancestor chain walk in `GetSessionDetailAsync` has no authorization check | `SessionService.cs` lines 61–68 | `GET /api/sessions/{any-id}` lets an authenticated user walk up the parent chain to discover and read sessions they don't own |
| `ParentSessionId` exposed on every `SessionListResponse` item without filtering | `SessionDtos.cs` + `SessionEndpoints.cs` `ToListResponse()` | Leaks orchestration topology (parent-child relationships) across all sessions regardless of ownership |
| `childSessionId` extracted from task tool output and used as navigation target without backend permission check | `activity-stream-v1.tsx` `TaskDelegationItem` | If a child session ID leaks, frontend navigates the user directly to a session they don't own |
| `HarnessEventRelay.TryFireCallbacksAsync` fires into target session without verifying same workspace/owner as source | `HarnessEventRelay.cs` | Child session in one user's context can prompt a parent session belonging to a different user |

**Fix pattern**: When `IUserContext` exists (Pillars 1–2), all five of these reduce to the same ownership check — verify `session.UserId == currentUser.Id` (or `session.OrgId` for shared org sessions) before accepting, traversing, or firing into a session. No structural redesign required; only guard clauses need adding at the identified call sites.

**Organization/Team layer** (Phase 2):
- [ ] `Organization` entity: `Id`, `Name`, `OwnerUserId`, `CreatedAt`
- [ ] `OrganizationMember` entity: `OrganizationId`, `UserId`, `Role` (owner/admin/member)
- [ ] Projects can optionally belong to an organization
- [ ] Resources within an org visible to all org members based on role
- [ ] `organizations`, `organization_members` tables

### 4. Connection & Resource Sharing

Two types of sharing:

**A. Project/Session Sharing (collaboration):**
- [ ] `ResourceShare` entity: `Id`, `ResourceType`, `ResourceId`, `OwnerUserId`, `SharedWithUserId`, `Permission` (view/collaborate/admin), `CreatedAt`, `ExpiresAt`
- [ ] `IResourceShareRepository` + Dapper implementation
- [ ] `ISharingService`: grant access, revoke access, list shares, check access
- [ ] API endpoints: `POST /api/shares`, `GET /api/shares`, `DELETE /api/shares/{id}`
- [ ] Real-time notifications: when a shared resource gets activity, notify shared-with users via their WebSocket
- [ ] `resource_shares` table with unique constraint on `(resource_type, resource_id, shared_with_user_id)`

**B. Integration Connection Sharing (e.g., GitHub):**
- [ ] Move from file-based `~/.weave/integrations.json` to database-backed `IntegrationConnection` entity
- [ ] `IntegrationConnection`: `Id`, `UserId`, `OrganizationId` (nullable for org-level), `Provider`, `EncryptedCredentials`, `Scopes`, `CreatedAt`
- [ ] Org-level integrations (e.g., shared GitHub App installation) vs. personal tokens
- [ ] `integration_connections` table

### 5. Database: SQLite → PostgreSQL

**What to build:**
- [ ] `NpgsqlConnectionFactory` implementing existing `IDbConnectionFactory`
- [ ] Connection pooling configuration (Npgsql built-in or PgBouncer)
- [ ] SQL dialect adaptation layer or dual migration scripts (SQLite uses `datetime('now')`, `TEXT`; Postgres uses `NOW()`, `TIMESTAMPTZ`, `VARCHAR`)
- [ ] `DatabaseProvider` config option: `"sqlite"` (default) or `"postgresql"`
- [ ] Factory pattern: connection factory implementation selected by config at startup
- [ ] Row-level security policies as defense-in-depth for tenant isolation

**Configuration:**
```json
{
  "Fleet": {
    "Database": {
      "Provider": "sqlite",
      "ConnectionString": "Data Source=weave-fleet.db",
      "PostgresConnectionString": "Host=...;Database=weave_fleet;..."
    }
  }
}
```

**Key concern:** Dapper SQL is mostly portable, but several SQLite-specific patterns need Postgres equivalents. A migration audit of all repository SQL is required.

### 6. Distributed Event Broadcasting

**Current:** `InMemoryEventBroadcaster` using `System.Threading.Channels`. Single process only.

**What to build:**
- [ ] `RedisEventBroadcaster` or `NatsEventBroadcaster` implementing `IEventBroadcaster`
- [ ] User-scoped topic structure: `sessions:{userId}`, `projects:{userId}`, `orgs:{orgId}`
- [ ] WebSocket subscription filtering — users only receive events for resources they own or have been shared
- [ ] Config option: `EventBroadcaster.Provider` = `"inmemory"` (default) or `"redis"` or `"nats"`
- [ ] Keep `InMemoryEventBroadcaster` for local mode

### 7. Harness Instance Management: Processes → Containers

**This is the single largest architectural change.**

**Current:** `OpenCodeHarness` spawns local OS processes via `OpenCodeProcessManager`. Port range 10000-10999. In-memory `InstanceTracker`.

**What to build:**
- [ ] `ContainerHarness` implementing `IHarness` — provisions isolated containers instead of local processes
- [ ] Container orchestration integration (Kubernetes, Docker API, or Fly.io Machines API)
- [ ] Per-user resource limits (CPU, memory, concurrent session caps)
- [ ] Workspace isolation via container volumes or cloud storage mounts
- [ ] Internal networking — harness containers communicate back to Fleet API over private network, not localhost
- [ ] Health monitoring and auto-shutdown for idle containers
- [ ] Container cleanup on session stop/timeout
- [ ] Persistent `InstanceTracker` (database-backed instead of in-memory) for multi-instance Fleet API

**Interim approach:** Before full containerization, can use per-user process pools with filesystem sandboxing on a single server. This preserves the existing `IHarness.SpawnAsync()` contract while adding user isolation.

### 8. Security Hardening

| Area | What to Add |
|------|-------------|
| **Secrets management** | Encrypted credential storage (Azure Key Vault, AWS Secrets Manager, or encrypted DB columns with envelope encryption) |
| **Rate limiting** | ASP.NET Core rate limiting middleware on auth and mutation endpoints |
| **Audit logging** | `audit_logs` table — every auth event, resource access, sharing action, admin operation |
| **CSRF protection** | Anti-forgery tokens for browser-originated non-GET requests |
| **Security headers** | HSTS, X-Frame-Options, X-Content-Type-Options, Content-Security-Policy |
| **CORS** | Restrict to known cloud domain origins (not `AllowAnyOrigin`) |
| **Input validation** | Strengthen validation on all endpoints; sanitize user-provided strings |
| **WebSocket security** | Validate token before accepting upgrade; rate limit messages |
| **Refresh token rotation** | `refresh_tokens` table with token hash, expiry, and revocation tracking |

---

## Cloud Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                    External IdP                          │
│              (Auth0 / Azure AD B2C / Cognito)            │
│         Account creation, login, MFA, password reset     │
└──────────────────────┬──────────────────────────────────┘
                       │ OIDC tokens (JWT)
                       ▼
┌─────────────────────────────────────────────────────────┐
│              Load Balancer / API Gateway                  │
│           (TLS termination, rate limiting)                │
└──────────────────────┬──────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────┐
│              Weave Fleet API (N instances)                │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ Auth Middle- │→ │ User Context  │→ │  Endpoints    │  │
│  │ ware (JWT)   │  │ (Claims)     │  │  (scoped)     │  │
│  └─────────────┘  └──────────────┘  └───────────────┘  │
│                                                          │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ Sharing Svc  │  │ Session Orch │  │ Event Bcast   │  │
│  │ (access ctl) │  │ (lifecycle)  │  │ (Redis/NATS)  │  │
│  └─────────────┘  └──────────────┘  └───────────────┘  │
└────────────┬─────────────┬────────────────┬─────────────┘
             │             │                │
             ▼             ▼                ▼
     ┌──────────────┐ ┌──────────┐  ┌──────────────┐
     │ PostgreSQL   │ │Container │  │ Redis/NATS   │
     │ (multi-user  │ │Orchestr. │  │ (distributed │
     │  with RLS)   │ │(K8s/Fly) │  │  events)     │
     └──────────────┘ └────┬─────┘  └──────────────┘
                           │
                    ┌──────┴──────┐
                    │  Harness    │  ← Isolated containers
                    │  Containers │     per user session
                    └─────────────┘
```

---

## Effort Estimate

| # | Pillar | Complexity | Files Impacted | Reason |
|---|--------|-----------|----------------|--------|
| 1 | Identity & Accounts | **Medium** | ~10 new | External IdP does heavy lifting; Fleet validates + syncs |
| 2 | Auth Middleware | **Medium** | ~20 modified | ASP.NET built-in JWT Bearer; wire up + conditional bypass |
| 3 | Multi-Tenancy | **High** | ~30+ modified | Touches every entity, repository, query, and endpoint |
| 4 | Connection Sharing | **Medium** | ~10 new | New entity + service + endpoints. Clean greenfield |
| 5 | PostgreSQL Support | **Medium-High** | ~15 modified/new | New factory, SQL dialect diffs, migration tooling |
| 6 | Distributed Events | **Medium** | ~5 new | New broadcaster impl + topic filtering |
| 7 | Container Harnesses | **Very High** | ~15+ new | Fundamentally different execution model |
| 8 | Security Hardening | **Medium** | ~10 modified/new | Incremental, well-understood patterns |

---

## Recommended Implementation Order

| Phase | Pillar | Rationale |
|-------|--------|-----------|
| **Phase 1** | 1. Identity + 2. Auth Middleware | Foundation — everything depends on knowing who the user is |
| **Phase 2** | 3. Multi-Tenancy | Must happen before any cloud deployment; most cross-cutting |
| **Phase 3** | 5. PostgreSQL | Can't serve multiple concurrent users from a single SQLite file |
| **Phase 4** | 6. Distributed Events | Required for multi-instance API deployment |
| **Phase 5** | 4. Connection Sharing | The user-facing collaboration feature |
| **Phase 6** | 8. Security Hardening | Before going live to real users |
| **Phase 7** | 7. Container Harnesses | The big one — can start with process isolation as interim |
| **Phase 8** | 3b. Organizations/Teams | After individual user accounts work end-to-end |

---

## Constitution Alignment

| Constitution Promise | Status | Assessment |
|---------------------|--------|------------|
| "Same codebase, same binary, different configuration" | ✅ Achievable | Architecture supports config-driven mode switching |
| "Auth boundaries from day one" | ❌ Not met | No auth interfaces, entities, or middleware exist |
| "No add-auth-later refactor" | ❌ Not met | A significant refactor is required (especially multi-tenancy) |
| "Null implementation for local" | ❌ Not met | Must be built alongside real implementation |
| "SQLite for local, swappable for cloud" | ✅ Ready | Repository pattern supports this; implementation needed |
| "Real-time by default" | ✅ Exists | SSE + WebSocket in place; needs distributed backend + user scoping |
| "Data capture by default" | ⚠️ Partial | Captures session data; needs user attribution + audit logging |
