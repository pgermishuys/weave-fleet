# Fleet on your phone (PWA)

## TL;DR
Make Fleet usable from a phone as an installable PWA served by each machine: QR pairing that gives each phone its own revocable token, Web Push notifications (needs you / question / finished / failed) for every machine via the phone's "home machine", a cross-machine "Needs you" inbox, and a phone session view that can read, answer and steer. Built so a native app later reuses the same contract.

## Context

### What Fleet is
- .NET backend in `src/`: `WeaveFleet.Api` (endpoints, auth, SignalR hub, Kestrel static files), `WeaveFleet.Application` (services, `SessionNotifier`), `WeaveFleet.Domain` (entities, events, repository interfaces), `WeaveFleet.Infrastructure` (Dapper repositories, DbUp SQLite migrations, hosted services, DI in `src/WeaveFleet.Infrastructure/DependencyInjection.cs`).
- Vue 3 client in `client/` (Vite, TanStack Vue Router file routes in `client/src/routes/`, Pinia stores in `client/src/stores/`, pure logic in `client/src/lib/`, composables in `client/src/composables/`).
- **Toolchain: always `bun` / `bunx`. Never `npm` / `npx`.** Read `AGENTS.md` (repo root) for layout vocabulary and the three test layers before starting.
- NuGet versions are central: `Directory.Packages.props` (already has `Microsoft.AspNetCore.SignalR.Client` 10.0.4).

### Machines and auth today (read `docs/machines.md` in full)
- Every machine runs its own Fleet, bound to loopback by default. HTTPS for other devices comes from `tailscale serve` + `--require-token`.
- One shared access token per machine, persisted in `<db>.machine.json` by `src/WeaveFleet.Application/Configuration/MachineIdentityStore.cs`; validated by `src/WeaveFleet.Application/Services/LocalTokenAuthService.cs` (`ILocalTokenAuthService.ValidateToken`, constant-time).
- `src/WeaveFleet.Api/Auth/BearerTokenHandler.cs`: `Authorization: Bearer` everywhere; `?access_token=` only on `/hubs/*` and WebSocket upgrades. Emits claim `amr` = `token` | `loopback` | `agent`. A wrong token never falls back.
- `src/WeaveFleet.Api/Auth/LoopbackAuthPolicy.cs`: loopback auto-auth only when bound to loopback and not `--require-token` and no proxy headers.
- `src/WeaveFleet.Api/Auth/MachineCorsPolicyProvider.cs`: a request presenting a token (or a preflight asking for `Authorization`) gets permissive CORS **without credentials**; everything else gets the configured policy. **These CORS rules must still hold after this plan.**
- Cookie sign-in: `POST /auth/token-login` in `src/WeaveFleet.Api/Endpoints/AuthEndpoints.cs` (persistent cookie, sliding). Cookie/bearer wiring and the `FleetUser` policy are in `src/WeaveFleet.Api/Program.cs` (local-mode branch around lines 300–395). Cookie `ForwardDefaultSelector` forwards to the bearer scheme when a token is presented.
- `src/WeaveFleet.Api/Endpoints/MachineEndpoints.cs`: `GET/PUT /api/machine`, `GET /api/machine/access`, `POST /api/machine/access/token` (local mode only). Tests: `tests/WeaveFleet.Api.Tests/Auth/MachineAccessEndpointTests.cs`, `LocalTokenAuthServiceTests.cs`, `LoopbackAuthPolicyEndpointTests.cs`, `tests/WeaveFleet.Application.Tests/Configuration/MachineIdentityStoreTests.cs`.
- Static files / SPA in `Program.cs` (~line 660): `UseDefaultFiles`, `UseStaticFiles` (`/assets/*` immutable; everything else `no-cache`), `MapFallbackToFile("index.html").AllowAnonymous()`. Client build output `client/dist` is copied into `src/WeaveFleet.Api/wwwroot` by `WeaveFleet.Api.csproj`. There is **no** `client/public/` folder yet.
- Client machine list lives only in the browser: `client/src/lib/machines.ts` (`MachineConnection`, localStorage keys `weave:machines`, `weave:active-machine`, `weave:session-machines`, `switchToMachine` reloads the page) and `client/src/stores/machines.ts` (polls other machines every 15 s, `identifyMachine`). Settings UI: `client/src/components/settings/MachinesSection.vue` ("This machine" access block, "Add a machine"). Tests: `client/src/lib/__tests__/machines.test.ts`, `client/src/stores/__tests__/machines.test.ts`.

### Notifications today
- `src/WeaveFleet.Application/Sessions/SessionNotifier.cs` turns activity changes (fed from `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`) and workflow waits into `session_notification` on the global `sessions` topic. Reasons in `src/WeaveFleet.Domain/Events/SessionEvents.cs` (`SessionNotificationReasons.NeedsYou` / `Finished`, `SessionNotificationPayload`). It skips sessions watched in a tab (`SessionFocusTracker`, fed by hub method `SetSessionFocusAsync` in `src/WeaveFleet.Api/Hubs/`), hidden/archived/child sessions, and **only broadcasts when the user's `DesktopNotifications` preference is on** (`INotificationPreference`, `src/WeaveFleet.Infrastructure/Services/RecapPreference.cs`).
- Client: `client/src/composables/use-session-notifications.ts` shows `new Notification(...)` (fails on Android without a service worker); toggle lives in `client/src/components/settings/FeaturesSection.vue`.
- Permission asks: harness event `permission.asked` / `permission.replied`; `IPendingPermissions` (`src/WeaveFleet.Application/Sessions/IPendingPermissions.cs`, `WaitingIn(sessionId)`); endpoints `GET /api/sessions/{id}/permissions`, `POST /api/sessions/{id}/permissions/{requestId}`; questions `POST /api/sessions/{id}/questions/{requestId}/answer|reject` (all in `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`). Client: `client/src/composables/use-session-permissions.ts`, `client/src/components/session/PermissionCard.vue`, `QuestionCard.vue`.
- Dashboard "Needs you": `client/src/components/dashboard/FleetDashboard.vue` (`waiting_input`, error, workflow waiting).

### Composer and session UI today
- `client/src/components/session/Composer.vue` (~1500 lines): send, queue (`QueuedMessages.vue`, `/api/sessions/{id}/queue`), "Send now" steering (`canSteer`, `steersByDefault` only for `opencode2`), interrupt (`/abort`), `!` shell (`/shell`), `/btw` side conversation (`/side`), agent/model/effort selectors, image attachments.
- Mobile layout bits: `client/src/composables/use-media-query.ts` (`useIsMobileNav` ≤716px), `use-sidebar-mobile.ts`, `client/src/components/layout/AppShell.vue`. Root layout `client/src/routes/__root.tsx` renders `AppShell` + `AuthGate` for everything except `/login`.
- Live data: `client/src/composables/use-signalr-socket.ts` (one connection, to the active machine), `use-session-stream.ts`, `client/src/lib/domain-event-reducer.ts`.

### Mockups (authoritative for UI — open and follow them)
- `mockups/phone-app/index.html`: pairing flow (desktop "Add a phone" QR + "Devices with access"; phone "Connect this phone to hangar?" with device name; notifications onboarding with Home Screen step and per-kind toggles + "Quiet while I'm at the desk"), option **A** (Needs-you inbox) and option **C** (notifications, Android action buttons, single-question page, "Answered" page). Option B is not being built.
- `mockups/phone-app/session.html`: clickable phone session prototype, the six rules, awkward cases (unreachable machine, "since you looked", terminal redirect) and the desktop→phone mapping table. Copy its structure, spacing (44px rows), copy text and states. Use Fleet's existing CSS tokens (`client/src/assets/main.css`).

## Scope
- In scope:
  - Per-device tokens: QR pairing (plus typeable fallback code), hashed storage, constant-time compare, 30-day sliding expiry, revocation, scope limits, Settings UI.
  - Installable PWA per machine origin: manifest, icons, hand-written service worker, registration.
  - Web Push (VAPID, aes128gcm, NuGet `WebPush`) for the local machine's sessions, per-kind preferences, quiet-while-at-desk presence, subscription cleanup and churn handling.
  - Server-side machine list on each machine (migrated from localStorage), home-machine fan-out of other machines' notifications, per-machine device grants for the phone.
  - Phone UI: `/pair`, `/phone/setup`, `/phone` inbox (option A), `/phone/s/$machineId/$sessionId` session view per `session.html`, Android notification actions, deep links to the ask, awkward cases.
  - Docs (`docs/phone.md`, `docs/machines.md`) and tests at all three layers, plus a security audit.
- Out of scope:
  - Relay/tunnel for phones without Tailscale.
  - A hosted app at app.tryweave.io (or any tryweave.io-hosted PWA).
  - A push gateway (APNs/FCM) and the native/Capacitor app itself.
  - Terminal, editor and browser canvas on the phone.
  - iOS Live Activities.
  - Option B ("Fleet folded down") polish of the desktop layout at phone width; the existing mobile layout keeps working as it is.
- Constraints / assumptions:
  - Decisions below are final; do not reopen them.
  - Local mode only (`!Auth.Enabled && Auth.TokenAuthEnabled`). OIDC/hosted mode is untouched; new endpoints are not mapped there.
  - Install + push need a secure context (HTTPS via `tailscale serve`, or `localhost`). Plain LAN HTTP still works as a phone web page; the UI explains why install/push are unavailable.
  - Pairing codes live in memory (10-min TTL); a Fleet restart invalidates outstanding codes. That is acceptable.
  - `apiVersion` in `GET /api/machine` stays `1`: everything here adds fields/endpoints.
  - Opening a session that lives on another machine from the phone inbox uses the existing active-machine switch (page reload into the phone session route). The inbox itself is multi-machine with no reload. A no-reload multi-machine session stream is a later refactor.

