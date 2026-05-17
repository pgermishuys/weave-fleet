# Weave-Riff: Collaborative Session Sharing Platform

## TL;DR
> **Summary**: A .NET 10 platform with an ASP.NET Core signaling server, Blazor WASM test client, and Aspire AppHost that enables peer-to-peer sharing of Weave Agent Fleet sessions via WebRTC DataChannels brokered by WebSocket signaling.
> **Estimated Effort**: XL

## Context
### Original Request
Build "weave-riff" — a new .NET 10 repository enabling users to share live AI agent sessions with others via WebRTC peer-to-peer connections. The signaling server brokers WebRTC handshakes but never sees session content. A Blazor WASM test client exercises the full protocol as both host and viewer.

### Key Findings
- The parent project (Weave Agent Fleet) is Next.js/TypeScript — this is a standalone .NET companion service
- C# standards require: `sealed` classes, PascalCase test names, no optional parameters, fix all warnings
- Testing strategy favors integration tests over unit tests; build verification in Release mode
- Star topology: host maintains one RTCPeerConnection per viewer
- WebRTC DataChannels provide E2E encryption (DTLS) — signaling server is zero-knowledge for session content

## Objectives
### Core Objective
Deliver a working signaling server + test client that demonstrates full WebRTC-based session sharing with OIDC auth, API key auth, and a user dashboard.

### Deliverables
- [ ] ASP.NET Core signaling server with OIDC + API key auth, WebSocket signaling, Razor Pages dashboard
- [ ] Blazor WASM test client (host + viewer modes) exercising full WebRTC DataChannel protocol
- [ ] Aspire AppHost for local orchestration
- [ ] Shared library for protocol types and models
- [ ] Integration tests for server endpoints and signaling flow

### Definition of Done
- [ ] `dotnet build -c Release` succeeds with zero warnings across solution
- [ ] `dotnet test` passes all tests
- [ ] AppHost launches signaling server + test client; two browser tabs can share a session end-to-end
- [ ] OIDC login works; API key auth works for API endpoints; admin panel restricted to admin role

### Guardrails (Must NOT)
- Signaling server must NOT relay or inspect session content — signaling only
- Must NOT use optional/default parameters in C#
- Must NOT leave unsealed concrete classes
- Must NOT store API keys in plaintext — hash with SHA-256 minimum
- Must NOT skip CORS configuration for the Blazor WASM client

---

## TODOs

### Phase 1: Solution Scaffold & Shared Types

- [ ] 1. Create solution structure
  **What**: Initialize `WeaveRiff.sln` with projects: `WeaveRiff.Server` (ASP.NET Core web), `WeaveRiff.Client` (Blazor WASM), `WeaveRiff.Shared` (class library), `WeaveRiff.AppHost` (Aspire), `WeaveRiff.Server.Tests` (xUnit), `WeaveRiff.Integration.Tests` (xUnit). Set `<TargetFramework>net10.0</TargetFramework>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` in `Directory.Build.props`. Add `.gitignore`, `global.json` (SDK 10.0).
  **Files**: `WeaveRiff.sln`, `Directory.Build.props`, `global.json`, `.gitignore`, `src/WeaveRiff.Server/WeaveRiff.Server.csproj`, `src/WeaveRiff.Client/WeaveRiff.Client.csproj`, `src/WeaveRiff.Shared/WeaveRiff.Shared.csproj`, `src/WeaveRiff.AppHost/WeaveRiff.AppHost.csproj`, `tests/WeaveRiff.Server.Tests/WeaveRiff.Server.Tests.csproj`, `tests/WeaveRiff.Integration.Tests/WeaveRiff.Integration.Tests.csproj`
  **Acceptance**: `dotnet build -c Release` succeeds across all projects

