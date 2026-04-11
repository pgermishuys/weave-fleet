# Cloud Track 3: Session Sharing + Collaborative PoC

## TL;DR
> **Summary**: Add explicit session sharing between authenticated Fleet users — owner grants access, participant can view and prompt, events fan out to all authorized participants, and revocation is immediate. This is the collaborative PoC that completes the cloud MVP, culminating in the full end-to-end verification.
> **Estimated Effort**: Medium

## Context

### Original Request
Split the approved `cloud-mvp-implementation.md` umbrella plan into three execution-ready tracks. This is Track 3: session sharing + collaborative PoC.

### Key Findings
- Track 1 establishes `UserId`-scoped multi-tenancy, event-scoping seams (owner-only delivery with participant fan-out seam), and ownership guards
- Track 2 establishes provider credential injection — shared-session resume must use the **owner's** credentials, not the participant's
- `InMemoryEventBroadcaster` needs to evolve from user-scoped to session-access-scoped delivery
- `SessionOrchestrator` is the integration point where access checks and credential sourcing converge
- Constitution Principle #5 and AD-11/AD-12 mandate that sharing is additive to ownership, not a replacement

### Parent Plan
`cloud-mvp-implementation.md` — Architecture Decisions AD-11, AD-12 apply specifically to this track. AD-5 (IUserContext always present) and AD-9 (no bearer tokens in URLs) are inherited from Track 1.

### Relationship to Other Tracks
- **Depends on Track 1**: `IUserContext`, `UserId`-scoped repositories, event-scoping seams (WebSocket/SSE owner-only delivery + participant fan-out seam), ownership guards on delegation paths
- **Depends on Track 2**: `ProviderCredentialService.BuildEnvironmentForUserAsync` (shared-session resume uses owner credentials), live infrastructure for E2E, onboarding flow

## Objectives

### Core Objective
A session owner can explicitly share a running session with another authenticated Fleet user. The participant can view the session, send prompts, and receive real-time events — but cannot stop, resume, share, or revoke. Provider credentials remain those of the owner. Revocation is immediate, including dropping active realtime connections.