## Decisions (final — recorded, not to be reopened)
1. **Per-machine origin.** Each machine serves its own PWA from its own origin (e.g. `https://hangar.<tailnet>.ts.net`). No hosted app in v1.
2. **Home machine fans out.** The machine a phone paired with first is its *home machine*; it sends push for all machines. The machine list (URL + token) moves server-side; the home server subscribes (SignalR client, bearer) to each other machine's global `sessions` topic and sends Web Push on its behalf. If home is down, notifications stop and the inbox marks machines unreachable. Backward-compatible migration from `localStorage["weave:machines"]`.
3. **Tailscale required for install + push in v1.** Plain LAN HTTP works in a phone browser without install/push; the UI explains this when not in a secure context.
4. **Per-device tokens.** QR pairing issues each phone its own revocable token, stored hashed, compared in constant time. Device tokens can do everything (start sessions, steer, approve, `!` commands, `/btw`) **except**: read/replace the machine token (`GET /api/machine/access`, `POST /api/machine/access/token`), list/add/remove devices or create pairing codes, change the machine list. Device tokens expire after 30 days unused (sliding). Settings › Machines › This machine gets "Add a phone" (one-time QR, 10-min TTL, single use, secret in URL fragment, never in query/logs) and "Devices with access" (last seen + Remove). Landing page: "Connect this phone to <machine>?" with device name + Connect.
5. **v1 UI** = notifications (option C) + cross-machine "Needs you" inbox (option A) + full phone session view with steering per `session.html`.
6. **Web Push** with VAPID via NuGet `WebPush` (web-push-libs/web-push-csharp), aes128gcm. VAPID keys per machine, persisted next to `fleet.machine.json`. Kinds: permission (needs you), question, finished, failed; per-kind toggles + "quiet while Fleet is open on a screen" (visibility heartbeat). Small generic payload: machineId, sessionId, reason/kind, title, short text, deep-link URL. 404/410 delete the subscription; handle `pushsubscriptionchange`. Android: action buttons Allow once / Deny on permission asks (SW posts the answer with the device token). iOS: no actions; tap opens the deep link; requires Home Screen install and permission request on a user gesture (onboarding screen per mockup).
7. **Installable PWA**: manifest (name, icons incl. maskable, `start_url`, `display: standalone`, `id`), service worker at root scope served by Kestrel with correct headers and `no-cache` for `sw.js`. **Hand-written SW** (no vite-plugin-pwa, no Workbox), built by a tiny separate Vite build; no offline caching of API responses.
8. **Native-ready.** (a) Push sender behind `IPushSender`; subscriptions table has a `channel` column (`webpush` now; `apns`/`fcm` later via a tryweave.io gateway). (b) QR payload versioned and app-neutral (machine URL + one-time secret in the fragment, JSON with a `v` field). (c) Phone logic in `client/src/lib/phone/` and composables, not tied to browser-only APIs, so a Capacitor wrapper is cheap. Non-browser clients use the `Authorization` header everywhere.

## Architecture

### Tokens and scopes
- Device token wire format: `fdt_<deviceId>.<secret>` — `deviceId` a ULID (package `Ulid` already referenced by Application), `secret` 32 random bytes base64url. DB stores `sha256(secret)` only. Validation: parse id → load row (cached) → check not revoked/expired → `CryptographicOperations.FixedTimeEquals`. Anything not starting `fdt_` goes to the existing machine-token path.
- Claims added by `BearerTokenHandler` (and in device cookies): `amr=token` (unchanged, so terminal checks keep working), `fleet_scope` = `owner` | `device`, and `fleet_device` = deviceId for devices. Absence of `fleet_scope` (old cookies, loopback) means owner.
- New authorization policy `MachineOwner`: authenticated, `fleet_scope` != `device`, and `amr` != `agent`. Applied to `/api/machine/access*`, `/api/machine/devices*`, `/api/machine/pairing*` (create), and mutating `/api/machines*` routes.
- Phone same-origin session: `POST /api/pairing/redeem` returns the token **and** signs in a persistent cookie carrying the device claims, so same-origin features that rely on cookies (images, `/pages`, hub) work. Cookie `OnValidatePrincipal` re-checks the device (revoked/expired → reject + sign out) using a 30 s in-memory cache.
- `last_used_at` written at most once per 5 minutes per device (in-memory throttle). Expiry = `last_used_at + 30 days`.

### Pairing
- `POST /api/machine/pairing` (MachineOwner) → `{ secret, manualCode, expiresAt, url, payload }`. `secret` 32 bytes base64url; `manualCode` 8 chars Crockford base32 (typeable fallback for iOS Home Screen storage split and future native apps). Both stored hashed in memory, 10-min TTL, single use (atomic remove on redeem).
- QR content = URL `https://<machine>/pair#p=<base64url(JSON)>` where JSON = `{"v":1,"machineId","machineName","url","secret"}`. Fragment never reaches the server or logs. A native scanner decodes the same JSON.
- `POST /api/pairing/preview { secret | manualCode }` (anonymous, rate limited) → `{ machineId, machineName, os, expiresAt }`, does not consume.
- `POST /api/pairing/redeem { secret | manualCode, deviceName, platform }` (anonymous, rate limited) → `{ deviceId, token, machine }` + device cookie.
- Phone URL shown in the QR: `MachineIdentity.PublicUrl` if set (new optional field, editable in Settings), else the page's own origin when it isn't loopback, else the first HTTPS address; the desktop UI lets you edit it and warns if it is not HTTPS.

### Push
- `IPushSender { string Channel; Task<PushSendResult> SendAsync(PushSubscriptionRecord sub, PushMessage msg, CancellationToken ct); }`, `WebPushSender` implements `webpush`. `PushSendResult` = Delivered | Gone (404/410 → delete) | RetryLater (429/5xx) | Failed.
- `VapidKeyStore` → `<db>.push.json` (0600 on Unix, same pattern as `MachineIdentityStore`), generated on first use with `VapidHelper.GenerateVapidKeys()`. Subject from `Fleet:Push:Subject`, default `https://tryweave.io` (Apple rejects invalid/`localhost` subjects).
- Table `push_subscriptions`: `id, device_id NULL, channel, endpoint UNIQUE, p256dh, auth, kinds (JSON), quiet_when_desk INTEGER, user_agent, created_at, last_success_at, failure_count`.
- `SessionNotifier` stops gating on the desktop preference (the browser already gates) and fans out to `ISessionNotificationSink`s: the existing SignalR broadcast, and `PushNotificationDispatcher`. Payload gains `kind` (`permission` | `question` | `finished` | `failed` | `workflow`), `requestId` (permission/question), `machineId`; `reason` keeps `needs_you`/`finished` for old clients (`failed` added).
- Quiet-at-desk: `DeskPresenceTracker` fed by new hub method `SetPresenceAsync(bool visible, string formFactor)`, heartbeat every 30 s, entry expires after 90 s. Push for a subscription with `quiet_when_desk` is skipped while any `desktop` connection is visible on the sending (home) machine.
- Push payload (≤ 1 KB, JSON): `{ "v":1, "machineId", "machineName", "sessionId", "kind", "reason", "title", "body", "url", "tag", "requestId"? }`. `url` = `/phone/s/<machineId>/<sessionId>?ask=<requestId>`; `tag` = `<machineId>:<sessionId>` so repeats collapse.

### Cross-machine
- Table `machines` on every Fleet: `id (remote machine id), name, base_url, encrypted_token, os, added_at, last_seen_at, status`. Tokens protected with the existing `ICredentialProtector` (`src/WeaveFleet.Infrastructure/Services/DataProtectionCredentialProtector.cs`).
- `RemoteMachineWatcher` (hosted service, Infrastructure): one `HubConnection` per listed machine to `<base>/hubs/session-events` with `AccessTokenProvider` = machine token, invokes `SubscribeToSessionsTopicAsync`, forwards `session_notification` events to `PushNotificationDispatcher` tagged with that machine's id/name. Exponential backoff (2 s → 60 s), status recorded for the inbox.
- Device grants: the phone holds a device token only for home. `POST /api/machines/{id}/device-grant` (device or owner caller) → home calls the remote machine's `POST /api/machine/devices` with the stored machine token to mint a device token named after the phone (`pairedVia` = home machine id), stores the mapping `(phone device id → remote device id)` in `device_grants`, returns `{ machineId, baseUrl, token }` to the phone. The phone talks to the remote directly cross-origin with that token (`credentials: "omit"`, existing CORS rule). Removing the phone on home revokes its grants on remotes (best effort, retried by the watcher).

### Client
- Pure, framework-light logic under `client/src/lib/phone/` and `client/src/lib/push/` (unit-tested); composables under `client/src/composables/phone/`; components under `client/src/components/phone/`; routes `client/src/routes/pair.tsx`, `phone.tsx` (+ children). `/pair` and `/phone*` render their own `PhoneShell`, not `AppShell` (adjust `__root.tsx`).
- Credentials on the phone: `client/src/lib/device-credentials.ts` keeps `{ homeMachineId, deviceId, token, grants: {machineId, baseUrl, token}[] }` in IndexedDB (readable by the service worker) with a localStorage mirror for sync reads.
- Service worker: `client/src/sw/sw.ts` built to `dist/sw.js` (IIFE, no hash) by `client/vite.sw.config.ts`; imports pure helpers from `client/src/lib/push/`.

## Objectives
- A phone can be paired by scanning a QR, gets its own token, and can be removed without affecting any other device.
- Fleet installs to the Home Screen (iOS/Android) from a `tailscale serve` HTTPS origin, and sends Web Push for needs-you / question / finished / failed, honouring per-kind and quiet-at-desk settings.
- One phone gets notifications and an inbox for every machine in the home machine's list.
- The phone session view supports reading, answering, queueing, steering, stopping, `!` commands and `/btw`, matching `mockups/phone-app/session.html`.
- Desktop behaviour, the machine-token contract and CORS rules are unchanged for existing clients.

## Dependencies and Order
1. **Phase 1 (Tasks 1–6) — devices + pairing.** Everything else authenticates the phone with a device token. Ships: phones can pair and use the existing mobile web layout.
2. **Phase 2 (Tasks 7–12) — PWA + local push.** Needs device ids (Task 1) to attach subscriptions and device-scoped endpoints (Task 3). Ships: installable app with notifications for the home machine's own sessions.
3. **Phase 3 (Tasks 13–16) — server-side machine list + fan-out + grants.** Needs the dispatcher (Task 11) and device minting endpoint (Task 4). Ships: notifications for every machine.
4. **Phase 4 (Tasks 17–18) — inbox.** Needs grants (Task 16) to reach other machines and the phone shell/credentials (Task 6). Ships: option A home screen.
5. **Phase 5 (Tasks 19–22) — session view.** Needs phone shell and routes (Task 18). Ships: full session on the phone.
6. **Phase 6 (Tasks 23–24) — actions, deep links, awkward cases.** Needs SW (Task 8), payload `requestId` (Task 11), docked ask (Task 21).
7. **Phase 7 (Tasks 25–28) — docs, E2E, security audit, final verification.** Security audit (Task 27) must pass before merge.
- Within a phase, server tasks precede client tasks that call them. Tasks 2/3/4 are strictly sequential (store → auth → endpoints).

