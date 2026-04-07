# Cloud-Hosted Weave Fleet — Research Findings

> Comprehensive analysis of authentication, tenant isolation, model provider auth,
> container sandboxing, and compliance (GDPR / SOC 2) for running Weave Fleet
> as a multi-user cloud service.
>
> **Date:** April 7, 2026
> **Related:** [Cloud Hosted Service Architecture](.weave/plans/cloud-hosted-service-architecture.md)

---

## Table of Contents

1. [User Authentication & Tenant Isolation](#1-user-authentication--tenant-isolation)
2. [Model Provider Authentication](#2-model-provider-authentication)
3. [Container Sandboxing for Harness Isolation](#3-container-sandboxing-for-harness-isolation)
4. [Data Privacy, GDPR & SOC 2 Compliance](#4-data-privacy-gdpr--soc-2-compliance)
5. [Cross-Cutting Observations](#5-cross-cutting-observations)

---

## 1. User Authentication & Tenant Isolation

### 1.1 Current State: Zero Isolation

The codebase has **no authentication or authorization** infrastructure:

- **No `User` entity** — no user model exists anywhere in the domain
- **No auth middleware** — `Program.cs` has no `UseAuthentication()` / `UseAuthorization()`
- **No `UserId` on any entity** — all 7 domain entities (Project, Session, Workspace, Instance, WorkspaceRoot, PersistedMessage, SessionCallback) lack ownership
- **All 18 endpoints are public** — zero auth guards on any endpoint file
- **WebSocket events are unscoped** — subscribers see all events via `*` wildcard
- **SSE activity stream is global** — `GET /api/activity-stream` broadcasts all sessions/instances
- **`FleetError.Unauthorized`** exists in the domain model but is never referenced

### 1.2 Data Leakage Vectors (20+ Identified)

**Data isolation:**
- `GET /api/sessions` returns ALL users' sessions
- `GET /api/sessions/{id}/messages` — read any user's conversation history by guessing session ID
- `POST /api/sessions/{id}/prompt` — send commands to any session
- `GET /api/projects`, `/workspaces`, `/instances` — all return unfiltered global data
- `GET /api/instances/{id}/find/files` — search filesystem of any instance's working directory
- GitHub tokens shared via single `~/.weave/integrations.json` file
- Config, skills, analytics all shared globally

**Event isolation:**
- WebSocket subscribes to `*` — every connected client receives ALL events
- Client-side topic filtering only — server never validates topic ownership
- SSE session events accessible for any session ID without ownership check

**Process isolation:**
- All harness processes run as the same OS user
- Shared port range (10000-10999)
- No filesystem sandboxing — processes can access any path on the server

### 1.3 Recommended Authentication Architecture

**External IdP + JWT Bearer** with config-driven local/cloud toggle:

```
┌──────────────────────────────────────────┐
│              External IdP                 │
│       (Auth0 / Azure AD B2C / Keycloak)   │
│   Account creation, login, MFA, password  │
└────────────────┬─────────────────────────┘
                 │ JWT (OIDC)
                 ▼
┌──────────────────────────────────────────┐
│           Weave Fleet API                 │
│  ┌────────────┐   ┌───────────────────┐  │
│  │ JWT Bearer  │──▶│ IUserContext       │  │
│  │ Middleware   │   │ (scoped service)  │  │
│  └────────────┘   └───────────────────┘  │
└──────────────────────────────────────────┘
```

**The `IUserContext` abstraction** — one interface, two implementations:

| Mode | Implementation | Behavior |
|------|---------------|----------|
| **Local** (`Auth.Enabled = false`) | `LocalUserContext` | Returns constant `"local-user"`, `Roles: ["admin"]`. No JWT validation. |
| **Cloud** (`Auth.Enabled = true`) | `ClaimsUserContext` | Extracts `UserId`, `Email`, `Roles` from JWT claims via `IHttpContextAccessor`. |

DI registration picks the implementation based on config. **Everything downstream is identical** — repositories, services, event broadcasting all depend on `IUserContext` and never know the difference.

**Key design principle:** The data model is **always multi-tenant**. Local mode is a single-tenant deployment. All entities always have `UserId`. All queries always filter by `UserId`. Local mode just happens to always get `"local-user"`.

This means:
- No `if (authEnabled)` scattered through the codebase
- Auth code paths are exercised in both modes
- Enabling cloud auth on an existing instance doesn't leak old data (owned by `"local-user"`)

### 1.4 Multi-Tenancy Implementation

**Entity changes** — add `UserId` to all 7 entities:

| Entity | Column | Purpose |
|--------|--------|---------|
| Project | `user_id TEXT` | Who owns this project |
| Session | `user_id TEXT` | Who created this session |
| Workspace | `user_id TEXT` | Who owns this workspace |
| Instance | `user_id TEXT` | Who spawned this instance |
| WorkspaceRoot | `user_id TEXT` | Who configured these scan paths |
| PersistedMessage | (via session) | Inherits from session ownership |
| SessionCallback | (via session) | Validated through source/target session ownership |

**Repository changes** — inject `IUserContext` into repositories (preferred over explicit `userId` parameters):

```csharp
public class DapperSessionRepository(IDbConnectionFactory cf, IUserContext user) : ISessionRepository
{
    public async Task<IReadOnlyList<Session>> ListAsync(...)
    {
        var sql = "SELECT * FROM sessions WHERE user_id = @UserId ...";
        return await conn.QueryAsync<Session>(sql, new { UserId = user.UserId });
    }
}
```

**Event broadcasting** — user-prefixed topics:

```
Topic: "user:{userId}:sessions"
Topic: "user:{userId}:session:{sessionId}"
```

WebSocket subscription validation checks that requested topic prefix matches the authenticated user.

### 1.5 WebSocket Authentication

**Recommended: Token in query string.**

```
ws://host/ws?token=eyJhbGciOiJS...
```

Validate the JWT before calling `AcceptWebSocketAsync()`. ASP.NET Core's JWT Bearer middleware can be configured to read from query strings for WebSocket routes.

### 1.6 Background Service Considerations

`HarnessEventRelay`, `AnalyticsWriterService`, and `AnalyticsRollupService` are `BackgroundService` instances with no `HttpContext`. These need either:
- A "system" context that bypasses user scoping
- Explicitly unscoped repository methods for admin operations
- Special handling for startup recovery (`MarkAllStoppedAsync` must remain unscoped)

### 1.7 Estimated Scope

~10 new files, ~40+ modified files, touching every layer. Most cross-cutting change in the system.

---

## 2. Model Provider Authentication

### 2.1 The Problem

When users run OpenCode locally, they type `/connect` interactively to authenticate with model providers (Anthropic, OpenAI, etc.). In cloud mode, harnesses run headlessly — there's no interactive terminal. Users interact through the Fleet web UI, which proxies prompts via the Fleet API. **There's no way to type `/connect`.**

### 2.2 Current State: The Plumbing Already Exists

Both harness process managers already support environment variable injection:

```
HarnessSpawnOptions.Environment (Dictionary<string,string>)
  → OpenCodeProcessOptions.EnvironmentVariables
    → ProcessStartInfo.Environment
```

The `OpenCodeProcessManager` (line 118-121) and `ClaudeCodeProcessManager` (line 139-142) both iterate over `options.EnvironmentVariables` and inject them into the child process.

**The dictionary is just never populated.** `SessionOrchestrator.CreateSessionAsync()` and `ResumeSessionAsync()` both use the default empty `Environment` dictionary.

Both OpenCode and Claude Code accept standard environment variables that bypass interactive auth entirely:

| Provider | Environment Variable |
|----------|---------------------|
| Anthropic | `ANTHROPIC_API_KEY` |
| OpenAI | `OPENAI_API_KEY` |
| Google | `GEMINI_API_KEY` |
| xAI | `XAI_API_KEY` |
| Mistral | `MISTRAL_API_KEY` |
| Groq | `GROQ_API_KEY` |
| GitHub Copilot | `GITHUB_TOKEN` |
| AWS Bedrock | `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY` + `AWS_REGION` |
| Azure OpenAI | `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT` |

### 2.3 Solution Approaches Analyzed

#### A. Environment Variable Injection (Recommended Primary)

User provides API keys through Fleet UI → Fleet stores encrypted → Fleet injects as env vars at harness spawn time.

**Pros:** Minimal changes (~5 new files, 2-3 modified, zero harness changes), harness-agnostic, per-user cost attribution, follows existing `OPENCODE_SERVER_PASSWORD` pattern.

**Cons:** Fleet becomes custodian of sensitive API keys (requires encryption at rest), users must manually manage key rotation.

#### B. Auth Proxy / Gateway (Optional Enhancement)

Fleet proxies all LLM API calls between harness and provider. Enables centralized metering, budgets, and cost attribution.

**Pros:** Central key management, rich observability, budget enforcement, key rotation in one place.

**Cons:** Significant complexity (must proxy every provider's streaming API correctly), added latency, single point of failure for all LLM calls.

#### C. Config File Pre-seeding

Write credentials to the harness's auth file before spawning. E.g., write `auth.json` for OpenCode with `XDG_DATA_HOME` override.

**Pros:** No harness changes, works for all auth types.

**Cons:** Fragile (coupled to internal file format), race conditions, cleanup reliability, different format per harness.

#### D. OAuth/Device Flow Proxy

Intercept harness auth URLs from process output, surface through Fleet UI.

**Pros:** No key storage, supports OAuth providers natively.

**Cons:** Complex, fragile, doesn't solve API key providers, not truly headless.

#### E. Shared API Key Pool (Optional Layer on A)

Organization provides centralized API keys. Fleet distributes to harness instances.

**Pros:** Zero user friction, central cost management, simplified onboarding.

**Cons:** Rate limit sharing, key exhaustion risk, ToS concerns for some providers.

### 2.4 Recommended Implementation

**Primary: Approach A (Environment Variable Injection)** — smallest change, biggest impact, zero harness modifications needed.

**Enhancement: Approach E (Shared Pool)** layered on top — if user has no personal key for a provider, fall back to org-level key.

**Future: Approach B (Auth Proxy)** as optional per-provider feature for organizations wanting centralized metering.

### 2.5 Implementation Details

**New entity:**
```
ProviderCredential
  Id: string (GUID)
  UserId: string
  ProviderId: string ("anthropic", "openai", etc.)
  CredentialType: string ("api_key", "oauth_token")
  EncryptedValue: string (ASP.NET Core Data Protection)
  CreatedAt, UpdatedAt: string (ISO 8601)
  UNIQUE(user_id, provider_id)
```

**API endpoints:**
- `GET /api/providers` — list known providers with connected status
- `POST /api/providers/{providerId}/credentials` — store an API key
- `DELETE /api/providers/{providerId}/credentials` — remove a stored key
- `GET /api/providers/{providerId}/credentials/status` — check if key exists (without revealing it)

**Orchestrator change** (the only modification to existing code):
```csharp
// In SessionOrchestrator.CreateSessionAsync(), before harness.SpawnAsync():
var credentials = await credentialService.GetForUserAsync(userId, ct);
var environment = BuildProviderEnvironment(credentials);

harnessInstance = await harness.SpawnAsync(new HarnessSpawnOptions
{
    // ... existing fields ...
    Environment = environment  // <-- populated with API keys
}, ct);
```

**Scope:** ~5 new files, 2-3 modified files, zero harness-layer changes.

---

## 3. Container Sandboxing for Harness Isolation

### 3.1 Current Process Model

Harnesses run as local OS processes:

- **OpenCode:** Long-running `opencode serve` HTTP server per session (port 10000-10999)
- **Claude Code:** Short-lived `claude -p` process per prompt (stdio-based)
- **Tracking:** Singleton `ConcurrentDictionary<string, IHarnessInstance>` (`InstanceTracker`)
- **Events:** `HarnessEventRelay` subscribes to each instance's event stream, forwards to `IEventBroadcaster`
- **No isolation:** Same OS user, shared filesystem, shared port range

### 3.2 Why the Existing Abstraction Is Container-Ready

The `IHarness` → `IHarnessInstance` interface is process-agnostic:

- `SessionOrchestrator` calls `harness.SpawnAsync()` — never touches `Process` objects
- `InstanceTracker` stores `IHarnessInstance` (interface, not concrete type)
- `HarnessEventRelay` calls `instance.SubscribeAsync()` (interface method)
- All communication with OpenCode is HTTP-based (`OpenCodeHttpClient`)
- **Everything above the harness infrastructure layer is unchanged by containerization**

### 3.3 Recommended Container Architecture

**Container-per-session** with a hybrid orchestration approach:

```
┌─────────────────────────────────────────────┐
│            Weave Fleet API                   │
│                                              │
│  SessionOrchestrator                         │
│       │                                      │
│       ▼                                      │
│  ContainerOpenCodeHarness (IHarness)         │
│       │                                      │
│       ▼                                      │
│  IContainerOrchestrator                      │
│  ├── DockerContainerOrchestrator (dev/self)  │
│  └── KubernetesContainerOrchestrator (cloud) │
└───────┬──────────────────────────────────────┘
        │ Docker API / K8s API
        ▼
   ┌──────────┐  ┌──────────┐  ┌──────────┐
   │Container │  │Container │  │Container │
   │Session A │  │Session B │  │Session C │
   │ opencode │  │ opencode │  │  claude   │
   │  serve   │  │  serve   │  │  wrapper  │
   │/workspace│  │/workspace│  │/workspace │
   └──────────┘  └──────────┘  └──────────┘
```

### 3.4 Container Image Design

**Separate images per harness type:**

| Image | Contents | Entrypoint |
|-------|----------|------------|
| `weave-fleet/harness-opencode` | opencode binary, git, node, dev tools | `opencode serve --hostname 0.0.0.0 --port 8080` |
| `weave-fleet/harness-claude-code` | claude CLI (Node.js), git | HTTP wrapper that accepts prompts and pipes to `claude -p` |
| `weave-fleet/harness-base` | Common layer: git, curl, jq, ssh-client, non-root user | — |

### 3.5 Isolation Guarantees

| Type | Mechanism | Strength |
|------|-----------|----------|
| **Filesystem** | Each container has its own overlay root FS | Strong — containers can't see each other's files |
| **Process** | PID namespaces | Strong — `ps` inside container A shows nothing from B |
| **Network** | Network namespaces + Docker networks | Configurable — can be fully isolated |
| **Resources** | cgroups v2 (CPU, memory, I/O) | Strong — prevents noisy-neighbor |
| **User** | User namespaces (optional) | Medium — prevents privilege escalation |

**What containers don't protect against:** Container escape (shared kernel), Docker socket access (never mount into harness containers), shared volumes (ensure unique workspace per session), side-channel attacks.

### 3.6 Implementation Approaches

| Approach | Best For | Pros | Cons |
|----------|----------|------|------|
| **A. Docker API** (Docker.DotNet) | Dev, single-server, MVP | Full programmatic control, lowest ops complexity | Single-host only, no auto-scheduling |
| **B. Docker Compose** | — | Declarative | Not suited to dynamic container-per-session |
| **C. Kubernetes** | Cloud production at scale | Production-grade orchestration, multi-node, network policies | Highest ops complexity, overkill for small deployments |
| **D. Fly.io Machines** | Small-medium cloud | Sub-second cold starts, no cluster mgmt | Vendor lock-in |
| **E. Hybrid** (recommended) | All deployments | Config-driven mode switching | Must maintain multiple implementations |

### 3.7 Key New Interfaces

```csharp
public interface IContainerOrchestrator
{
    Task<ContainerInfo> CreateAndStartAsync(ContainerDefinition definition, CancellationToken ct);
    Task StopAsync(string containerId, TimeSpan timeout, CancellationToken ct);
    Task RemoveAsync(string containerId, CancellationToken ct);
    Task<ContainerStatus> GetStatusAsync(string containerId, CancellationToken ct);
}
```

**Configuration-driven mode selection:**

```json
{
  "Fleet": {
    "HarnessMode": "process",          // "process" (default) or "container"
    "ContainerBackend": "docker",       // "docker" or "kubernetes"
    "Container": {
      "OpenCodeImage": "weave-fleet/harness-opencode:latest",
      "ClaudeCodeImage": "weave-fleet/harness-claude-code:latest",
      "Network": "weave-fleet",
      "MemoryLimitBytes": 2147483648,
      "CpuLimit": 1.0
    }
  }
}
```

### 3.8 Workspace Volume Strategy

```
Host: /data/workspaces/{workspace-id}/  →  Container: /workspace (read-write)
Host: /data/config/{session-id}/        →  Container: /home/harness/.config (read-write)
```

Existing isolation strategies (`existing`, `worktree`, `clone`) work on the host **before** container creation. The resulting directory is then volume-mounted into the container.

### 3.9 Claude Code Container Challenge

Claude Code uses a process-per-prompt model (`claude -p` per prompt). Creating a new container per prompt would be too slow. Solution: the container runs a lightweight HTTP wrapper that:
1. Accepts prompt requests from Fleet via HTTP
2. Spawns `claude -p <prompt> --resume <session-id>` inside the already-running container
3. Streams NDJSON output back to Fleet

This makes Claude Code architecturally similar to OpenCode from Fleet's perspective (long-running container with HTTP API).

### 3.10 Challenges and Mitigations

| Challenge | Mitigation |
|-----------|------------|
| Cold start latency (2-10s vs <1s) | Pre-pulled images, warm container pool, progress feedback in UI |
| Image size (1-2 GB) | Multi-stage builds, shared base layer, private registry with caching |
| Zombie containers | Label-based GC background service, idle timeout auto-shutdown |
| Idle resource consumption | Auto-stop after N minutes idle, resume on demand |
| Secrets in containers | Env var injection at create time, never bake into images |

### 3.11 Estimated Scope

~8-10 new files, 1-2 modified files (`DependencyInjection.cs`, `FleetOptions.cs`), plus Docker image definitions. Well-isolated from existing code — the process-based harnesses remain untouched.

---

## 4. Data Privacy, GDPR & SOC 2 Compliance

### 4.1 Does GDPR Apply?

**Yes, unambiguously.** GDPR applies the moment any EU/EEA resident is a user (Art. 3(2)), regardless of where Weave Fleet is hosted. The data involved (email, session prompts, login timestamps, API keys) is personal data under Art. 4(1).

**Weave Fleet is the data controller.** Anthropic and OpenAI are data processors (confirmed by their DPAs).

### 4.2 Database Isolation Models and Privacy Implications

Three models are under consideration, each with different compliance characteristics:

#### Model 1: Shared Database + Row-Level Security (RLS)

All users share one database. Isolation enforced by `WHERE user_id = @UserId` in application code, with PostgreSQL RLS as defense-in-depth.

```sql
ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY sessions_user_isolation ON sessions
    USING (user_id = current_setting('app.current_user_id'));
```

| Dimension | Assessment |
|-----------|------------|
| **Isolation strength** | Software-level. A bug = data leak. |
| **GDPR Art. 17 (erasure)** | Must `DELETE FROM` every table by user_id. Risk of missing tables. Most error-prone. |
| **GDPR Art. 25 (privacy by design)** | Weakest story — regulators may view skeptically for AI processing. |
| **SOC 2 CC6 audit** | Auditors will scrutinize RLS extensively. Likely to generate observations. |
| **Breach scope** | One SQL injection = all users exposed. |
| **Operational cost** | Lowest. One DB to manage. |

#### Model 2: Schema-per-Tenant

One PostgreSQL database, each user/org gets their own schema. Connection middleware sets `SET search_path = tenant_{id}` per request.

| Dimension | Assessment |
|-----------|------------|
| **Isolation strength** | Structural. Wrong schema = table doesn't exist. |
| **GDPR Art. 17 (erasure)** | `DROP SCHEMA tenant_{id} CASCADE`. Clean and auditable. |
| **GDPR Art. 25 (privacy by design)** | Good story — data structurally separated. |
| **SOC 2 CC6 audit** | Clean. Schema-level access controls are verifiable. |
| **Breach scope** | Application-layer bug could cross schemas, but direct DB breach still total. |
| **Operational cost** | Moderate. Migration per-schema adds complexity. |

#### Model 3: Database-per-Tenant

Each user/org gets their own PostgreSQL database (or server).

| Dimension | Assessment |
|-----------|------------|
| **Isolation strength** | Physical. Separate credentials, separate encryption keys possible. |
| **GDPR Art. 17 (erasure)** | `DROP DATABASE`. Trivially complete. |
| **GDPR Art. 25 (privacy by design)** | Strongest. Best regulatory posture. |
| **SOC 2 CC6 audit** | Trivially demonstrable isolation. |
| **Breach scope** | DB-level breach affects one tenant only. |
| **Operational cost** | Highest. Every migration runs N times. Monitoring × N. |

#### Recommended Hybrid Approach

| Customer Tier | Isolation Model | Rationale |
|---------------|-----------------|-----------|
| **Free / Individual** | Shared DB + RLS | Cost-effective, good-enough isolation |
| **Team / Business** | Schema-per-tenant | Stronger boundary, easy data export/erasure |
| **Enterprise** | Dedicated database (possibly dedicated region) | Contractual data sovereignty, compliance |

### 4.3 GDPR Requirements — Detailed Analysis

#### Lawful Basis (Art. 6)

| Data Type | Lawful Basis | Rationale |
|-----------|-------------|-----------|
| Account data (email, name, login times) | **Contract performance** (Art. 6(1)(b)) | Necessary to provide the service |
| Session data (prompts & AI responses) | **Contract performance** | Core service delivery |
| Workspace metadata | **Contract performance** | Necessary to route sessions |
| Provider API keys (encrypted) | **Contract performance** | Necessary to execute model calls |
| Analytics beyond billing | **Consent** or **Legitimate interest** with balancing test | Must be opt-in if behavioral |
| GitHub OAuth tokens | **Contract performance** | Necessary for GitHub integration |

#### Data Minimization (Art. 5(1)(c))

**Strictly necessary:** Email, session content (while active + replay), container binding, API keys, billing metrics.

**Requires separate justification:** Full conversation history retained indefinitely (needs retention policy), directory paths in metadata (consider hashing post-session), IP addresses beyond 90 days, detailed tool invocation analytics.

#### Right of Access (Art. 15) — 1 month SLA

Must be able to assemble on request:
- Complete account record
- All session conversations (prompts + responses)
- Workspace/project metadata
- Provider configuration (which providers, not raw keys)
- Analytics (token usage, costs)
- Processing info (list of processors, transfer mechanisms, retention periods)

**Architecture implication:** Need a data export API. Easier with schema-per-tenant (single query scope) than shared RLS (cross-table aggregation with filtering).

#### Right to Erasure (Art. 17) — 30-day SLA

**Must delete:** All database records by user_id, encrypted API keys, logs containing user_id/email, container volumes.

**The backup problem:** GDPR doesn't require restoring backups to delete records, but:
- Document that backups contain personal data subject to retention schedule
- Ensure backups expire within defined window (e.g., 90-day rolling)
- Implement "deleted users" exclusion list to prevent restoration

**AI context window:** Prompts already sent to model APIs cannot be "un-sent." However:
- Anthropic and OpenAI commit to not training on API customer content
- Anthropic DPA: deletion within 30 days of agreement termination
- Inference computation is ephemeral — context windows not persisted post-call
- DPIA should document this residual risk

**Erasure by isolation model:**
- Shared DB: `DELETE FROM` every table by user_id — most error-prone
- Schema-per-tenant: `DROP SCHEMA` — clean and complete
- Database-per-tenant: `DROP DATABASE` — trivially auditable

#### Data Protection Impact Assessment (Art. 35) — MANDATORY

A DPIA is **required** for Weave Fleet due to:
- AI model APIs = "new technology" trigger
- Potentially sensitive data (user code may contain credentials, PII, health records)
- Profile-based processing (token analytics + session data = developer behavior profile)

Must produce: risk register, documented mitigations, DPO/legal sign-off, annual review trigger.

#### Data Processing Agreements (Art. 28) — Required with ALL Processors

| Processor | DPA Status | Key Terms |
|-----------|------------|-----------|
| **Anthropic** | Available (eff. Feb 24, 2025) | SCCs Module 2, Irish law. No training on customer content. 48hr breach notification. 30-day deletion post-termination. |
| **OpenAI** | Available (eff. Jan 1, 2026) | SCCs Module 2, OpenAI Ireland Ltd. for EEA. No training on API data. |
| **Cloud provider** (AWS/GCP/Azure) | Available | Standard DPAs with SCCs. |
| **Identity provider** (Auth0/etc.) | Available | Must execute before processing auth data. |
| **GitHub** | Available | Review Enterprise terms for OAuth token processing. |
| **Monitoring/logging SaaS** | Must obtain | If logs contain user IDs/emails, this is a processor relationship. |

#### Cross-Border Data Transfers (Art. 44-49)

EU users' prompts routed to US-based model providers is a restricted transfer.

**Transfer mechanisms:**
- Both Anthropic and OpenAI use **Standard Contractual Clauses (SCCs)** as primary mechanism
- Both have Irish entities (Anthropic Ireland Ltd., OpenAI Ireland Ltd.) as EEA contracting parties
- EU-US Data Privacy Framework (DPF) adequacy decision (July 2023) provides additional coverage

**For EU data residency:** Use AWS Bedrock or Google Vertex AI with EU region Claude endpoints instead of direct Anthropic API. This keeps inference within the EU without cross-border transfer.

#### Breach Notification (Art. 33-34)

**72-hour notification** to supervisory authority from awareness. Direct user notification when "likely high risk."

**Breach examples for Weave Fleet:**
- Cross-tenant data leakage (User A accesses User B's sessions) — **critical, requires user notification**
- Database breach exposing session conversations — **critical**
- GitHub OAuth token leak — **high**
- Container escape accessing another user's workspace — **critical**

**Breach scope by isolation model:**
- Shared DB: one breach potentially exposes ALL users
- Schema-per-tenant: application-layer breach could cross schemas
- Database-per-tenant: DB breach scoped to one tenant — dramatically reduces notification scope

#### Data Retention Schedule

| Data Category | Recommended Retention | Rationale |
|---------------|----------------------|-----------|
| Session content (prompts/responses) | User-configurable, max 12 months default | Product feature; Art. 5(1)(e) storage limitation |
| Session metadata | 3 years | Billing dispute resolution |
| Account data | Duration of account + 30 days post-deletion | Contract performance |
| Login/access timestamps | 90 days | Security monitoring |
| Security logs | 90 days (app) / 1 year (SOC 2) | Security + audit |
| Billing records | 7 years | Tax/accounting legal obligation |
| GitHub OAuth tokens | Duration of integration | Revoke on removal |
| Backups | 90 days rolling | Documented in privacy policy |
| Audit logs | 3-7 years | SOC 2 + accountability |

### 4.4 SOC 2 Requirements

SOC 2 Type II (covering 6-12 month audit period) is the target. Five Trust Service Criteria:

#### Security (CC — Mandatory)

**CC6 — Logical Access Controls (most critical for multi-tenancy):**
- Tenant isolation documented and tested as a security control
- Unique user authentication with MFA for admin accounts
- Least-privilege: internal staff cannot query user data without audit log
- Encryption key management via HSM/KMS
- Access revocation on account deletion

**CC7 — System Operations:**
- Monitoring for anomalous cross-tenant queries
- Automated alerts for unauthorized data access attempts
- Container security monitoring (K8s network policies for lateral movement prevention)

**CC8 — Change Management:**
- Code review before production deployment
- Automated testing that RLS/schema isolation isn't broken by migrations
- Security testing in CI/CD

**Auditor evidence required:** Access control matrix, MFA enrollment exports, annual pen test reports, vulnerability scans, incident response plan, security training records.

#### Availability

- Documented uptime SLA with monitoring dashboards
- Multi-AZ database replication with automated failover
- Container orchestration redundancy (K8s multi-node)
- Backup restoration testing (annual)
- Defined RPO/RTO
- Graceful degradation when model provider APIs are down

#### Processing Integrity

- Session routing correctness: each container bound to specific user_id + session_id
- Response correlation: prompts and responses correctly attributed
- Billing integrity: automated reconciliation of provider usage vs. internal analytics
- Input validation preventing injection attacks

#### Confidentiality

- **Encryption at rest: AES-256** (matches Anthropic DPA requirement)
  - Database-level encryption for all user data
  - Application-level envelope encryption for API keys (KMS-managed keys)
  - Container volume encryption (EBS/PD encryption)
  - Backup encryption with separate KMS key
- **Encryption in transit: TLS 1.2+** (TLS 1.3 preferred)
  - All client-to-server: TLS 1.3
  - Internal service-to-service: mTLS for data-handling services
  - Database connections: TLS required
- **Key management:** Cloud-native KMS, annual rotation, never co-locate keys with encrypted data
- **No API keys in logs** — automated secret scanning in CI/CD

#### Privacy

Substantially overlaps with GDPR. Requires:
- Published privacy notice
- Consent management system
- Data subject rights fulfillment process
- Retention/disposal schedule
- Sub-processor privacy review program

### 4.5 Model Providers Seeing User Code

**Is this a GDPR problem?** Not inherently, if properly structured:

1. Code itself is usually not personal data — it's IP. GDPR applies when natural persons are identifiable.
2. Code **can** contain personal data (comments with names, embedded credentials, test data with real PII).
3. Lawful basis: Art. 6(1)(b) contract performance — user engaged the AI assistant to process their code.
4. Both Anthropic and OpenAI confirm processor role, no training on API data, deletion commitments.

**Residual risk mitigation:**
- Pre-scan code for embedded secrets before including in model context
- Never inject user PII (email, real name) into system prompts — use opaque identifiers
- Document risk in DPIA

### 4.6 Prioritized Compliance Requirements

#### Tier 1: Required Before Serving EU Users (Legal Obligations)

1. Execute DPAs with all processors (Anthropic, OpenAI, cloud provider, IdP)
2. Draft and publish privacy notice (Art. 13: purposes, bases, processors, transfers, retention, rights)
3. Implement data subject rights (self-service export, 30-day deletion, portability)
4. Conduct DPIA for AI processing of user code
5. Create Record of Processing Activities (Art. 30 register)
6. Implement retention policies with automated purging
7. Implement breach response plan (72-hour workflow)

#### Tier 2: Required for SOC 2 Readiness (Commercial Requirement)

8. Achieve schema-per-tenant or database-per-tenant isolation
9. AES-256 encryption at rest, application-level encryption for API keys
10. TLS 1.2+ in transit, TLS 1.3 preferred
11. Centralized immutable audit logging
12. MFA on all internal admin accounts
13. Annual penetration testing with remediation tracking
14. Formal security policies (access control, incident response, change management)

#### Tier 3: Enterprise Readiness

15. Database-per-tenant for highest-value customers
16. Per-tenant encryption keys (enables cryptographic erasure)
17. EU region deployment option
18. Runtime secrets scanning before sending code to model APIs
19. Transfer Impact Assessments for each non-EEA processor
20. DPO designation
21. Container volume cryptographic wiping on session termination

---

## 5. Cross-Cutting Observations

### 5.1 Implementation Order

```
Phase 1: Authentication + IUserContext (foundation — everything depends on user identity)
    │
Phase 2: Provider Credential Storage + Injection (smallest workstream, high value)
    │
Phase 3: Multi-Tenancy (UserId on entities, repository filtering, event scoping)
    │
Phase 4: Container Sandboxing (can proceed in parallel with Phase 3 once auth exists)
    │
Phase 5: PostgreSQL + Isolation Model (shared → schema-per-tenant → DB-per-tenant)
    │
Phase 6: Compliance Documentation (DPIA, privacy notice, DPAs, retention automation)
    │
Phase 7: SOC 2 Preparation (audit logging, pen testing, formal policies)
```

### 5.2 Architecture Was Designed for This

The codebase is well-positioned:
- Clean `IHarness` / `IHarnessInstance` abstraction is container-ready
- `HarnessSpawnOptions.Environment` dictionary exists and is wired through
- `IDbConnectionFactory` interface supports connection string swapping
- `IEventBroadcaster` interface supports alternative implementations
- `FleetOptions` pattern supports config-driven mode switching
- `Result<T>` error model already has `FleetError.Unauthorized`
- `FleetError.Unauthorized` → 401 mapping just needs wiring in `ResultExtensions`

### 5.3 Local Mode Stays Untouched

All workstreams use config-driven switching:
- `Auth.Enabled = false` → `LocalUserContext` (constant "local-user")
- `HarnessMode = "process"` → existing `OpenCodeHarness` / `ClaudeCodeHarness`
- `CredentialOptions.Enabled = true` → works with local-user default
- **Same binary, different `appsettings.json`** — Constitution Principle #4

### 5.4 Three Independent Isolation Boundaries

The combined architecture provides defense-in-depth:

| Boundary | What It Protects | Failure Mode |
|----------|-----------------|--------------|
| **Application layer** (`IUserContext` + repository filtering) | Data queries scoped to authenticated user | Bug in query = cross-tenant leak |
| **Database layer** (RLS / schema / dedicated DB) | Even buggy app code can't read other tenants | DBA access or DB breach |
| **Container layer** (Docker / K8s isolation) | Process, filesystem, network isolation per session | Container escape (kernel exploit) |

A data leak requires **all three to fail simultaneously**.