- [ ] 2. Define shared protocol types
  **What**: In `WeaveRiff.Shared`, define the WebRTC signaling and DataChannel protocol as sealed record types. These are the contracts used by both server and client.
  **Files**: `src/WeaveRiff.Shared/Protocol/SignalingMessages.cs`, `src/WeaveRiff.Shared/Protocol/DataChannelMessages.cs`, `src/WeaveRiff.Shared/Protocol/SessionStatus.cs`
  **Acceptance**: Types compile; used by both Server and Client projects

  Signaling messages (WebSocket):
  ```
  // Client → Server
  sealed record RegisterShare(string SessionTitle);
  sealed record JoinSession(string ShareToken);
  sealed record SdpOffer(string TargetPeerId, string Sdp);
  sealed record SdpAnswer(string TargetPeerId, string Sdp);
  sealed record IceCandidate(string TargetPeerId, string Candidate, string SdpMid, int SdpMLineIndex);

  // Server → Client
  sealed record ShareRegistered(string ShareToken, string JoinUrl);
  sealed record ViewerJoined(string PeerId, string DisplayName);
  sealed record ViewerLeft(string PeerId);
  sealed record SdpOfferForward(string FromPeerId, string Sdp);
  sealed record SdpAnswerForward(string FromPeerId, string Sdp);
  sealed record IceCandidateForward(string FromPeerId, string Candidate, string SdpMid, int SdpMLineIndex);
  sealed record SignalingError(string Message);
  ```

  DataChannel messages (peer-to-peer, for Shared reference):
  ```
  sealed record SessionMeta(string Title, string Status, int Tokens, decimal Cost);
  sealed record SessionEvent(string EventType, JsonElement Data);
  sealed record ViewerList(IReadOnlyList<ViewerInfo> Viewers);
  sealed record SuggestionResponse(string Id, string Action, string? Reason); // Action: "accepted"|"rejected"
  sealed record CatchUp(IReadOnlyList<SessionEvent> Messages, SessionMeta Meta);
  sealed record Suggest(string Id, string PromptText);
  sealed record Identity(string DisplayName);
  ```

- [ ] 3. Define shared domain models
  **What**: EF Core entity types matching the data model: `User`, `ApiKey`, `SharedSession`, `SessionParticipant`. All sealed classes. Include enums `UserRole`, `SessionStatus`, `ParticipantRole`.
  **Files**: `src/WeaveRiff.Shared/Models/User.cs`, `src/WeaveRiff.Shared/Models/ApiKey.cs`, `src/WeaveRiff.Shared/Models/SharedSession.cs`, `src/WeaveRiff.Shared/Models/SessionParticipant.cs`, `src/WeaveRiff.Shared/Models/Enums.cs`
  **Acceptance**: Models compile; properties match specified data model exactly

### Phase 2: Database & Auth Foundation

- [ ] 4. Set up EF Core with SQLite
  **What**: Create `WeaveRiffDbContext` in the Server project. Configure entity relationships, indexes (unique on `ApiKey.KeyHash`, `SharedSession.ShareToken`, `User.OidcSubject`). Add initial migration. NuGet: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`.
  **Files**: `src/WeaveRiff.Server/Data/WeaveRiffDbContext.cs`, `src/WeaveRiff.Server/Data/Migrations/` (generated)
  **Acceptance**: `dotnet ef migrations add Initial` succeeds; database created on startup with correct schema

- [ ] 5. Implement OIDC authentication
  **What**: Configure `AddAuthentication().AddOpenIdConnect()` with settings from `appsettings.json` / env vars. Support configurable provider (Authority, ClientId, ClientSecret, Scopes). On first login, upsert `User` record from OIDC claims (subject, name, email). Add `AddAuthorization()` with "Admin" policy checking `UserRole.Admin`. NuGet: `Microsoft.AspNetCore.Authentication.OpenIdConnect`.
  **Files**: `src/WeaveRiff.Server/Auth/OidcSetup.cs`, `src/WeaveRiff.Server/Auth/UserSyncMiddleware.cs`, `src/WeaveRiff.Server/appsettings.json`
  **Acceptance**: OIDC login redirects to provider; user record created in DB on first login; admin policy enforced

  Config shape:
  ```json
  {
    "Oidc": {
      "Authority": "https://...",
      "ClientId": "...",
      "ClientSecret": "...",
      "Scopes": ["openid", "profile", "email"]
    }
  }
  ```

- [ ] 6. Implement API key authentication
  **What**: Custom `AuthenticationHandler<ApiKeyAuthOptions>` that reads `X-Api-Key` header, hashes it with SHA-256, looks up in DB. If found and not revoked, sets `ClaimsPrincipal` with user's claims. Register as scheme `"ApiKey"`. Add endpoints for API key management (create, list, revoke) behind OIDC auth.
  **Files**: `src/WeaveRiff.Server/Auth/ApiKeyAuthHandler.cs`, `src/WeaveRiff.Server/Auth/ApiKeyAuthOptions.cs`, `src/WeaveRiff.Server/Endpoints/ApiKeyEndpoints.cs`
  **Acceptance**: `POST /api/keys` creates key (returns plaintext once); API requests with valid `X-Api-Key` authenticate as the owning user; revoked keys rejected

- [ ] 7. Write auth integration tests
  **What**: Using `WebApplicationFactory<Program>`, test: unauthenticated requests get 401/redirect; API key auth works; admin policy blocks non-admins. Use an in-memory SQLite DB. Mock OIDC with test cookie auth scheme for tests.
  **Files**: `tests/WeaveRiff.Server.Tests/Auth/ApiKeyAuthTests.cs`, `tests/WeaveRiff.Server.Tests/TestFixtures/TestWebAppFactory.cs`
  **Acceptance**: All auth tests pass with `dotnet test`

### Phase 3: Session Management API

- [ ] 8. Implement share management endpoints
  **What**: Minimal API endpoints:
  - `POST /api/sessions/share` — API key auth. Creates `SharedSession` with crypto-random `ShareToken` (URL-safe base64, 32 bytes). Returns `{ shareToken, joinUrl: "/join/{token}", weaveRiffUri: "weave-riff://join/{token}" }`.
  - `DELETE /api/sessions/share/{id}` — API key auth. Sets status to `Ended`, records `EndedAt`. Only the host can end their own share.
  - `GET /api/sessions/{token}` — OIDC auth. Returns session info (title, host display name, status, participant count).
  **Files**: `src/WeaveRiff.Server/Endpoints/SessionEndpoints.cs`, `src/WeaveRiff.Server/Services/ShareTokenGenerator.cs`
  **Acceptance**: Create/end/query flows work; tokens are cryptographically random; only host can end own session

- [ ] 9. Implement join page
  **What**: `GET /join/{token}` — Razor Page. If user is authenticated, shows session info + "Join" button. Includes meta tag for `weave-riff://join/{token}` deep link (future fleet app integration). If session is ended, shows "Session ended" message. Unauthenticated users redirected to OIDC login with return URL.
  **Files**: `src/WeaveRiff.Server/Pages/Join.cshtml`, `src/WeaveRiff.Server/Pages/Join.cshtml.cs`
  **Acceptance**: Join page renders with session details; deep link URI present; ended sessions show appropriate message