## Tasks

### Phase 1 — Per-device tokens and pairing

- [x] 1. Devices table, entity and repository
  - **What**: Persist paired devices.
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/048_add_devices.sql` (use the next free number if 048 is taken), `src/WeaveFleet.Domain/Entities/Device.cs`, `src/WeaveFleet.Domain/Repositories/IDeviceRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DeviceRepository.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `tests/WeaveFleet.Infrastructure.Tests/Data/DeviceRepositoryTests.cs`
  - **Depends on**: None
  - **Implementation outline**:
    1. Migration: `devices(id TEXT PK, name TEXT NOT NULL, platform TEXT, token_hash BLOB NOT NULL, paired_via TEXT NULL, created_at TEXT NOT NULL, last_used_at TEXT NOT NULL, revoked_at TEXT NULL)` plus index on `revoked_at`. Follow the style of `047_add_agent_browser_steps.sql`; migrations are embedded (`Migrations\*.sql` in the csproj) and run by `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs`.
    2. Entity + interface: `GetAsync(id)`, `ListActiveAsync()`, `InsertAsync`, `TouchAsync(id, lastUsedAt)`, `RevokeAsync(id, at)`. Follow `QueuedPromptRepository` / `SmartLinkRepository` Dapper patterns.
    3. Register in DI.
  - **Pitfalls / non-goals**:
    - Never store the raw secret. Store `sha256(secret)` bytes.
    - Keep `paired_via` for grants minted by another machine (Task 16).
  - **Acceptance**:
    - Migration applies on a fresh and on an existing DB.
    - Repository round-trips, revoke hides the row from `ListActiveAsync`.
    - `dotnet test tests/WeaveFleet.Infrastructure.Tests --filter "FullyQualifiedName~DeviceRepository"` passes.

- [x] 2. DeviceTokenService (issue, validate, expire, revoke)
  - **What**: Application service owning device token format, hashing, sliding expiry and last-seen throttling.
  - **Files**: `src/WeaveFleet.Application/Devices/DeviceTokenService.cs`, `src/WeaveFleet.Application/Devices/DeviceToken.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `tests/WeaveFleet.Application.Tests/Devices/DeviceTokenServiceTests.cs`
  - **Depends on**: Task 1
  - **Implementation outline**:
    1. `IssueAsync(name, platform, pairedVia?)` → `(Device, string token)`; token `fdt_<ulid>.<base64url 32 bytes>`.
    2. `ValidateAsync(string token)` → `DeviceValidation?` (deviceId, name). Parse, load (memory cache 30 s keyed by id, invalidated on revoke), reject revoked or `last_used_at + 30d < now`, `CryptographicOperations.FixedTimeEquals(sha256(secret), stored)`.
    3. Touch `last_used_at` at most every 5 minutes per device (in-memory `ConcurrentDictionary<string, DateTime>`).
    4. `RevokeAsync(id)`, `ListAsync()` (returns name, platform, createdAt, lastUsedAt, pairedVia — never hashes). Inject `TimeProvider` for testable time.
  - **Pitfalls / non-goals**:
    - Malformed tokens must fail fast without DB hits that leak timing on id existence beyond what is unavoidable; always run the hash compare against a dummy hash when the id is unknown.
    - Validation must be callable synchronously-cheaply from the auth handler (cache hit path).
  - **Acceptance**:
    - Tests cover: valid token, wrong secret, unknown id, malformed, revoked, expired at 30 days + 1 s, sliding renewal after use, throttled touch.
    - `dotnet test tests/WeaveFleet.Application.Tests --filter "FullyQualifiedName~DeviceTokenService"` passes.

- [x] 3. Auth: device tokens in bearer + cookie, `MachineOwner` policy
  - **What**: Accept device tokens everywhere a machine token works, tag them with scope claims, keep revoked devices out of cookie sessions, and lock owner-only endpoints.
  - **Files**: `src/WeaveFleet.Api/Auth/BearerTokenHandler.cs`, `src/WeaveFleet.Api/Auth/FleetClaims.cs` (new), `src/WeaveFleet.Api/Program.cs`, `src/WeaveFleet.Api/Endpoints/AuthEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/MachineEndpoints.cs`, `tests/WeaveFleet.Api.Tests/Auth/DeviceTokenAuthTests.cs`, `tests/WeaveFleet.Api.Tests/Auth/MachineAccessEndpointTests.cs`
  - **Depends on**: Task 2
  - **Implementation outline**:
    1. `FleetClaims`: constants `Scope = "fleet_scope"`, `Owner`, `Device`, `DeviceId = "fleet_device"`, helper `IsOwner(ClaimsPrincipal)` (no scope claim ⇒ owner, except `amr=agent`).
    2. `BearerTokenHandler.HandleAuthenticateAsync`: if presented starts with `fdt_` → `DeviceTokenService.ValidateAsync`; success ⇒ claims `amr=token`, `fleet_scope=device`, `fleet_device=<id>`. Otherwise existing machine-token path, adding `fleet_scope=owner`. Handler becomes async (`Task<AuthenticateResult>`).
    3. `/auth/token-login`: accept a device token too; the cookie copies the device claims.
    4. Cookie options in `Program.cs`: `Events.OnValidatePrincipal` — if the principal has `fleet_device`, re-validate the device by id (cached); revoked/expired ⇒ `RejectPrincipal()` + `SignOutAsync`.
    5. Add authorization policy `MachineOwner` (same schemes as `FleetUser`, plus `RequireAssertion(ctx => FleetClaims.IsOwner(ctx.User))`). Apply to `GET /api/machine/access` and `POST /api/machine/access/token`. A device caller gets `403`.
  - **Pitfalls / non-goals**:
    - Do not change `?access_token=` scoping (hubs + WS only) or `MachineCorsPolicyProvider` — device tokens get the same no-credentials CORS as machine tokens because `PresentsToken` is format-agnostic. Add a test proving `Access-Control-Allow-Credentials` is never set.
    - A wrong device token must be `401`, never fall back to loopback/cookie.
    - Existing cookies without scope claims keep owner rights (back compat).
    - Agents (`amr=agent`) must not pass `MachineOwner`.
  - **Acceptance**:
    - Device token works on `GET /api/sessions` (header) and on `/hubs/session-events?access_token=`; ignored in query on other paths.
    - Device token on `/api/machine/access` ⇒ 403; machine token ⇒ 200.
    - Revoked device: header ⇒ 401; existing cookie ⇒ 401 on next API call.
    - `dotnet test tests/WeaveFleet.Api.Tests --filter "FullyQualifiedName~Auth"` passes (old tests unchanged and green).

- [x] 4. Pairing service and endpoints, device management, `PublicUrl`
  - **What**: One-time pairing codes, preview/redeem, device list/remove, machine-to-machine device minting, and a configurable phone URL.
  - **Files**: `src/WeaveFleet.Application/Devices/PairingCodeStore.cs`, `src/WeaveFleet.Application/Configuration/MachineIdentityStore.cs` (add optional `PublicUrl` to `MachineIdentity`), `src/WeaveFleet.Api/Endpoints/DeviceEndpoints.cs` (new; map from `MapFleetEndpoints` in `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`), `src/WeaveFleet.Api/Endpoints/MachineEndpoints.cs`, `src/WeaveFleet.Api/Contracts/DeviceContracts.cs`, `src/WeaveFleet.Api/JsonContext.cs`, `src/WeaveFleet.Api/Program.cs` (rate limiter), `tests/WeaveFleet.Api.Tests/Endpoints/PairingEndpointTests.cs`, `tests/WeaveFleet.Application.Tests/Devices/PairingCodeStoreTests.cs`
  - **Depends on**: Task 3
  - **Implementation outline**:
    1. `PairingCodeStore` (singleton, in memory, `TimeProvider`): `Create()` → secret (32 B base64url) + manual code (8 chars Crockford base32, display as `XXXX-XXXX`), both hashed; `Peek(secretOrCode)`; `TryConsume(secretOrCode)` atomic; TTL 10 min; purge expired on access; at most 5 live codes.
    2. Endpoints (local mode only, same guard as `MachineEndpoints`):
       - `POST /api/machine/pairing` [MachineOwner] → `{ secret, manualCode, expiresAt, url, payload }` where `payload` is the v1 JSON and `url` = `<phoneBase>/pair#p=<base64url(payload)>`; `phoneBase` from request body `{ baseUrl }` (client sends the chosen URL) validated as absolute http(s).
       - `POST /api/pairing/preview` [AllowAnonymous] `{ secret?, manualCode? }` → `{ machineId, machineName, os, expiresAt }` or 404.
       - `POST /api/pairing/redeem` [AllowAnonymous] `{ secret?, manualCode?, deviceName, platform }` → `{ deviceId, token, machine: GET /api/machine shape }`; signs in the device cookie (persistent).
       - `GET /api/machine/devices` [MachineOwner], `DELETE /api/machine/devices/{id}` [MachineOwner], `POST /api/machine/devices` [MachineOwner, machine-token (`amr=token`, owner) only — not cookie] `{ name, platform, pairedVia }` → `{ deviceId, token }` for Task 16.
       - `PUT /api/machine` accepts optional `publicUrl`; `GET /api/machine` returns it.
    3. Rate limit `/api/pairing/*` with ASP.NET `AddRateLimiter` fixed window (e.g. 10/min global partition — behind `tailscale serve` every caller is 127.0.0.1) and stricter for manual codes (5/min).
    4. Device name: trim, 1–60 chars; platform enum `ios|android|other`.
  - **Pitfalls / non-goals**:
    - Secrets only ever in request bodies; never accept them in the query string; never log them (check `LoggerMessage` calls and request logging).
    - `/api/pairing/*` must be excluded from any auth requirement but still behind the antiforgery middleware rules (local mode has auth disabled so antiforgery is skipped — confirm).
    - Redeem returns 404 for expired/used codes with a distinguishable `code` field (`expired` | `used` | `unknown`) only if it does not help brute force; simplest: `{ error: "This code has expired or was already used." }`.
  - **Acceptance**:
    - Create → preview → redeem works once; second redeem 404; after 10 min 404.
    - Device callers get 403 on pairing/devices endpoints; anonymous on preview/redeem works.
    - Removing a device makes its token 401 immediately.
    - `dotnet test tests/WeaveFleet.Api.Tests --filter "FullyQualifiedName~Pairing"` and `tests/WeaveFleet.Application.Tests --filter "FullyQualifiedName~PairingCodeStore"` pass.