### Deliverables
- [ ] `SessionParticipant` entity with role model (Owner implicit, Participant explicit)
- [ ] `ISessionParticipantRepository` + Dapper implementation + migration
- [ ] `SessionAccessService` — centralized authorization for session operations
- [ ] Session sharing API endpoints (share, list participants, revoke, list shared-with-me)
- [ ] Updated session queries/endpoints for owner-or-participant access
- [ ] Session event broadcasting extended from owner-only to authorized-participants
- [ ] SSE activity stream updated for shared-session access
- [ ] Shared-session resume uses owner credentials (not participant's)
- [ ] Full end-to-end test covering the complete MVP journey including sharing

### Non-Goals
- Public or anonymous share links (explicitly excluded per guardrails)
- Organization/team sharing
- Participant-scoped credential injection (participants use owner's credentials)
- Role hierarchy beyond Owner/Participant
- Notification system for share invitations
- Sharing non-session resources (projects, workspaces, workspace roots remain owner-only)

### Definition of Done
- [ ] `dotnet build` succeeds with zero errors
- [ ] `dotnet test` passes — all existing and new tests
- [ ] Cloud mode: owner shares session with participant → participant can list/open it
- [ ] Cloud mode: non-participant cannot access shared session
- [ ] Cloud mode: participant can view session and send prompts
- [ ] Cloud mode: participant cannot stop, resume, share, or revoke
- [ ] Cloud mode: both owner and participant receive real-time events for shared session
- [ ] Cloud mode: owner revokes access → participant immediately loses access (including active realtime)
- [ ] Cloud mode: shared-session resume uses owner's credentials, not participant's
- [ ] Cloud mode: sharing does not expose owner credentials, settings, or unrelated resources
- [ ] End-to-end: sign in → onboard → store key → start session → share → collaborate → revoke

### Guardrails (Must NOT)
- Must NOT use public or anonymous share links
- Must NOT allow sharing to expose owner credentials, personal settings, or unrelated resources
- Must NOT allow participant to substitute their own credentials into the session runtime
- Must NOT break local mode
- Must NOT add organization/team features
- Must NOT allow partially authenticated access to shared sessions

## TODOs

### Phase 1: Sharing Model + Access Service

- [ ] 1. Create SessionParticipant entity
  **What**: Entity with fields: `SessionId` (string), `UserId` (string), `Role` (string — "participant" for MVP), `GrantedByUserId` (string), `CreatedAt` (string). Owner remains on `Session.UserId`; sharing is additive. Single non-owner role for MVP: `Participant`.
  **Files**: `src/WeaveFleet.Domain/Entities/SessionParticipant.cs`
  **Acceptance**: Session access model supports owner + explicit authenticated participants.

- [ ] 2. Create ISessionParticipantRepository and Dapper implementation
  **What**: Repository with: `AddParticipantAsync`, `RemoveParticipantAsync`, `GetParticipantsBySessionAsync`, `GetSessionsSharedWithUserAsync`, `IsParticipantAsync(sessionId, userId)`. Efficient queries.
  **Files**: `src/WeaveFleet.Domain/Repositories/ISessionParticipantRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionParticipantRepository.cs`
  **Acceptance**: Sharing records CRUD works. Access checks efficient.

- [ ] 3. Create session_participants migration
  **What**: `CREATE TABLE session_participants` with `session_id`, `user_id`, `role`, `granted_by_user_id`, `created_at`. Primary key on `(session_id, user_id)`. Indexes on `user_id` and `session_id`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/012_add_session_participants.sql`
  **Acceptance**: Migration runs cleanly on fresh and existing databases.

- [ ] 4. Create SessionAccessService
  **What**: Centralized authorization service. Methods: `CanViewSessionAsync(sessionId)`, `CanPromptAsync(sessionId)`, `CanManageAsync(sessionId)` (stop/resume/share/revoke). Logic: Owner has full access. Participant has view + prompt. Non-participant has nothing. Used by session detail, prompt, stop/resume, and share-management paths.
  **Files**: `src/WeaveFleet.Application/Services/SessionAccessService.cs`
  **Acceptance**: Authorization centralized. MVP permission matrix explicit — Owner: view/prompt/stop/resume/share/revoke; Participant: view/prompt only.

- [ ] 5. Register sharing services in DI
  **What**: Register `ISessionParticipantRepository`, `DapperSessionParticipantRepository`, `SessionAccessService`.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: All services resolvable. `dotnet build` succeeds.

### Phase 2: Sharing Endpoints + Access Integration

- [ ] 6. Create session sharing API endpoints
  **What**: `POST /api/sessions/{sessionId}/participants` — share with user (by email or userId). `GET /api/sessions/{sessionId}/participants` — list participants. `DELETE /api/sessions/{sessionId}/participants/{userId}` — revoke. `GET /api/sessions/shared` — list sessions shared with current user. Sharing requires both users to be authenticated Fleet users. No public links.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionShareEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: Owner can share/revoke. Participant can discover shared sessions. Non-owners cannot share or revoke.

- [ ] 7. Update session queries/endpoints for shared access
  **What**: Session list/detail/prompt flows allow owner-or-participant access via `SessionAccessService`. Non-session resources (projects, workspaces, workspace roots) remain owner-only. Shared participants may view and prompt; stop/resume/share/revoke are owner-only.
  **Files**: `src/WeaveFleet.Application/Services/SessionService.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, relevant session endpoint files in `src/WeaveFleet.Api/Endpoints/`
  **Acceptance**: Shared participant can open/interact with shared session. Cannot access unrelated owner resources.

- [ ] 8. Enforce owner credentials on shared-session resume
  **What**: In `SessionOrchestrator.ResumeSessionAsync`, when a participant resumes a shared session: resolve session owner first, verify caller access via `SessionAccessService`, then build environment using **owner's** provider credentials (not participant's). This seam was prepared in Track 2 TODO 16; this task enforces it for the sharing case explicitly.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Shared-session resume always uses owner credentials. Participant cannot substitute their own.

### Phase 3: Event Delivery + Realtime

- [ ] 9. Extend event broadcasting to authorized participants
  **What**: Evolve `InMemoryEventBroadcaster` from owner-only delivery (Track 1 TODO 26) to session-access-scoped delivery. Session events fan out to all authorized participants (owner + explicit participants). Use the seam prepared in Track 1. Non-session events remain user-scoped.
  **Files**: `src/WeaveFleet.Application/Services/IEventBroadcaster.cs`, `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`
  **Acceptance**: Shared session events reach both owner and participants. Non-participants receive nothing.

- [ ] 10. Update SSE activity stream for shared sessions
  **What**: Modify `SessionEventEndpoints` to deliver events for sessions the authenticated user is authorized to access (owner or participant), not just owned sessions. Uses `SessionAccessService`.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`
  **Acceptance**: SSE stream includes events for shared sessions. Non-participant sessions excluded.

- [ ] 11. Update WebSocket for shared session events
  **What**: WebSocket endpoint delivers session events to all authorized participants. On access revocation, immediately drop the participant's active WebSocket connection for that session.
  **Files**: `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`
  **Acceptance**: Participant receives live events. Revocation drops connection immediately.

- [ ] 12. Handle revocation of active connections
  **What**: When owner revokes participant access, immediately terminate participant's active SSE and WebSocket connections for that session. Ensure `RemoveParticipantAsync` triggers cleanup of active subscriptions/connections.
  **Files**: `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`, `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`
  **Acceptance**: Revoke access → participant's realtime connections for that session drop within seconds.

### Phase 4: Tests + E2E Verification

- [ ] 13. Write session sharing unit/integration tests
  **What**: Tests: owner shares → participant can list/open. Non-participant cannot. Participant can prompt but cannot stop/resume/share/revoke. Owner can revoke and participant immediately loses access. Owner credentials used on shared-session resume, not participant's.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/SessionAccessTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/SessionShareEndpointTests.cs`
  **Acceptance**: All sharing scenarios covered. Tests pass.

- [ ] 14. Write event delivery tests for shared sessions
  **What**: Tests: participant receives live events for shared session. Non-participant does not. Revocation drops realtime access. Owner-only events remain scoped.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/EventBroadcasterSharingTests.cs`
  **Acceptance**: Event fan-out to participants verified. Revocation cleanup verified.

- [ ] 15. Write full end-to-end hosted session + sharing test
  **What**: E2E test: owner signs in → onboarding → store API key → create session (verify managed workspace) → share with second user → second user opens shared session → both observe event stream → participant prompts → participant cannot stop/resume/share/revoke → revoke access → participant loses access and realtime dropped. Include: cloud-unsafe directory input rejection, absence of bearer tokens in URLs.
  **Files**: `tests/WeaveFleet.E2E/HostedSessionFlowTests.cs`
  **Acceptance**: Full MVP journey completes. All authorization rules hold.

## Dependencies

### This track depends on

| Dependency | From | Specific items needed |
|------------|------|-----------------------|
| `IUserContext` + auth pipeline | Track 1, Phase 1 | TODOs 1-4, 9, 10 |
| `UserId`-scoped repositories | Track 1, Phase 2 | TODOs 20-23 |
| Event-scoping seams (owner-only + participant fan-out prep) | Track 1, Phase 2 | TODOs 26, 28 |
| Ownership guards on delegation paths | Track 1, Phase 2 | TODO 27 |
| `ProviderCredentialService` + credential injection | Track 2, Phase 2 | TODOs 14, 16 |
| Live infrastructure | Track 2, Phase 1 | TODOs 1-8 |
| Onboarding flow | Track 2, Phase 3 | TODOs 19-23 |

### What can start early
- Phase 1 (TODOs 1-5): Can begin as soon as Track 1 Phase 1 is complete (needs `IUserContext` and entity patterns established)
- Phase 2 (TODOs 6-8): Requires Track 1 Phase 2 complete (needs `UserId`-scoped repos and `SessionOrchestrator` changes landed)
- Phase 3 (TODOs 9-12): Requires Track 1 Phase 2 event seams and Phase 2 of this track
- Phase 4 (TODOs 13-15): Requires all phases of all three tracks

### Tracks that depend on this
- None — this is the final track in the cloud MVP

## Verification

- [ ] `dotnet build` succeeds with zero errors
- [ ] `dotnet test` — all existing and new tests pass
- [ ] Cloud mode: owner shares session → participant can list/open
- [ ] Cloud mode: non-participant cannot access shared session
- [ ] Cloud mode: participant can view + prompt; cannot stop/resume/share/revoke
- [ ] Cloud mode: both owner and participant receive real-time events
- [ ] Cloud mode: owner revokes → participant loses access immediately (including realtime)
- [ ] Cloud mode: shared-session resume uses owner credentials
- [ ] Cloud mode: sharing doesn't expose owner credentials or unrelated resources
- [ ] End-to-end MVP journey: sign in → onboard → store key → session → share → collaborate → revoke
- [ ] Local mode: unchanged behavior

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Event broadcaster complexity for participant fan-out | Medium | Medium | Track 1 prepares the seam; this track only extends it. Keep `InMemoryEventBroadcaster` simple — no distributed concerns for MVP. |
| Revocation of active connections is tricky | Medium | Medium | Track active WebSocket/SSE connections by session+user. On revoke, iterate and close. Simple for single-node. |
| Credential leakage through shared session context | Low | High | `SessionAccessService` enforces boundary. Shared participant never receives credential data. Resume always resolves owner credentials server-side. |
| Race condition: share + revoke + event delivery | Low | Medium | Single-node SQLite serializes writes. Access checks happen at event delivery time, not subscription time. |
