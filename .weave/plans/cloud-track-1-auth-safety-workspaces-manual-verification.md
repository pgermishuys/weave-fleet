# Cloud Track 1 — Manual Testing and Verification

This note captures the manual verification still required for `cloud-track-1-auth-safety-workspaces`.

## What automated coverage exists today

### Covered by automated tests
- API auth behavior:
  - authenticated `GET /api/user/me` returns `200`
  - unauthenticated `GET /api/user/me` returns `401`
  - CSRF-protected endpoints reject missing token
  - logout requires CSRF token
  - unsafe external login `returnUrl` is normalized back to `/`
- Application/infrastructure tenancy behavior:
  - user-scoped entity creation
  - managed workspace creation rules
  - repository ownership guards
  - user-scoped event delivery
- Browser E2E:
  - app loads and main flows still work in local/test mode
  - session creation and navigation still work after auth/tenancy changes
  - archived-session fork/new-context flow still navigates to a fresh session

### Not covered by automated tests
- A real OIDC / Clerk round-trip against an actual identity provider
- Browser sign-in flow with real redirects, callback handling, and real auth cookies
- Multi-user browser validation against two real authenticated users
- Production-like cloud deployment behavior behind a real origin / TLS / reverse proxy

## Important answer: are we testing a real IDP flow today?

No.

Current automated auth tests use app-hosted test authentication / direct authenticated test setup. They validate Fleet's auth enforcement and cookie/CSRF behavior, but they do **not** validate a real external identity provider login round-trip.

There is currently **no end-to-end browser test** that signs in through a real Clerk/OIDC provider.

## Recommended manual verification checklist

## 1. Environment setup
- Deploy/run Fleet in cloud mode with:
  - `Fleet:Auth:Enabled=true`
  - `Fleet:Cloud:Enabled=true`
  - valid OIDC/Clerk `Authority`
  - valid `ClientId`
  - valid `ClientSecret`
  - correct callback URL registered at the IdP
  - correct logout callback URL registered at the IdP
  - correct allowed browser origin in `Fleet:Auth:AllowedOrigins`
- Use HTTPS and the real host name you expect users to access.
- Prepare **two separate test users** in the IdP.
- Start with a fresh database if possible.

## 2. Real sign-in / sign-out verification

### Expected outcome
Fleet should redirect to the real IdP, complete login, return to Fleet, and establish an authenticated browser session cookie.

### Steps
1. Open Fleet in a clean browser profile.
2. Confirm you are not already signed in.
3. Attempt to access a protected page or use the UI normally.
4. Trigger sign-in.
5. Verify browser redirect to the real IdP.
6. Complete login with test user A.
7. Verify redirect back to Fleet after the callback.
8. Verify Fleet loads successfully after login.
9. Open browser dev tools and confirm:
   - no bearer token is present in URL query strings
   - no bearer token is stored in localStorage/sessionStorage for Fleet auth
   - auth is cookie-based
10. Call `GET /api/user/me` from the browser/dev tools and confirm the correct user identity is returned.
11. Trigger logout.
12. Verify logout completes and protected Fleet pages are no longer accessible without re-authentication.

### Things to watch for
- redirect loop
- callback path mismatch
- wrong post-login redirect target
- cookies missing / immediately expiring
- CSRF failures after apparently successful login

## 3. Cloud-mode protected endpoint verification

### Expected outcome
Protected API routes reject unauthenticated requests.

### Steps
1. In a clean unauthenticated session, try to hit:
   - `/api/user/me`
   - `/api/sessions`
   - `/api/projects`
   - `/api/workspaces`
2. Confirm they do not behave like public endpoints.
3. Confirm health/public endpoints still behave appropriately.
4. After login, retry the protected endpoints and confirm they succeed.

## 4. CSRF verification

### Expected outcome
State-changing browser requests should require valid CSRF context/token.

### Steps
1. Sign in as user A.
2. Perform normal state-changing UI actions:
   - update config
   - create session
   - archive/unarchive session
   - delete session
3. Confirm normal UI actions succeed.
4. Using dev tools or a manual HTTP client that reuses only auth cookies but omits the CSRF token/header, attempt a state-changing request.
5. Confirm the request is rejected.

## 5. Managed workspace verification

### Expected outcome
In cloud mode, users should not be supplying arbitrary host directories for normal session creation. Fleet should create managed workspace directories under the configured workspace root.

### Steps
1. Sign in as user A.
2. Create a new session normally.
3. Confirm the session succeeds.
4. Inspect the created workspace on disk or via logs/admin access.
5. Verify the workspace path is under the configured cloud workspace root.
6. Verify it is user-scoped and workspace-scoped.
7. Attempt to create a session by forcing a raw directory path where that is not allowed in cloud mode.
8. Confirm the request is rejected.

## 6. Multi-user isolation verification

### Expected outcome
Each user should only see and affect their own data.

### Steps
1. Open browser profile A and sign in as user A.
2. Create:
   - at least one project
   - at least one session
   - at least one workspace/workspace root if exposed in UI
3. Open browser profile B and sign in as user B.
4. Confirm user B does **not** see user A's sessions/projects/workspaces.
5. Create equivalent data as user B.
6. Return to profile A and confirm user A does **not** see user B's data.
7. Attempt deep-link access from user B into a user A session URL if known.
8. Confirm access is denied or the resource is hidden.
9. Verify live updates/events only appear for the currently signed-in owner's resources.

## 7. Forking / delegation / callback safety verification

### Expected outcome
Cross-user child-session linking or callback effects should not occur.

### Steps
1. As user A, create a session and exercise delegation / fork / new context window flows.
2. Confirm normal same-user behavior works.
3. Attempt to trigger or replay equivalent callback/delegation identifiers from user B context if you have admin/test tooling.
4. Confirm cross-user linkage is rejected.

## 8. Browser/network safety verification

### Expected outcome
No bearer token leakage in URLs or logs.

### Steps
1. Perform login, session creation, websocket/SSE activity, and logout.
2. Inspect:
   - browser address bar
   - network request URLs
   - websocket URLs
   - SSE URLs
   - server logs
3. Confirm bearer tokens are not present in query strings or request URLs.

## 9. Plugin / GitHub follow-up validation

These are currently known follow-up concerns and should be tested explicitly:
- backend plugin endpoints should require appropriate auth in cloud mode
- GitHub/plugin integration state should eventually be verified as per-user, not global

For now, manually verify:
1. unauthenticated access to plugin/integration routes is not allowed where it should be protected
2. user B cannot see or reuse user A's plugin/integration state

## Recommended evidence to capture
- screenshots of login, post-login, logout, and unauthorized responses
- network captures showing cookie-based auth and absence of bearer tokens in URLs
- logs or filesystem evidence showing managed workspaces under the cloud root
- screenshots from two-user isolation checks

## Suggested future automation
- Add a dedicated real-browser auth E2E path for sign-in that can run against a test OIDC provider or Clerk sandbox tenant.
- Add authenticated multi-user E2E scenarios with two isolated browser contexts.
- Add protected plugin route tests once plugin auth scope is finalized.