- [x] 5. Settings › Machines › This machine: "Add a phone" and "Devices with access"
  - **What**: Desktop UI to show a QR + manual code and manage devices, per `mockups/phone-app/index.html` (section "Getting Fleet onto a phone", step 1).
  - **Files**: `client/src/components/settings/MachinesSection.vue`, `client/src/components/settings/AddPhonePanel.vue` (new), `client/src/components/settings/DevicesList.vue` (new), `client/src/lib/pairing.ts` (new: encode/decode v1 payload, choose phone base URL), `client/src/lib/__tests__/pairing.test.ts`, `client/package.json` (QR dependency)
  - **Depends on**: Task 4
  - **Implementation outline**:
    1. Add a small dependency-free QR encoder (`bun add uqr` or equivalent; render as SVG).
    2. `pairing.ts`: `encodePairingPayload`, `decodePairingFragment(hash)` (validates `v === 1`, required fields), `choosePhoneBaseUrl(publicUrl, location, addresses)`.
    3. "Add a phone" button → POST pairing → QR + `XXXX-XXXX` code + countdown + editable phone URL (saved as `publicUrl`); warning when URL is not `https:` ("Install and notifications need HTTPS — see docs/phone.md").
    4. "Devices with access": "This computer" row + each device (name, added date, last seen relative, Remove with confirm). Refresh on pairing success (poll every 3 s while the QR is open).
  - **Pitfalls / non-goals**:
    - Only show for owner sessions; a device-scoped browser gets 403 — hide the panel gracefully.
    - Don't put the secret in any URL other than the fragment; don't persist it.
  - **Acceptance**:
    - `bun run test` (in `client/`) passes including `pairing.test.ts` round-trip + rejection of `v: 2`.
    - `bunx vue-tsc --noEmit` clean.
    - Visual check against the mockup (screenshot at desktop size).

- [x] 6. Phone pairing landing `/pair` and device credentials
  - **What**: "Connect this phone to <machine>?" page; stores the device token; works outside `AppShell`.
  - **Files**: `client/src/routes/pair.tsx`, `client/src/components/phone/PhoneShell.vue` (new, minimal frame with safe-area insets), `client/src/components/phone/PairPage.vue`, `client/src/lib/device-credentials.ts`, `client/src/lib/__tests__/device-credentials.test.ts`, `client/src/routes/__root.tsx`, `client/src/components/layout/AuthGate.vue` (or wherever `AuthGate` lives — skip auth gate for `/pair`)
  - **Depends on**: Tasks 4, 5
  - **Implementation outline**:
    1. On load read `location.hash`, decode via `pairing.ts`, then immediately `history.replaceState` to drop the fragment.
    2. Preview → show machine name/os, device-name field prefilled from UA (`iPhone`, `Pixel`…), Connect → redeem → save credentials (IndexedDB + localStorage mirror) → navigate to `/phone/setup` (Phase 2; until then `/`).
    3. No fragment (e.g. opened from the Home Screen without credentials, iOS storage split): show manual code entry `XXXX-XXXX`.
    4. Error states: expired/used ("Ask for a new code on the computer"), wrong machine (payload `url` origin ≠ `location.origin` → offer to open the right URL).
    5. `__root.tsx`: render `<Outlet/>` inside `PhoneShell` (no `AppShell`, no `AuthGate`) for `/pair`.
  - **Pitfalls / non-goals**:
    - `device-credentials.ts` must not import Vue; it is shared with the service worker build later.
    - iOS: Safari and the Home Screen app may not share storage; the manual code path is the fallback — call it out in the UI copy ("Opened from the Home Screen? Enter the code shown on your computer").
  - **Acceptance**:
    - Unit tests for credentials store (in-memory IndexedDB shim, e.g. `fake-indexeddb` via `bun add -d`).
    - Manual: pair a Playwright Chromium at 390×844 and see the app load signed in; Settings shows the new device.
    - `bun run test`, `bunx vue-tsc --noEmit` pass.

### Phase 2 — Installable PWA and Web Push for the local machine

- [ ] 7. Manifest, icons and Kestrel headers
  - **What**: Make the app installable.
  - **Files**: `client/public/manifest.webmanifest`, `client/public/icons/icon-192.png`, `icon-512.png`, `icon-maskable-512.png`, `apple-touch-icon.png` (180), `client/index.html`, `src/WeaveFleet.Api/Program.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/PwaStaticFilesTests.cs`
  - **Depends on**: None (can run in parallel with Phase 1)
  - **Implementation outline**:
    1. Manifest: `id: "/phone"`, `name: "Weave Fleet"`, `short_name: "Fleet"`, `start_url: "/phone?source=pwa"`, `scope: "/"`, `display: "standalone"`, theme/background `#0c0c0d`, icons incl. `purpose: "maskable"`.
    2. Generate icons from `client/src/assets/weave_logo.png` (one-off script with Playwright/`sharp` via `bunx`; commit the PNGs, not the script output pipeline).
    3. `index.html`: `<link rel="manifest">`, `<link rel="apple-touch-icon">` (meta tags for apple already exist).
    4. `Program.cs`: `FileExtensionContentTypeProvider` mapping `.webmanifest` → `application/manifest+json`; ensure `sw.js` and `manifest.webmanifest` get `Cache-Control: no-cache` (current non-`/assets` rule does this — assert in test) and `sw.js` gets `Service-Worker-Allowed: /` (harmless, explicit).
  - **Pitfalls / non-goals**:
    - The manifest and icons must be served anonymously (static files run before endpoint auth; confirm).
    - Vite copies `client/public/` to `dist/` root; check the csproj copy picks up `public` output (it copies all of `dist`).
  - **Acceptance**:
    - Test boots the API with a temp `wwwroot` containing `sw.js`/`manifest.webmanifest` and asserts content types + cache headers.
    - Chrome DevTools/Lighthouse "installable" passes on `https://<machine>.ts.net` (manual) and on `http://localhost` in Playwright (`page.evaluate` checks `navigator.serviceWorker` + manifest fetch 200).

- [ ] 8. Hand-written service worker, build and registration
  - **What**: SW that shows pushes, opens deep links, and re-subscribes on `pushsubscriptionchange`. No API caching.
  - **Files**: `client/src/sw/sw.ts`, `client/vite.sw.config.ts`, `client/package.json` (`build`: `vite build && vite build -c vite.sw.config.ts`), `client/src/lib/push/payload.ts`, `client/src/lib/push/notification-options.ts`, `client/src/lib/push/__tests__/payload.test.ts`, `client/src/composables/use-service-worker.ts`, `client/src/main.ts`, `client/tsconfig*.json` (WebWorker lib for `src/sw`)
  - **Depends on**: Task 7
  - **Implementation outline**:
    1. `vite.sw.config.ts`: lib build, entry `src/sw/sw.ts`, format `iife`, `fileName: () => "sw.js"`, `outDir: "dist"`, `emptyOutDir: false`, same `@` alias.
    2. `payload.ts`: `parsePushPayload(json)` (validates `v:1`), `notificationOptionsFor(payload, platform)` → title/body/tag/data/actions (actions only for `kind === "permission"` and only added in Task 23; leave a hook).
    3. `sw.ts`: `install` → `skipWaiting`; `activate` → `clients.claim`; `push` → parse + `showNotification`; `notificationclick` → focus an existing client on the same origin and `postMessage({type:"navigate", url})`, else `clients.openWindow(url)`; `pushsubscriptionchange` → subscribe again with stored VAPID key and `PUT /api/push/subscriptions` with the device token from IndexedDB (`device-credentials.ts`); `fetch` handler **absent** (no caching).
    4. `use-service-worker.ts`: register `/sw.js` only when `window.isSecureContext` and `import.meta.env.PROD` (or `VITE_SW_DEV=1`), expose `registration`, listen for `navigate` messages and route with the router.
  - **Pitfalls / non-goals**:
    - No Workbox, no vite-plugin-pwa, no precache.
    - SW must not import Vue or anything touching `window`.
    - Dev server on :3002 serves no `sw.js` unless built; document `VITE_SW_DEV`.
  - **Acceptance**:
    - `bun run build` produces `dist/sw.js` (no hash) and `dist/manifest.webmanifest`.
    - `bun run test` covers payload parsing (valid, wrong version, oversized fields truncated).
    - In Playwright Chromium against the built app, `navigator.serviceWorker.ready` resolves and `registration.showNotification` from the page works.