- [ ] 10. Write session API integration tests
  **What**: Test full CRUD lifecycle: create share → query by token → end share → query shows ended. Test auth: non-host cannot delete. Test invalid tokens return 404.
  **Files**: `tests/WeaveRiff.Server.Tests/Endpoints/SessionEndpointTests.cs`
  **Acceptance**: All session tests pass

### Phase 4: WebSocket Signaling

- [ ] 11. Implement WebSocket signaling hub
  **What**: `WS /ws/signal` endpoint using raw WebSockets (not SignalR — keep it lightweight and protocol-explicit). Accept both OIDC cookie and API key auth. On connect, authenticate and associate connection with user. Message routing:
  - Host sends `RegisterShare` → server tracks connection as host for that share token
  - Viewer sends `JoinSession` → server notifies host with `ViewerJoined`, creates `SessionParticipant` record
  - SDP and ICE messages forwarded between specific peers by `TargetPeerId`
  - On disconnect, clean up: notify peers, update `SessionParticipant.LeftAt`
  
  Use `System.Text.Json` for serialization with a discriminated `type` field. Keep a `ConcurrentDictionary<string, WebSocketConnection>` for active connections.
  **Files**: `src/WeaveRiff.Server/Signaling/SignalingWebSocketHandler.cs`, `src/WeaveRiff.Server/Signaling/ConnectionManager.cs`, `src/WeaveRiff.Server/Signaling/SignalingMessageRouter.cs`
  **Acceptance**: Two WebSocket clients can exchange SDP/ICE messages through the server; connection cleanup works on disconnect

- [ ] 12. Add STUN/TURN configuration endpoint
  **What**: `GET /api/ice-servers` — returns ICE server configuration (STUN/TURN URLs, credentials if TURN). Configuration from `appsettings.json`. Default: Google's public STUN server. TURN is optional, configured via settings.
  **Files**: `src/WeaveRiff.Server/Endpoints/IceServerEndpoints.cs`
  **Acceptance**: Returns configured ICE servers; clients can use them for WebRTC setup

  Config shape:
  ```json
  {
    "IceServers": [
      { "Urls": ["stun:stun.l.google.com:19302"] },
      { "Urls": ["turn:your-turn-server:3478"], "Username": "...", "Credential": "..." }
    ]
  }
  ```