- [ ] 9. VAPID keys, push subscription storage and `IPushSender`
  - **What**: Server-side push foundation.
  - **Files**: `Directory.Packages.props` (add `WebPush`), `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`, `src/WeaveFleet.Application/Push/IPushSender.cs`, `src/WeaveFleet.Application/Push/PushModels.cs` (`PushMessage`, `PushSendResult`, `PushSubscriptionRecord`, `NotificationKinds`), `src/WeaveFleet.Application/Configuration/VapidKeyStore.cs`, `src/WeaveFleet.Infrastructure/Push/WebPushSender.cs`, `src/WeaveFleet.Infrastructure/Migrations/049_add_push_subscriptions.sql`, `src/WeaveFleet.Domain/Repositories/IPushSubscriptionRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/PushSubscriptionRepository.cs`, `src/WeaveFleet.Application/Configuration/FleetOptions.cs` (`Push.Subject`), `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, tests in `tests/WeaveFleet.Infrastructure.Tests/Push/` and `tests/WeaveFleet.Application.Tests/Configuration/VapidKeyStoreTests.cs`
  - **Depends on**: Task 1
  - **Implementation outline**:
    1. `VapidKeyStore(databasePath)` → `<db>.push.json` `{ publicKey, privateKey, createdAt }`, created with `VapidHelper.GenerateVapidKeys()`, Unix mode 0600 (copy the write/permission code from `MachineIdentityStore`).
    2. Table per Architecture (`channel` default `'webpush'`, `endpoint` unique, FK-less `device_id`). Repository: upsert by endpoint, list by kind, delete by endpoint, delete by device, record success/failure.
    3. `WebPushSender` (`Channel = "webpush"`): `WebPushClient.SendNotificationAsync(new PushSubscription(endpoint,p256dh,auth), json, new VapidDetails(subject, pub, priv))` with TTL 1 h and urgency `high` for permission/question, `normal` otherwise. Map `WebPushException.StatusCode`: 404/410 → `Gone`, 429/5xx → `RetryLater`, else `Failed`. Uses aes128gcm (library default — verify the version in use does; pin a version that supports it).
    4. Register `IPushSender` as an enumerable so future `apns`/`fcm` senders plug in by `Channel`.
  - **Pitfalls / non-goals**:
    - Never log endpoints in full (they are capability URLs) — log host only.
    - Use a shared `HttpClient` via `IHttpClientFactory`.
  - **Acceptance**:
    - Integration test: a local Kestrel/`HttpListener` fake push service receives a POST with `Content-Encoding: aes128gcm`, `Authorization: vapid t=…, k=…`, `TTL` header; returning 410 yields `Gone`.
    - VAPID keys persist across store instances.
    - `dotnet test tests/WeaveFleet.Infrastructure.Tests --filter "FullyQualifiedName~Push"` passes.

- [ ] 10. Push subscription endpoints
  - **What**: Let a browser register, update preferences, test and remove its subscription.
  - **Files**: `src/WeaveFleet.Api/Endpoints/PushEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`, `src/WeaveFleet.Api/Contracts/PushContracts.cs`, `src/WeaveFleet.Api/JsonContext.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/PushEndpointTests.cs`
  - **Depends on**: Task 9, Task 3
  - **Implementation outline**:
    1. `GET /api/push/vapid` → `{ publicKey }` (FleetUser).
    2. `PUT /api/push/subscriptions` `{ endpoint, keys:{p256dh,auth}, kinds:[...], quietWhenDesk, channel? }` → upsert; set `device_id` from `fleet_device` claim when present.
    3. `GET /api/push/subscriptions/current?endpoint=` is **not** used (endpoint is secret-ish) — use `POST /api/push/subscriptions/lookup { endpoint }` to read prefs.
    4. `DELETE /api/push/subscriptions` `{ endpoint }`; `POST /api/push/test { endpoint }` sends a test push to that subscription only.
    5. Revoking a device (Task 4 DELETE) deletes its subscriptions.
  - **Pitfalls / non-goals**:
    - A device may only modify subscriptions with its own `device_id` (or null-device ones it created via endpoint knowledge); owners can modify any.
    - Validate endpoint is `https://` and kinds are known.
  - **Acceptance**:
    - Tests for upsert, prefs update, delete, device isolation, cascade on device removal.
    - `dotnet test tests/WeaveFleet.Api.Tests --filter "FullyQualifiedName~PushEndpoint"` passes.

- [ ] 11. Notification kinds, sinks, push dispatcher and desk presence
  - **What**: Turn session notifications into pushes with kinds, preferences and quiet-at-desk.
  - **Files**: `src/WeaveFleet.Domain/Events/SessionEvents.cs`, `src/WeaveFleet.Application/Sessions/SessionNotifier.cs`, `src/WeaveFleet.Application/Sessions/ISessionNotificationSink.cs`, `src/WeaveFleet.Application/Sessions/BroadcastNotificationSink.cs`, `src/WeaveFleet.Application/Push/PushNotificationDispatcher.cs`, `src/WeaveFleet.Application/Sessions/DeskPresenceTracker.cs`, `src/WeaveFleet.Api/Hubs/*` (the session-events hub class: add `SetPresenceAsync`, remove presence on disconnect), `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs` (only if a hook for `failed` is needed), `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (where lifecycle status becomes `error`, `_lifecycleStatusError`), `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `client/src/lib/domain-events.ts`, `client/src/composables/use-session-notifications.ts`, tests `tests/WeaveFleet.Application.Tests/Sessions/SessionNotifierTests.cs` (extend or create), `tests/WeaveFleet.Application.Tests/Push/PushNotificationDispatcherTests.cs`, `tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`
  - **Depends on**: Tasks 9, 10
  - **Implementation outline**:
    1. Add `SessionNotificationReasons.Failed = "failed"` and `SessionNotificationKinds` (`permission`, `question`, `finished`, `failed`, `workflow`). Extend `SessionNotificationPayload` with `Kind`, `RequestId?`, `MachineId`, `MachineName` (keep `Reason`).
    2. `SessionNotifier`: on `waiting_input` decide kind: `IPendingPermissions.WaitingIn(sessionId)` non-empty ⇒ `permission` (+ first request id), else `question` (find the pending question id the same way the client does — check how questions are surfaced; if not cheaply available, omit `requestId`). Body: short command/question text, ≤ 140 chars. Add `OnSessionFailed(sessionId, message)` called where the session's lifecycle status becomes `error`.
    3. Replace direct broadcast with `IEnumerable<ISessionNotificationSink>`. `BroadcastNotificationSink` always broadcasts (remove the `INotificationPreference` gate from the server; the client already gates on `DesktopNotifications`). `PushNotificationDispatcher` is a sink.
    4. `PushNotificationDispatcher.HandleAsync(payload, origin)`: load subscriptions whose `kinds` include the kind; skip `quiet_when_desk` ones when `DeskPresenceTracker.AnyDesktopVisible`; build the push payload (Architecture) and send via the sender for `channel`; `Gone` ⇒ delete; failures increment `failure_count` and delete after 10 consecutive. Fire-and-forget off the relay pump with bounded concurrency (e.g. `Channel<T>` + 1 worker).
    5. `DeskPresenceTracker`: `Set(connectionId, visible, formFactor)`, expiry 90 s, `AnyDesktopVisible`.
    6. Client: `use-session-notifications.ts` keeps working (ignores unknown kinds gracefully); add a presence heartbeat (every 30 s + on `visibilitychange`) from `AppShell` with `formFactor` = `phone` when standalone/≤716px else `desktop`.
  - **Pitfalls / non-goals**:
    - Desktop notifications must still only show when the user enabled them (client-side gate) — verify with the existing client tests.
    - Do not push for child/hidden/archived sessions or watched sessions (preserve existing checks).
    - Avoid duplicate push when both `needs_you` activity and a workflow wait fire within a second: dedupe per `(sessionId, kind, requestId)` for 30 s.
  - **Acceptance**:
    - Unit tests: kind selection, failed path, kinds filter, quiet-at-desk, 410 cleanup, dedupe.
    - SignalR contract test asserts the new payload fields on `session_notification`.
    - `dotnet test tests/WeaveFleet.Application.Tests --filter "FullyQualifiedName~SessionNotifier|FullyQualifiedName~PushNotificationDispatcher"` and `dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests"` pass; `bun run test` passes.

- [ ] 12. Phone notification onboarding `/phone/setup` and push client
  - **What**: Screen 3 of the pairing flow in `index.html`: Add to Home Screen (iOS), per-kind toggles, "Quiet while I'm at the desk", "Turn on notifications".
  - **Files**: `client/src/routes/phone.setup.tsx`, `client/src/components/phone/NotificationSetup.vue`, `client/src/lib/push/capabilities.ts` (secure context, `PushManager`, standalone, iOS/Android detection), `client/src/lib/push/subscribe.ts`, `client/src/lib/push/__tests__/capabilities.test.ts`, `client/src/composables/phone/use-push-subscription.ts`
  - **Depends on**: Tasks 8, 10, 11, 6
  - **Implementation outline**:
    1. `capabilities.ts` returns one state: `insecure` (explain: "Install and notifications need HTTPS through tailscale serve — this page still works in the browser", link docs/phone.md), `ios-needs-install` (Share → Add to Home Screen instructions), `ready`, `denied`, `unsupported`.
    2. On the Turn on click (user gesture): `Notification.requestPermission()` → `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })` → `PUT /api/push/subscriptions` with kinds + quiet flag.
    3. On every app open: compare current subscription endpoint with the stored one; re-PUT if changed or missing (subscription churn).
    4. "Send a test notification" button (`POST /api/push/test`).
  - **Pitfalls / non-goals**:
    - Never call `requestPermission` outside a click handler (iOS ignores it).
    - Toggles saved server-side per subscription, not per device token.
  - **Acceptance**:
    - Unit tests for capability states (mock `navigator`, `matchMedia('(display-mode: standalone)')`).
    - Opt-in Web Push E2E (see Task 26) receives the test push via `registration.getNotifications()`.
    - `bun run test`, `bunx vue-tsc --noEmit` pass.

### Phase 3 — Server-side machine list and home-machine fan-out

- [ ] 13. Machines table and `/api/machines` endpoints
  - **What**: Move the machine list (URL + token) server-side.
  - **Files**: `src/WeaveFleet.Infrastructure/Migrations/050_add_machines.sql` (machines + device_grants), `src/WeaveFleet.Domain/Entities/RemoteMachine.cs`, `src/WeaveFleet.Domain/Repositories/IRemoteMachineRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/RemoteMachineRepository.cs`, `src/WeaveFleet.Application/Machines/RemoteMachineService.cs`, `src/WeaveFleet.Api/Endpoints/MachinesEndpoints.cs`, `src/WeaveFleet.Api/Contracts/MachinesContracts.cs`, `src/WeaveFleet.Api/JsonContext.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/MachinesEndpointTests.cs`
  - **Depends on**: Task 3
  - **Implementation outline**:
    1. `machines(id PK, name, base_url, encrypted_token, os, added_at, last_seen_at NULL, status TEXT)`; `device_grants(device_id, machine_id, remote_device_id, created_at, PRIMARY KEY(device_id, machine_id))`.
    2. Tokens encrypted with `ICredentialProtector` (scoped — resolve via scope in singletons).
    3. `GET /api/machines` (FleetUser): owners get tokens; devices get `{ id, name, baseUrl, os, status, lastSeenAt }` without tokens.
    4. `POST /api/machines` [MachineOwner] `{ baseUrl, token }` → server calls `<baseUrl>/api/machine` with the token (same checks as client `identifyMachine`: `apiVersion` supported), stores; `PUT /api/machines/{id}`, `DELETE /api/machines/{id}` [MachineOwner]; `POST /api/machines/import` [MachineOwner] `{ machines: MachineConnection[] }` → idempotent upsert by id, returns the list.
    5. Reject adding self (same machine id) and non-http(s) URLs.
  - **Pitfalls / non-goals**:
    - The server now makes outbound requests to user-provided URLs: restrict to http/https, short timeouts (5 s), no redirects to other hosts; owner-only so not an SSRF from devices.
  - **Acceptance**:
    - Tests: add/import/delete, token redaction for device callers, 403 for device mutations, identify failure → 400 with message.
    - `dotnet test tests/WeaveFleet.Api.Tests --filter "FullyQualifiedName~MachinesEndpoint"` passes.

- [ ] 14. Client: machine list from the server, one-time migration from localStorage
  - **What**: The desktop client reads/writes the machine list via `/api/machines`; existing `weave:machines` lists are imported once.
  - **Files**: `client/src/lib/machines.ts`, `client/src/stores/machines.ts`, `client/src/components/settings/MachinesSection.vue`, `client/src/lib/__tests__/machines.test.ts`, `client/src/stores/__tests__/machines.test.ts`
  - **Depends on**: Task 13
  - **Implementation outline**:
    1. On store init (home machine, owner): `GET /api/machines`; if `localStorage["weave:machines"]` has entries not on the server and `weave:machines-imported` is unset → `POST /api/machines/import`, then set the flag. Keep writing the list to localStorage as a cache (so `machines.ts` sync reads at startup and the active-machine reload still work).
    2. Add/remove in Settings go through the API.
    3. Device-scoped callers (403 on owner endpoints) read the redacted list and rely on grants (Task 16) for tokens.
  - **Pitfalls / non-goals**:
    - Do not break `switchToMachine`, `weave:active-machine`, `weave:session-machines`.
    - If the home server is older (404 on `/api/machines`), fall back to localStorage-only behaviour.
  - **Acceptance**:
    - Store tests: import once, no double import, 404 fallback, 403 device path.
    - `bun run test`, `bunx vue-tsc --noEmit` pass; existing machine tests unchanged and green.

- [ ] 15. `RemoteMachineWatcher`: subscribe to other machines and forward notifications
  - **What**: Home server listens to every listed machine's `sessions` topic and pushes on its behalf.
  - **Files**: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj` (add `Microsoft.AspNetCore.SignalR.Client`), `src/WeaveFleet.Infrastructure/Machines/RemoteMachineWatcher.cs`, `src/WeaveFleet.Infrastructure/Machines/RemoteMachineConnection.cs`, `src/WeaveFleet.Application/Machines/RemoteMachineService.cs` (status + change notifications), `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `tests/WeaveFleet.IntegrationTests/Machines/RemoteMachineWatcherTests.cs`
  - **Depends on**: Tasks 11, 13
  - **Implementation outline**:
    1. Hosted service keeps one `RemoteMachineConnection` per machine row; reacts to add/remove/update (event from `RemoteMachineService`).
    2. Connection: `HubConnectionBuilder().WithUrl(base + "/hubs/session-events", o => o.AccessTokenProvider = () => token)`; on connected invoke `SubscribeToSessionsTopicAsync` (check exact hub method names in `src/WeaveFleet.Api/Hubs/`); `On<string,long,JsonElement>("Event", ...)` filter `type == "session_notification"`.
    3. Forward to `PushNotificationDispatcher.HandleAsync(payload with MachineId/MachineName = remote)`; map old remotes' `reason` (`needs_you` → `permission`/`question` unknown ⇒ `permission` copy "Needs you") when `kind` missing.
    4. Backoff 2 s → 60 s; update `status` (`online` | `unreachable` | `unauthorized`) and `last_seen_at`; 401 ⇒ `unauthorized`, stop retrying until the token changes.
  - **Pitfalls / non-goals**:
    - The remote's hub origin check (`IsHubOriginAllowed`) only applies when OIDC auth is enabled; a .NET client sends no `Origin` — fine in local mode, note it in docs.
    - The remote only emits `session_notification` after Task 11 is deployed there (it used to be gated on that machine's desktop preference). Document "upgrade every machine".
    - Never forward notifications for the home machine itself (id check) to avoid loops.
  - **Acceptance**:
    - Integration test boots two in-process Fleets (two `WebApplicationFactory`/Kestrel hosts, as `SignalREventContractTests` does), lists B on A, broadcasts a `session_notification` on B, and asserts A's dispatcher receives it with B's machine id (fake `IPushSender`).
    - Unreachable B ⇒ status `unreachable` within one backoff cycle.
    - `dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~RemoteMachineWatcher"` passes.

- [ ] 16. Device grants: the phone gets its own token on other machines
  - **What**: Let a phone paired with home talk directly to other machines without ever seeing their machine tokens.
  - **Files**: `src/WeaveFleet.Application/Machines/DeviceGrantService.cs`, `src/WeaveFleet.Api/Endpoints/MachinesEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/DeviceEndpoints.cs` (cascade on delete), `client/src/lib/device-credentials.ts`, `client/src/composables/phone/use-machine-grants.ts`, tests `tests/WeaveFleet.Api.Tests/Endpoints/DeviceGrantTests.cs`, `client/src/composables/__tests__/use-machine-grants.test.ts`
  - **Depends on**: Tasks 4, 13, 15
  - **Implementation outline**:
    1. `POST /api/machines/{id}/device-grant` (FleetUser; caller must have `fleet_device`): reuse an existing grant if the remote still accepts it, else call remote `POST /api/machine/devices { name: "<device name> via <home>", platform, pairedVia: homeId }` with the stored machine token; persist `device_grants`; return `{ machineId, baseUrl, token }`.
    2. Home stores only the remote device id, never the remote device token (the phone keeps it).
    3. On `DELETE /api/machine/devices/{id}` at home: for each grant call remote `DELETE /api/machine/devices/{remoteId}`; failures are retried by the watcher when the machine is reachable again.
    4. Client: after pairing and on inbox load, request grants for every listed machine lacking one; store in credentials; on 401 from a remote, drop and re-request once.
  - **Pitfalls / non-goals**:
    - Remote must be reachable from the phone over HTTPS (mixed content). If `baseUrl` is `http:` and the page is `https:`, the inbox shows "Reachable only from computers — add it with its https://…ts.net address".
    - Grants can only be requested for machines in home's list.
  - **Acceptance**:
    - Test with two hosts: phone device on A gets a grant for B; the token works on B's `/api/sessions`; removing the phone on A makes the B token 401.
    - `dotnet test tests/WeaveFleet.Api.Tests --filter "FullyQualifiedName~DeviceGrant"` and `bun run test` pass.

### Phase 4 — Cross-machine "Needs you" inbox

- [ ] 17. Inbox model and per-machine feeds
  - **What**: Pure grouping logic + live feeds for every machine at once.
  - **Files**: `client/src/lib/phone/inbox.ts`, `client/src/lib/phone/__tests__/inbox.test.ts`, `client/src/lib/phone/machine-feed.ts` (framework-free: SignalR connection + poll fallback per machine), `client/src/composables/phone/use-inbox.ts`, `client/src/composables/__tests__/use-inbox.test.ts`
  - **Depends on**: Task 16
  - **Implementation outline**:
    1. `inbox.ts`: input = per-machine session lists + pending asks; output sections `needsYou` (waiting_input, error, workflow waiting — reuse the predicates from `FleetDashboard.vue`; extract them into `client/src/lib/session-row-status.ts` or a new `lib/needs-you.ts` and use from both), `working`, `finished` (recent, ≤ 24 h), sorted by recency; each item carries `machineId`, machine name, status line, ask preview (command or question text).
    2. `machine-feed.ts`: for each machine (home via cookie/same origin; others via grant token, `credentials:"omit"`), open a `@microsoft/signalr` connection with `accessTokenFactory`, subscribe to the `sessions` topic, refetch `GET /api/sessions` on relevant events; if the hub fails, poll every 15 s; expose `status` (`live` | `polling` | `unreachable` + `lastHeardAt`).
    3. Pending asks: `GET /api/sessions/{id}/permissions` for waiting sessions (and the question data the desktop uses) to fill previews and quick actions.
  - **Pitfalls / non-goals**:
    - Do not reuse the singleton `use-signalr-socket.ts`; feeds are independent connections. Close them when the inbox unmounts / app hidden for > 5 min.
  - **Acceptance**:
    - Unit tests for grouping/sorting, unreachable handling, and feed fallback (mocked hub).
    - `bun run test` passes.

- [ ] 18. Phone shell, inbox route and standalone entry
  - **What**: Option A home screen per `mockups/phone-app/index.html`.
  - **Files**: `client/src/routes/phone.tsx`, `client/src/routes/phone.index.tsx`, `client/src/components/phone/PhoneShell.vue`, `client/src/components/phone/InboxPage.vue`, `client/src/components/phone/InboxAskRow.vue`, `client/src/components/phone/InboxSessionRow.vue`, `client/src/components/phone/BottomSheet.vue`, `client/src/components/phone/PhoneTabBar.vue`, `client/src/routes/__root.tsx`, `client/src/routes/index.tsx`
  - **Depends on**: Task 17, Task 6
  - **Implementation outline**:
    1. `/phone*` renders inside `PhoneShell` (header "Fleet · N machines", safe areas, tab bar: Needs you / Sessions / Machines) behind `AuthGate`-equivalent that accepts the device cookie.
    2. Inbox rows: machine + age, title, ask preview, inline **Allow once** (permission) / first two options (question) and **More…** opening a sheet with the full PermissionCard choices (Allow once, Always allow …, Deny with text). Working and Finished sections as in the mockup. Unreachable machines get a dimmed header "unreachable since HH:MM".
    3. Machines tab: list with status, home marked "Home · sends notifications"; if a machine lacks https, show the reason.
    4. `routes/index.tsx`: when `matchMedia('(display-mode: standalone)')` or `?source=pwa` and `useIsMobileNav`, redirect `/` → `/phone`. Desktop behaviour unchanged. Add a "Phone view" link in the existing mobile sidebar for browser (non-installed) users.
    5. Tapping a row → session route (Task 19); if the session's machine differs from the active machine, use `switchToMachine(machineId, "/phone/s/<machineId>/<id>")`.
  - **Pitfalls / non-goals**:
    - Use existing tokens/classes from `client/src/assets/main.css`; run `bun run lint:design`.
    - 44px minimum touch targets.
  - **Acceptance**:
    - Playwright screenshot at 390×844 matches the mockup layout (manual comparison) with a seeded waiting session.
    - Allow once from the inbox resolves the ask (session leaves Needs you).
    - `bun run test`, `bunx vue-tsc --noEmit`, `bun run lint:design` pass.

### Phase 5 — Phone session view with steering

- [ ] 19. Session route, header status, plan bar, folded steps, "since you looked"
  - **What**: The reading half of `mockups/phone-app/session.html`.
  - **Files**: `client/src/routes/phone.s.$machineId.$sessionId.tsx`, `client/src/components/phone/session/PhoneSessionPage.vue`, `PhoneSessionHeader.vue`, `PhonePlanBar.vue`, `FoldedStepsRow.vue`, `StepsSheet.vue`, `SinceYouLookedMarker.vue`, `client/src/lib/phone/fold-steps.ts`, `client/src/lib/phone/__tests__/fold-steps.test.ts`, `client/src/lib/phone/last-seen.ts`, `client/src/lib/phone/__tests__/last-seen.test.ts`
  - **Depends on**: Task 18
  - **Implementation outline**:
    1. Load the stream with `use-session-stream.ts` (active machine = route machine; see Task 18 step 5) and render messages with existing message components where possible.
    2. Header: back, title, "<machine> · Working · 1m 12s | Needs you · 2m | Finished · 6m 40s" (amber for needs you), ⋯ button.
    3. Plan bar from `client/src/lib/session-progress.ts` data ("Plan · 3 of 5 · next …"), tap ⇒ plan sheet.
    4. `fold-steps.ts`: collapse consecutive tool parts of a turn into one row "Read 4 files · edited 1 · ran 1 command"; agent text stays; tap ⇒ `StepsSheet` list; step ⇒ diff/output sheet (read-only). Subagent rows stay as rows that open the child session.
    5. `last-seen.ts`: per `machineId:sessionId` last-seen timestamp in localStorage, updated on leave/hide; render "Since you looked at HH:MM" before the first newer message and scroll there on open (unless deep-linked to an ask).
  - **Pitfalls / non-goals**:
    - No terminal/editor/browser canvas components on this route.
    - Keep reducer logic in `domain-event-reducer.ts`; phone only changes presentation.
  - **Acceptance**:
    - Unit tests for folding (mixed text/tool sequences, subagents) and last-seen marker placement.
    - Screenshot at 390×844 against `session.html` steps 1 and 6.
    - `bun run test`, `bunx vue-tsc --noEmit` pass.

- [ ] 20. Phone dock composer: Send / Queue / Send now / Stop, chips, + menu
  - **What**: The writing half: composer per the session rules.
  - **Files**: `client/src/composables/use-composer-actions.ts` (new; extracted from `Composer.vue`), `client/src/components/session/Composer.vue` (use the composable; no behaviour change), `client/src/components/phone/session/PhoneComposer.vue`, `PhoneChipSheet.vue` (agent/model/effort), `PhonePlusSheet.vue`, `client/src/composables/__tests__/use-composer-actions.test.ts`
  - **Depends on**: Task 19
  - **Implementation outline**:
    1. Extract from `Composer.vue` the non-UI actions: send prompt, queue (`/queue`), send queued now, steer (`canSteer`, `steersByDefault`), abort, shell (`/shell` for `!` input), side question (`/side` for `/btw`), agent/model/effort selection state, image attachments. Desktop `Composer.vue` consumes it; its tests stay green.
    2. `PhoneComposer`: one primary button — **Send** when idle, **Queue** while working, **Stop** while working and the field is empty; **Send now** beside it only when `canSteer`. Queued messages appear above the composer marked "Next" with edit / send now / remove (reuse `QueuedMessages.vue` logic).
    3. Chips (agent, model, effort) shown while the field is focused; each opens a bottom sheet.
    4. + sheet: Photo or screenshot (up to 5, `<input type=file accept=image/* multiple>`), Camera (`capture="environment"`), A file from <machine> (@ picker sheet using existing composer-references), Run a command (prefills `!`), Side question (opens `/btw` sheet). Typing `@`, `!`, `/` still works.
  - **Pitfalls / non-goals**:
    - Refactor must be behaviour-preserving for desktop: run the existing Composer tests and the E2E golden path.
    - Harness differences: if the harness can't steer, hide Send now (don't fake it); Queue is always available.
    - Keyboard: use `visualViewport` to keep the dock above the iOS keyboard.
  - **Acceptance**:
    - Composable tests for queue vs send vs steer decisions per harness type, `!` and `/btw` routing.
    - `bun run test`, `bunx vue-tsc --noEmit` pass; `dotnet test tests/WeaveFleet.E2E --filter "FullyQualifiedName~GoldenPathTests"` still passes.