- [ ] 13. Write signaling integration tests
  **What**: Using `WebApplicationFactory`, open two WebSocket connections (host + viewer). Test full handshake: host registers share → viewer joins → SDP exchange → ICE exchange. Test error cases: join invalid token, duplicate registration. Test disconnect cleanup.
  **Files**: `tests/WeaveRiff.Server.Tests/Signaling/SignalingTests.cs`
  **Acceptance**: Full signaling round-trip works in tests; error cases handled gracefully

### Phase 5: Dashboard & Admin UI

- [ ] 14. Implement user dashboard
  **What**: `GET /` — Razor Page (OIDC auth required). Shows:
  - Active shares you're hosting (title, share link, viewer count, created time)
  - Sessions you've joined as viewer (title, host name, status)
  - History of past sessions (last 30 days)
  
  Use Tailwind CSS via CDN for styling (simple, no build step). Include copy-to-clipboard for share links.
  **Files**: `src/WeaveRiff.Server/Pages/Index.cshtml`, `src/WeaveRiff.Server/Pages/Index.cshtml.cs`, `src/WeaveRiff.Server/Pages/Shared/_Layout.cshtml`
  **Acceptance**: Dashboard shows accurate data; share links are copyable; responsive layout

- [ ] 15. Implement admin panel
  **What**: `GET /admin` — Razor Page (OIDC + admin role). Shows:
  - User list with role management (promote/demote admin)
  - Active sessions overview (all users) with ability to force-end
  - System stats (total users, active sessions, total sessions)
  - API key management (view/revoke any user's keys)
  
  POST endpoints for admin actions: `POST /admin/users/{id}/role`, `POST /admin/sessions/{id}/end`.
  **Files**: `src/WeaveRiff.Server/Pages/Admin/Index.cshtml`, `src/WeaveRiff.Server/Pages/Admin/Index.cshtml.cs`, `src/WeaveRiff.Server/Pages/Admin/Users.cshtml`, `src/WeaveRiff.Server/Pages/Admin/Users.cshtml.cs`, `src/WeaveRiff.Server/Pages/Admin/Sessions.cshtml`, `src/WeaveRiff.Server/Pages/Admin/Sessions.cshtml.cs`
  **Acceptance**: Admin panel only accessible to admin role; all management actions work; non-admins get 403

### Phase 6: Blazor WASM Test Client

- [ ] 16. Set up Blazor WASM client project
  **What**: Configure `WeaveRiff.Client` as Blazor WASM standalone app. Add reference to `WeaveRiff.Shared`. NuGet: `Microsoft.AspNetCore.Components.WebAssembly` (implicit with template). Configure OIDC auth in the client (same provider as server). Add `HttpClient` configured with server base URL. Add JS interop file for WebRTC APIs (RTCPeerConnection, RTCDataChannel).
  **Files**: `src/WeaveRiff.Client/Program.cs`, `src/WeaveRiff.Client/wwwroot/index.html`, `src/WeaveRiff.Client/wwwroot/js/webrtc-interop.js`, `src/WeaveRiff.Client/Auth/OidcConfig.cs`, `src/WeaveRiff.Client/Services/WebRtcService.cs`
  **Acceptance**: Client loads in browser; OIDC login works; JS interop callable from C#

- [ ] 17. Implement WebRTC JS interop layer
  **What**: JavaScript module exposing WebRTC operations to Blazor via `IJSRuntime`:
  - `createPeerConnection(iceServers)` → returns connection ID
  - `createOffer(connId)` → returns SDP
  - `setRemoteDescription(connId, sdp, type)`
  - `createAnswer(connId)` → returns SDP
  - `addIceCandidate(connId, candidate, sdpMid, sdpMLineIndex)`
  - `createDataChannel(connId, label)` → returns channel ID
  - `sendMessage(channelId, json)`
  - Event callbacks via `DotNetObjectReference`: `onIceCandidate`, `onDataChannelOpen`, `onDataChannelMessage`, `onConnectionStateChange`
  
  C# wrapper `WebRtcService` (sealed) manages connection lifecycle and maps to/from protocol types.
  **Files**: `src/WeaveRiff.Client/wwwroot/js/webrtc-interop.js`, `src/WeaveRiff.Client/Services/WebRtcService.cs`, `src/WeaveRiff.Client/Services/IWebRtcService.cs`
  **Acceptance**: Can create RTCPeerConnection and DataChannel from Blazor; events fire back to C#

- [ ] 18. Implement WebSocket signaling client
  **What**: Sealed `SignalingClient` class using `ClientWebSocket`. Connects to `WS /ws/signal`. Sends/receives typed signaling messages. Exposes events: `OnShareRegistered`, `OnViewerJoined`, `OnViewerLeft`, `OnSdpOffer`, `OnSdpAnswer`, `OnIceCandidate`, `OnError`. Auto-reconnect with exponential backoff.
  **Files**: `src/WeaveRiff.Client/Services/SignalingClient.cs`
  **Acceptance**: Client connects to signaling server; messages serialize/deserialize correctly; reconnect works

- [ ] 19. Implement host mode UI
  **What**: Razor component `HostPage.razor`. Flow:
  1. Click "Start Sharing" → calls `POST /api/sessions/share` → gets share token
  2. Connects to signaling WS → sends `RegisterShare`
  3. Displays share link (copyable) and `weave-riff://` URI
  4. Generates fake session events on a timer (simulated agent output: thinking, tool calls, file edits, token counts)
  5. When viewer joins: creates RTCPeerConnection, completes SDP/ICE exchange, opens DataChannel
  6. Sends `CatchUp` on DataChannel open, then streams events
  7. Shows viewer list with display names
  8. Receives `Suggest` messages, shows in a queue with Accept/Reject buttons
  9. "Stop Sharing" button ends session

  Fake event generator: configurable interval (default 2s), cycles through event types (text output, tool invocation, file change, cost update).
  **Files**: `src/WeaveRiff.Client/Pages/Host.razor`, `src/WeaveRiff.Client/Pages/Host.razor.cs`, `src/WeaveRiff.Client/Services/FakeEventGenerator.cs`
  **Acceptance**: Host can start sharing, get link, see viewers connect, receive and respond to suggestions

- [ ] 20. Implement viewer mode UI
  **What**: Razor component `ViewerPage.razor`. Flow:
  1. Enter share token or navigate to `/viewer/{token}`
  2. Calls `GET /api/sessions/{token}` for session info
  3. Connects to signaling WS → sends `JoinSession`
  4. Completes WebRTC handshake (receives SDP offer, sends answer, exchanges ICE)
  5. DataChannel opens → receives `CatchUp` → renders message history
  6. Streams live events, renders in scrollable log
  7. Shows session meta (title, tokens, cost) in header
  8. Text input to submit prompt suggestions → sends `Suggest` on DataChannel
  9. Shows suggestion responses (accepted/rejected with optional reason)

  Event renderer: display different event types with appropriate formatting (code blocks for file changes, inline for text output, badges for tool calls).
  **Files**: `src/WeaveRiff.Client/Pages/Viewer.razor`, `src/WeaveRiff.Client/Pages/Viewer.razor.cs`, `src/WeaveRiff.Client/Components/EventRenderer.razor`
  **Acceptance**: Viewer can join via token, see live events, submit suggestions, see responses

- [ ] 21. Add multi-user testing support
  **What**: Add a "Dev Tools" panel to the test client that allows:
  - Quick-switch between simulated users (dropdown with 3-4 test identities)
  - Open host and viewer in same browser (different auth contexts via query param override in dev mode)
  - Connection status indicator (signaling WS state + WebRTC connection state per peer)
  - DataChannel message log (collapsible, shows raw JSON for debugging)
  
  Only visible when `ASPNETCORE_ENVIRONMENT=Development`.
  **Files**: `src/WeaveRiff.Client/Components/DevTools.razor`, `src/WeaveRiff.Client/Components/ConnectionStatus.razor`
  **Acceptance**: Can test full host→viewer flow in multiple tabs; connection states visible; message log shows protocol traffic

### Phase 7: Aspire AppHost & Integration

- [ ] 22. Configure Aspire AppHost
  **What**: Set up `WeaveRiff.AppHost` to orchestrate:
  - Signaling server (with OIDC config)
  - Blazor WASM client (served from server or as separate project)
  - Optional: Keycloak container for dev OIDC (via `Aspire.Hosting.Keycloak` or docker compose resource)
  
  NuGet: `Aspire.Hosting`, `Aspire.Hosting.AppHost`. Wire up service discovery so client knows server URL. Configure CORS on server for client origin.

  If Keycloak: pre-configure realm "weave-riff" with two clients (server, client), two test users (user + admin).
  **Files**: `src/WeaveRiff.AppHost/Program.cs`, `src/WeaveRiff.AppHost/appsettings.json`, `src/WeaveRiff.AppHost/keycloak/realm-export.json` (if Keycloak)
  **Acceptance**: `dotnet run --project src/WeaveRiff.AppHost` starts all services; Aspire dashboard shows healthy resources; OIDC login works end-to-end

- [ ] 23. Configure CORS and security headers
  **What**: On the signaling server:
  - CORS: Allow client origin (from Aspire service discovery or config), allow credentials, allow WebSocket upgrade
  - Security headers middleware: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`
  - WebSocket auth: validate token/cookie before upgrading connection
  - Rate limiting on API endpoints (use `Microsoft.AspNetCore.RateLimiting`)
  **Files**: `src/WeaveRiff.Server/Middleware/SecurityHeadersMiddleware.cs`, `src/WeaveRiff.Server/Program.cs`
  **Acceptance**: CORS works for client; security headers present on responses; rate limiting enforced

### Phase 8: End-to-End Tests & Polish

- [ ] 24. Write end-to-end integration tests
  **What**: Tests using `WebApplicationFactory` that exercise the full flow:
  1. Create user, create API key
  2. Register share via API
  3. Open two WebSocket connections (host + viewer)
  4. Complete signaling handshake
  5. Verify session participant records in DB
  6. End session, verify cleanup
  
  Also test: concurrent viewers (3+), rapid connect/disconnect, invalid message handling.
  **Files**: `tests/WeaveRiff.Integration.Tests/FullFlowTests.cs`, `tests/WeaveRiff.Integration.Tests/ConcurrencyTests.cs`, `tests/WeaveRiff.Integration.Tests/TestFixtures/IntegrationTestBase.cs`
  **Acceptance**: All integration tests pass; no flaky tests from race conditions

- [ ] 25. Add logging and observability
  **What**: Structured logging with `ILogger<T>` throughout:
  - Log signaling events (connect, disconnect, message routing) at Information level
  - Log auth events (login, API key usage) at Information level
  - Log errors (WebSocket failures, auth failures) at Warning/Error level
  - Add health check endpoint `GET /health` (DB connectivity check)
  - Add OpenTelemetry traces for signaling message routing (if Aspire provides collector)
  **Files**: `src/WeaveRiff.Server/Program.cs` (logging config), `src/WeaveRiff.Server/HealthChecks/DbHealthCheck.cs`
  **Acceptance**: Structured logs visible in console and Aspire dashboard; health endpoint returns healthy

---

## Verification

- [ ] `dotnet build -c Release` succeeds with zero warnings for entire solution
- [ ] `dotnet test` passes all tests (server unit + integration tests)
- [ ] AppHost starts cleanly with `dotnet run --project src/WeaveRiff.AppHost`
- [ ] Manual test: open two browser tabs, host shares session, viewer joins, events stream, suggestions flow works
- [ ] Security: unauthenticated requests rejected; admin panel restricted; API keys hashed in DB; CORS configured
- [ ] No session content passes through signaling server (verify via message logs)

## Key NuGet Packages

| Package | Project | Purpose |
|---------|---------|---------|
| `Microsoft.EntityFrameworkCore.Sqlite` | Server | Database |
| `Microsoft.EntityFrameworkCore.Design` | Server | Migrations |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | Server | OIDC auth |
| `Microsoft.AspNetCore.RateLimiting` | Server | Rate limiting |
| `Aspire.Hosting` | AppHost | Orchestration |
| `Aspire.Hosting.Keycloak` | AppHost | Dev OIDC provider |
| `Microsoft.AspNetCore.Components.WebAssembly.Authentication` | Client | OIDC in WASM |

## Security Checklist

- [ ] API keys hashed with SHA-256 before storage; plaintext returned only on creation
- [ ] OIDC: validate issuer, audience, signature; use PKCE for public clients (Blazor WASM)
- [ ] WebSocket upgrade requires valid auth (cookie or API key)
- [ ] Share tokens: 32 bytes crypto-random, URL-safe base64
- [ ] CORS: explicit origin allowlist, not wildcard
- [ ] Rate limiting on share creation and WebSocket connections
- [ ] Admin actions require admin role claim
- [ ] No session content logged or stored by signaling server