- [ ] 21. Docked ask: permission and question in place of the composer
  - **What**: When the agent asks, the ask replaces the composer at the bottom.
  - **Files**: `client/src/components/phone/session/DockedPermission.vue`, `DockedQuestion.vue`, `client/src/components/phone/session/PhoneSessionPage.vue`, `client/src/lib/phone/dock-state.ts`, `client/src/lib/phone/__tests__/dock-state.test.ts`
  - **Depends on**: Task 20
  - **Implementation outline**:
    1. `dock-state.ts`: given pending permissions/questions and composer state, choose `composer | permission | question` (permission first, oldest first).
    2. Permission: "Run a command · Needs you", the command block, 44px rows: Allow once / Don't ask again for <pattern> this session / Deny → reveals a text field ("tell the agent what to do instead"); plus "Later: let me read first" (collapses to a pill above the composer). Reuse `use-session-permissions.ts` for replies.
    3. Question: radio rows, "Something else…" text, **Send answer** and **Skip**/**Later**; reuse question APIs used by `QuestionCard.vue`.
    4. After answering, a quiet line "Asked · … → answer ✓" stays in the conversation and the composer returns.
  - **Pitfalls / non-goals**:
    - Same reply payloads as the desktop cards; don't invent new endpoints.
  - **Acceptance**:
    - Unit tests for dock-state transitions.
    - E2E (Task 26) with `QuestionToolTests`-style fake harness answers a question on the phone.
    - `bun run test`, `bunx vue-tsc --noEmit` pass.

- [ ] 22. ⋯ menu, read-only sheets, /btw sheet and "open on computer"
  - **What**: Everything else from the mapping table.
  - **Files**: `client/src/components/phone/session/SessionMenuSheet.vue`, `ChangesSheet.vue`, `FilesSheet.vue`, `PlanSheet.vue`, `SideConversationSheet.vue`, `OpenOnComputerCard.vue`
  - **Depends on**: Task 21
  - **Implementation outline**:
    1. ⋯ menu: Changes (n files), Files, Side question /btw, Fork from here, Rename, Open on my computer, Archive (and Stop while working). Reuse existing API calls used by desktop session header actions.
    2. Changes/Files sheets: read-only diff and file viewer reusing `diff-parser.ts` / session-files API; no editing.
    3. `/btw`: full-height sheet with its own composer bound to `/side` endpoints.
    4. Terminal/editor/preview links in messages → `OpenOnComputerCard` ("Terminals need a keyboard and a wide screen…", offer `!` command, and "Open <machine> on my computer" which copies/shares the desktop URL via `navigator.share` fallback to clipboard).
  - **Pitfalls / non-goals**:
    - No terminal/editor/browser canvas on phone (out of scope).
  - **Acceptance**:
    - Screenshot checks at 390×844 for menu, changes sheet and terminal card vs `session.html`.
    - `bun run test`, `bunx vue-tsc --noEmit` pass.

### Phase 6 — Notification actions, deep links and awkward cases

- [ ] 23. Android action buttons and deep link to the ask
  - **What**: Allow once / Deny from the notification (Android); tapping any notification opens the session at the ask.
  - **Files**: `client/src/lib/push/notification-options.ts`, `client/src/sw/sw.ts`, `client/src/lib/push/answer.ts`, `client/src/lib/push/__tests__/answer.test.ts`, `client/src/components/phone/session/PhoneSessionPage.vue`, `client/src/components/phone/AnsweredPage.vue` (route `client/src/routes/phone.answered.tsx`)
  - **Depends on**: Tasks 8, 11, 16, 21
  - **Implementation outline**:
    1. For `kind === "permission"` with `requestId`, add `actions: [{action:"allow-once", title:"Allow once"}, {action:"deny", title:"Deny"}]` (only if `Notification.maxActions > 0`; iOS gets none).
    2. SW `notificationclick` with an action: look up the machine (`machineId` → home or grant `baseUrl` + token from IndexedDB), `fetch(baseUrl + /api/sessions/{id}/permissions/{requestId}, { method:"POST", headers:{Authorization:"Bearer …"}, credentials:"omit", body })` with the same body the desktop sends for once/reject; on success show a short "Allowed — <machine> carries on" notification (same tag); on failure (409 already answered, network) open the deep link instead.
    3. Deep link `?ask=<requestId>`: session page skips the since-you-looked scroll and opens with the docked ask focused; if already answered, show "Already answered" toast.
    4. Question notifications open the session (or the single-question page from option C if the session view is heavy — use the session view with the docked question).
  - **Pitfalls / non-goals**:
    - `event.waitUntil` around all async work in the SW.
    - Never put the token in the notification `data`.
  - **Acceptance**:
    - Unit tests for action selection and answer request building.
    - Playwright can't click a system notification's action button, so E2E calls `answer.ts` (the same code the SW runs) from the page against a live server with the device token and checks that the permission resolves. Real Android covers the button itself in Task 28.
    - `bun run test` passes.

- [ ] 24. Unreachable machines, held messages and subscription churn
  - **What**: Awkward cases from `session.html`.
  - **Files**: `client/src/lib/phone/outbox.ts`, `client/src/lib/phone/__tests__/outbox.test.ts`, `client/src/components/phone/session/PhoneComposer.vue`, `client/src/components/phone/session/UnreachableBanner.vue`, `client/src/composables/phone/use-push-subscription.ts`
  - **Depends on**: Tasks 20, 12, 17
  - **Implementation outline**:
    1. Machine status from the feed/stream: when unreachable, header shows "<machine> last heard HH:MM", banner "Can't reach <machine>. Trying again in N s.", conversation stays as last heard, status shows "Working when last heard".
    2. `outbox.ts`: messages typed while unreachable are held (localStorage, keyed `machineId:sessionId`, with draft-storage patterns from `client/src/lib/draft-storage.ts`), shown as "Held · … · Edit", sent in order when the machine answers; never silently dropped; failures stay held with a retry.
    3. Subscription churn: on app open and on `visibilitychange` to visible, verify `pushManager.getSubscription()` matches what the server has; re-PUT if not; if permission was revoked, show a banner linking to `/phone/setup`.
  - **Pitfalls / non-goals**:
    - Don't queue permission answers offline — they're time-sensitive; show an error instead.
  - **Acceptance**:
    - Outbox unit tests (hold, order, retry, edit, delete).
    - Manual/E2E: stop the server, type, restart, message is delivered.
    - `bun run test` passes.

### Phase 7 — Docs, E2E, security, verification

- [ ] 25. Docs: `docs/phone.md`, update `docs/machines.md`
  - **What**: User and contract documentation.
  - **Files**: `docs/phone.md` (new), `docs/machines.md`, `README.md` (link only, if it lists docs)
  - **Depends on**: Tasks 1–24
  - **Implementation outline**:
    1. `docs/phone.md`: requirements (Tailscale + `tailscale serve` HTTPS + `--require-token`, HTTPS certificates on), pairing (QR / manual code), iOS install → notifications, Android actions, home machine and what happens when it's down, quiet-at-desk, plain-LAN-HTTP limitations, removing a phone, troubleshooting (no notifications: permission, subscription, home down, iOS focus modes).
    2. `docs/machines.md`: replace "There are no scopes and no per-device tokens yet" with the device token contract (format opaque to clients, scopes, 30-day expiry, `POST /api/machine/devices`, pairing endpoints, QR payload v1), `GET /api/machine` `publicUrl`, `/api/machines` server-side list + migration, `/api/push/*`, `session_notification` new fields, note that the machine-token and CORS rules are unchanged.
  - **Pitfalls / non-goals**:
    - Keep the doc's plain style; no marketing.
  - **Acceptance**:
    - Every new endpoint in this plan appears in `docs/machines.md`.

- [ ] 26. End-to-end tests (phone viewport) and opt-in Web Push E2E
  - **What**: Full-stack coverage of pairing, inbox and session view.
  - **Files**: `tests/WeaveFleet.E2E/Tests/PhonePairingTests.cs`, `tests/WeaveFleet.E2E/Tests/PhoneInboxTests.cs`, `tests/WeaveFleet.E2E/Tests/PhoneSessionTests.cs`, `tests/WeaveFleet.E2E/Tests/WebPushTests.cs`, `tests/WeaveFleet.E2E/Infrastructure/*` (phone context helper: viewport 390×844, `isMobile`, `hasTouch`)
  - **Depends on**: Tasks 1–24
  - **Implementation outline**:
    1. Pairing: desktop context creates a code via UI, phone context opens the QR URL, connects, lands signed in; Settings lists the device; Remove → phone gets 401 and is sent to `/pair`.
    2. Inbox: seed a waiting permission with the fake harness; phone inbox shows it; Allow once resolves it.
    3. Session: queue a message while working, Stop, answer a docked question, `!` command output appears.
    4. Device scope: phone context calling `/api/machine/access` gets 403.
    5. `WebPushTests` (`[Trait("Category","PushE2E")]`, skipped unless `FLEET_PUSH_E2E=1`): run headed Chromium under `xvfb-run -a`, enable notifications permission, subscribe (allow 20–90 s for `pushManager.subscribe`; endpoint is `https://…google.com/fcm/send/…`), trigger a needs-you, poll `registration.getNotifications()` in the SW for up to 90 s.
  - **Pitfalls / non-goals**:
    - Build the frontend first (`cd client && bun install && bun run build`), then iterate with `-p:SkipFrontendBuild=true`.
    - Push E2E needs network to FCM; never make it part of the default `Category=E2E` run.
  - **Acceptance**:
    - `dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"` passes, including the new phone tests.
    - `FLEET_PUSH_E2E=1 xvfb-run -a dotnet test tests/WeaveFleet.E2E --filter "Category=PushE2E"` passes locally.

- [ ] 27. Security audit (before merge)
  - **What**: Have the security auditor (warp) review auth, tokens, pairing, push and CORS; fix every blocking finding.
  - **Depends on**: Tasks 1–26
  - **Implementation outline**:
    1. Ask for review of: `BearerTokenHandler` changes, device token format/hash/compare/expiry, cookie `OnValidatePrincipal`, `MachineOwner` policy coverage (every owner-only route, agents excluded), pairing secrets (fragment only, never logged, single use, TTL, rate limits, manual-code brute force), `/api/machines` outbound requests (SSRF, timeouts), device grants and cascade revocation, VAPID private key file permissions, push payload contents (no secrets/PII beyond title), SW use of tokens (IndexedDB, `credentials:"omit"`), CORS (`MachineCorsPolicyProvider` unchanged: token requests any origin, never credentials; `?access_token` still limited to hubs/WS).
    2. Grep logs and request logging for secret leakage (`access_token`, `fdt_`, `secret`, `endpoint`).
  - **Pitfalls / non-goals**:
    - Not optional: merge is blocked until the verdict is approve.
  - **Acceptance**:
    - Auditor verdict "approve"; findings and fixes recorded in the PR description.

- [ ] 28. Final verification
  - **What**: Run everything and do a real-device smoke test.
  - **Depends on**: Task 27
  - **Implementation outline**:
    1. Run the commands in `## Verification`.
    2. Real devices over `tailscale serve`: iPhone (Safari → pair → Add to Home Screen → notifications → tap opens the ask) and Android Chrome (install → Allow once from the notification). Two machines: notification from the non-home machine arrives.
  - **Pitfalls / non-goals**:
    - Record any iOS storage-split behaviour observed during pairing in `docs/phone.md`.
  - **Acceptance**:
    - All commands green; both device smoke tests pass.

## Progress

### Phase 1 — done (2026-10-05)
Shipped: devices table (migration **051**, since 048–050 were taken on main), `DeviceTokenService`, device tokens in
`BearerTokenHandler` + `/auth/token-login`, cookie re-validation, `MachineOwner` policy, pairing codes and endpoints,
`publicUrl`, Settings "Add a phone" + "Devices with access", `/pair` and device credentials. Live-checked on a
scratch Fleet (127.0.0.1 + RequireToken, as behind `tailscale serve`): desktop makes a code, a 390×844 phone context
opens the QR URL, connects, lands signed in as a device (403 on `/api/machine/access`), the device shows on the
desktop, a second redeem is 404, and Remove makes the phone's next request 401.

Differences from the plan (code won, or a detail the plan left open):
- The branch was rebased onto `origin/main` before starting (49 commits behind, including migrations 048–050).
- Device **cookies** carry `fleet_scope=device` + `fleet_device` but **not** `amr=token`, like the owner's
  token-login cookie: `amr=token` lets a terminal socket open from any origin, which only a presented token may do.
  Bearer device tokens do get `amr=token`.
- `PUT /api/machine`: a field left out (`null`) now stays as it is; an empty string resets it. Before, a missing
  `name` reset the name, which would have happened on every `publicUrl` save.
- The typed-code limit (5/min) is a `ManualCodeAttempts` singleton checked in the endpoint, beside the
  `AddRateLimiter` policy (10/min) on `/api/pairing/*`; the limiter can't see the body to tell the two apart.
- "Add a phone" asks for the phone's address first when the page is on loopback and no `publicUrl` is saved.
- Deferred: nothing.

## Risks and unknowns
- **iOS**: no notification action buttons (tap → deep link only); push only for Home Screen apps on iOS 16.4+; permission request must be on a user gesture; Safari and Home Screen app storage may be separate → manual pairing code fallback (Task 6). Focus modes can silence pushes.
- **Home machine single point of failure**: if home is down, no notifications for any machine; inbox shows remotes as unreachable. Documented; a push gateway is the future fix.
- **Subscription churn**: browsers rotate/expire subscriptions; handled by `pushsubscriptionchange`, re-check on open, 404/410 cleanup, failure counter.
- **Harness differences**: only some harnesses steer mid-turn (`steersByDefault` is opencode2-only today); the UI hides Send now when unsupported. Question/permission shapes differ per harness — reuse the desktop cards' API paths.
- **Chrome Local Network Access**: not relevant — each machine serves its own PWA from its own origin and remotes are reached over tailnet HTTPS with tokens. Revisit only if a hosted origin is introduced.
- **Mixed content**: remotes listed with `http://` URLs are unreachable from an `https://` phone page; the UI says so.
- **Older remote machines**: before Task 11 they broadcast `session_notification` only when their desktop preference is on and without `kind`; the watcher maps them. Document "upgrade all machines".
- **WebPush library**: confirm the pinned `WebPush` version sends aes128gcm (older versions used aesgcm); if not, upgrade or set the content encoding explicitly.
- **Composer extraction** (Task 20) is the riskiest refactor; keep it behaviour-preserving and covered by existing tests.

## Verification
From the repo root unless noted:

```sh
# .NET
dotnet build
dotnet test tests/WeaveFleet.Api.Tests
dotnet test tests/WeaveFleet.Application.Tests
dotnet test tests/WeaveFleet.Infrastructure.Tests
dotnet test tests/WeaveFleet.IntegrationTests -c Debug --filter "FullyQualifiedName~SignalREventContractTests|FullyQualifiedName~RemoteMachineWatcher"

# Client (in client/)
bun install
bun run test
bunx vue-tsc --noEmit
bun run lint
bun run lint:design
bun run build            # dist/sw.js and dist/manifest.webmanifest exist, sw.js has no hash

# E2E (needs the frontend build)
dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"
# Opt-in Web Push (network to FCM; subscribe can take 20–90 s)
FLEET_PUSH_E2E=1 xvfb-run -a dotnet test tests/WeaveFleet.E2E --filter "Category=PushE2E"
```

Passing looks like: all test runs report 0 failed; `vue-tsc` prints nothing; Playwright phone screenshots (390×844) match `mockups/phone-app/index.html` option A and `mockups/phone-app/session.html`; the security auditor approves; real iPhone and Android smoke tests in Task 28 pass.
