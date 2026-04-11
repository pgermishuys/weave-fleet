# Duende IdP + Auth E2E Pipeline

## TL;DR
> **Summary**: Create a Duende IdentityServer test IdP project, get manual browser sign-in working at `auth.dev.localhost`, wire Playwright E2E tests for authenticated flows, and add onboarding refresh regression coverage — all without Aspire, using direct-process orchestration suitable for Loom/Tapestry execution.
> **Estimated Effort**: Large

## Context

### Original Request
Build an execution plan in four phases: (1) create a Duende IdentityServer test IdP project, (2) get manual browser sign-in working at `auth.dev.localhost`, (3) wire Playwright for authenticated E2E, (4) add onboarding refresh regression coverage. Constraints: proper OIDC-compliant Duende host; no Aspire; direct-process startup first; Loom/Tapestry-suitable task decomposition.

### Key Findings

**Existing E2E infrastructure is mature:**
- `FleetWebApplicationFactory` boots real Kestrel on `http://127.0.0.1:0` (ephemeral port), replaces `IHarness` with `TestHarness`, uses isolated per-test SQLite.
- `PlaywrightFixture` launches headless Chromium with `HEADED=1` toggle.
- `E2ETestBase` provides per-test browser context, tracing, screenshot/trace capture on failure.
- 9 existing test classes, 5 page objects — all run with `Auth.Enabled = false`.

**Production auth flow (Program.cs):**
- When `Auth.Enabled = true`: cookie auth (default scheme) + OIDC challenge scheme.
- `CookieSecurePolicy.Always` + CSRF `Secure = true` → **HTTPS mandatory for browser tests**.
- `AuthGate` (client SPA) calls `GET /api/config/client` + `GET /api/user/me`; on 401 → redirects to `/auth/login`.
- `OnboardingGate` shows wizard when `authEnabled && cloudMode && !onboardingStatus.completed`.
- Onboarding flow: Welcome → Credential → Ready → `POST /api/user/me/complete-onboarding`.
- Onboarding state is entirely in-memory (React `useState`); a browser refresh during onboarding re-evaluates `OnboardingGate` from server state — if `complete-onboarding` wasn't POSTed yet, wizard reappears.

**API test auth pattern exists but bypasses real OIDC:**
- `ApiWebApplicationFactory` has `TestAuthHandler` (fake claims) and `UnauthorizedAuthHandler`.
- Tests authorization logic but not browser-visible OIDC flows.

**Prior plans exist but chose custom IdP over Duende:**
- `.weave/plans/oidc-e2e-test-infrastructure.md` — detailed 312-line plan for custom ~300-line OIDC IdP.
- `.weave/plans/auth-e2e-harness-architecture-review.md` — architecture review favoring in-process custom IdP.
- Both plans cited Duende license concerns. The current request explicitly asks for Duende, so we use it.

**No Docker/Aspire in the repo today.** Zero Dockerfiles, Testcontainers, or Aspire projects. Pure .NET 10 + Bun SPA.

**Tech stack:** .NET 10, C# 14, `TreatWarningsAsErrors`, central package management (`Directory.Packages.props`), xUnit 2.9, Playwright 1.51, Shouldly 4.2.

### Architecture Decision: Duende IdentityServer as Separate Test Project

Unlike the prior plans that proposed a custom ~300-line OIDC IdP, this plan uses **Duende IdentityServer** as a proper OIDC-compliant host. Reasons:

1. **User explicitly requested Duende** — overrides prior license-avoidance reasoning.
2. **OIDC compliance out of the box** — discovery, JWKS, PKCE, nonce/state round-trip, userinfo, end-session all handled by a production-tested library.
3. **Reduces custom code** — no need to hand-roll 5 OIDC endpoints, JWT signing, or discovery metadata.
4. **Duende Community Edition** — free for dev/testing (license key not required for local dev). The project is a test infrastructure project, not shipped to production.

**Topology:**

```
Browser (Chromium / manual)
    │  HTTPS
    ├──► auth.dev.localhost:{idp-port}  ← Duende IdentityServer
    │       ├─ /.well-known/openid-configuration
    │       ├─ /connect/authorize  (renders login UI)
    │       ├─ /connect/token
    │       ├─ /connect/userinfo
    │       └─ /connect/endsession
    │
    └──► app.dev.localhost:{fleet-port} ← Fleet (Auth.Enabled=true)
            ├─ /auth/login       → OIDC challenge → redirect to IdP
            ├─ /auth/callback    → code exchange → cookie
            ├─ /api/user/me      → authenticated user info
            └─ /api/user/me/complete-onboarding
```

**Hostname strategy:**
- `auth.dev.localhost` and `app.dev.localhost` resolve to `127.0.0.1` natively on modern browsers (`.localhost` is a reserved TLD per RFC 6761 — Chromium and Firefox resolve `*.localhost` to `127.0.0.1` without hosts-file edits).
- This gives proper cross-origin cookie semantics and makes OIDC `redirect_uri` validation realistic.
- For CI (Ubuntu): `*.localhost` resolution works natively on modern Linux. If not, add `/etc/hosts` entries in CI workflow.

**HTTPS strategy:**
- Programmatic in-memory certs via `System.Security.Cryptography` (same as prior plan).
- Self-signed CA → leaf cert with SAN: `DNS:auth.dev.localhost, DNS:app.dev.localhost, IP:127.0.0.1`.
- Kestrel HTTPS binding for both IdP and Fleet.
- Playwright: `IgnoreHTTPSErrors = true` per browser context.
- OIDC backchannel: custom `ServerCertificateCustomValidationCallback` trusting the test CA.

## Objectives

### Core Objective
Stand up a Duende IdentityServer test IdP, prove manual browser sign-in, wire Playwright E2E tests for the auth pipeline, and add regression coverage for the onboarding-refresh edge case — forming a complete auth E2E test infrastructure.

### Deliverables
- [ ] New `tests/WeaveFleet.IdP/` project — Duende IdentityServer host with test users, test client registration, and login UI
- [ ] Manual browser verification: navigate to `https://app.dev.localhost:{port}/` → redirect to IdP → sign in → redirected back with cookie → dashboard visible
- [ ] `AuthFleetWebApplicationFactory` that boots Fleet with `Auth.Enabled=true`, HTTPS, and OIDC pointed at the Duende IdP
- [ ] `AuthE2ETestBase` with `LoginAsync()` / `AssertAuthenticatedAsync()` helpers
- [ ] Playwright E2E tests for: sign-in flow, sign-out flow, CSRF lifecycle, authenticated API access
- [ ] Onboarding refresh regression test: sign in → see wizard → browser refresh mid-wizard → wizard re-renders correctly → complete → refresh → wizard does not reappear

### Definition of Done
- [ ] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=AuthE2E" --configuration Release` passes
- [ ] Existing non-auth E2E tests pass unchanged
- [ ] `dotnet build WeaveFleet.slnx --configuration Release` compiles with zero warnings
- [ ] No production source files have `Secure` or `SecurePolicy` changes
- [ ] CI workflow runs auth E2E tests on Ubuntu

### Guardrails (Must NOT)
- Must NOT use Aspire for orchestration
- Must NOT modify `CookieSecurePolicy.Always` or `Secure = true` on CSRF cookies in production code
- Must NOT add test-only backdoor auth schemes to `Program.cs`
- Must NOT require Docker, external IdP, or internet access to run tests
- Must NOT break existing local-mode E2E tests

## TODOs

### Phase 1: Duende IdentityServer Test Project

- [x] 1. Create `tests/WeaveFleet.IdP/` Project
  **What**: Create a new ASP.NET Core project (`Microsoft.NET.Sdk.Web`) that hosts Duende IdentityServer. Configure in-memory clients, API scopes, identity resources, and test users. Register a single OIDC client (`fleet-test-client`) with authorization code flow, client secret, and redirect URIs matching `https://app.dev.localhost:{port}/auth/callback`. Add 2-3 test users (e.g., `test-user-1` / `test@example.com`, `new-user` / `new@example.com` for onboarding tests). Include a minimal Razor/HTML login page that Playwright can interact with (username + password + submit button with `data-testid` attributes). The IdP should also expose a logout/end-session page.
  **Files**: `tests/WeaveFleet.IdP/WeaveFleet.IdP.csproj`, `tests/WeaveFleet.IdP/Program.cs`, `tests/WeaveFleet.IdP/Config.cs`, `tests/WeaveFleet.IdP/Pages/Login.cshtml`, `tests/WeaveFleet.IdP/Pages/Login.cshtml.cs`, `tests/WeaveFleet.IdP/Pages/Logout.cshtml`, `tests/WeaveFleet.IdP/Pages/Logout.cshtml.cs`
  **Acceptance**: `dotnet build tests/WeaveFleet.IdP/` compiles. `dotnet run --project tests/WeaveFleet.IdP/` starts and serves valid `/.well-known/openid-configuration` JSON at `https://localhost:{port}`.

- [x] 2. Add IdP Project to Solution
  **What**: Add `tests/WeaveFleet.IdP/WeaveFleet.IdP.csproj` to `WeaveFleet.slnx` under the `/tests/` folder. Add Duende IdentityServer NuGet package version to `Directory.Packages.props` (central package management). Ensure the E2E project references the IdP project so it can start the IdP programmatically.
  **Files**: `WeaveFleet.slnx`, `Directory.Packages.props`, `tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj`
  **Acceptance**: `dotnet build WeaveFleet.slnx --configuration Release` succeeds with zero warnings.

- [x] 3. Create `TestCertificateAuthority` — Programmatic TLS
  **What**: Build a utility class generating: (a) a self-signed CA cert (RSA 2048, `BasicConstraints(CA=true)`), (b) a leaf cert signed by the CA with SAN: `DNS:auth.dev.localhost, DNS:app.dev.localhost, IP:127.0.0.1`. Expose the leaf cert as `X509Certificate2` for Kestrel HTTPS. Expose the CA cert for custom `ServerCertificateCustomValidationCallback`. All in-memory — no filesystem, no `dotnet dev-certs`. Use `CertificateRequest`, `RSA.Create(2048)`, `SubjectAlternativeNameBuilder`. Cert validity: 1 day (test-only). The class should be `sealed` and `internal` per C# standards.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/TestCertificateAuthority.cs`
  **Acceptance**: Unit test creates certs, binds a Kestrel endpoint with HTTPS, makes a request using the custom callback → succeeds.

- [x] 4. Create `IdpProcessHost` — Direct-Process IdP Launcher
  **What**: Build a host launcher that starts the Duende IdP as a direct in-process `WebApplication` (not a separate OS process). Key implementation: call `WebApplication.CreateBuilder()` with the IdP's configuration, configure Kestrel to listen on `https://auth.dev.localhost:0` with the leaf cert from `TestCertificateAuthority`, start the host, extract the bound port. Expose `string Authority` (e.g., `https://auth.dev.localhost:5401`). Implement `IAsyncDisposable` for clean shutdown. Support dynamic client `redirect_uri` registration — since the Fleet port isn't known until Fleet starts, either: (a) configure the Duende client with a wildcard `redirect_uri` pattern (`https://app.dev.localhost:*/auth/callback`), or (b) configure the client after Fleet's port is known via Duende's mutable in-memory store. Option (a) is simpler for test-only usage.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/IdpProcessHost.cs`
  **Acceptance**: `IdpProcessHost` starts, `Authority` property returns a valid HTTPS URL, HTTP GET to `{Authority}/.well-known/openid-configuration` returns valid OIDC metadata.

### Phase 2: Manual Browser Sign-In at auth.dev.localhost

- [x] 5. Create `AuthFleetWebApplicationFactory` — Auth-Enabled Fleet Host
  **What**: Create a variant of `FleetWebApplicationFactory` that boots Fleet with `Auth.Enabled = true`, HTTPS on `https://app.dev.localhost:0`, and OIDC configured to point at the `IdpProcessHost`. Key configuration: (a) Start `IdpProcessHost` first → get Authority URL, (b) configure Fleet via `builder.UseSetting()`: `Fleet:Auth:Enabled=true`, `Fleet:Auth:Authority={idpHost.Authority}`, `Fleet:Auth:ClientId=fleet-test-client`, `Fleet:Auth:ClientSecret=test-secret`, `Fleet:Auth:AllowedOrigins:0=https://app.dev.localhost:{fleet-port}`, `Fleet:Cloud:Enabled=true` (needed for onboarding), `Fleet:Cloud:WorkspaceRoot={tempDir}`, (c) configure Kestrel HTTPS with the leaf cert, (d) inject custom `BackchannelHttpHandler` into OIDC options that trusts the test CA, (e) extract Fleet's bound port after startup and set `AllowedOrigins` (may require PostConfigure or startup sequencing). The factory should own the lifecycle of both IdP and Fleet hosts.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/AuthFleetWebApplicationFactory.cs`
  **Acceptance**: Factory starts both IdP and Fleet. Navigating to `https://app.dev.localhost:{port}/api/user/me` returns 401. Navigating to `https://app.dev.localhost:{port}/auth/login` redirects to `https://auth.dev.localhost:{idp-port}/connect/authorize?...`.

- [x] 6. Manual Browser Verification Smoke Test
  **What**: Create a minimal xUnit test (tagged `Category=ManualVerification`) that starts `AuthFleetWebApplicationFactory`, prints the Fleet URL and IdP URL to the test output, and waits for a keypress (or configurable timeout). This lets a developer run the test in headed mode (`HEADED=1`) and manually walk through the sign-in flow in the browser to verify: (a) navigate to Fleet → see redirect to IdP login, (b) enter test credentials → submit, (c) get redirected back to Fleet with auth cookie → see dashboard. This test is NOT meant for CI — it's a developer verification aid. Mark with `[Fact(Skip = "Manual verification only")]` or use an environment variable gate.
  **Files**: `tests/WeaveFleet.E2E/Tests/ManualAuthVerificationTest.cs`
  **Acceptance**: Running with `HEADED=1 MANUAL_AUTH=1 dotnet test --filter ManualAuthVerification` opens a browser, shows the Fleet login flow end-to-end.

### Phase 3: Wire Playwright for Authenticated E2E

- [x] 7. Create `AuthE2ETestBase` — Base Class for Auth E2E Tests
  **What**: Create a base class analogous to `E2ETestBase` but using `AuthFleetWebApplicationFactory`. Key differences: (a) browser context created with `IgnoreHTTPSErrors = true`, (b) `LoginAsync(string username, string password)` helper that navigates to a protected page, waits for redirect chain (Fleet → IdP login), fills credentials on the IdP login page, submits, waits for redirect back to Fleet with cookie set, (c) `AssertAuthenticatedAsync()` helper that calls `Page.GotoAsync("{ServerUrl}/api/user/me")` and asserts 200, (d) `LogoutAsync()` helper that POSTs to `/auth/logout` with CSRF token. Inherits trait `[Trait("Category", "AuthE2E")]` and also `[Trait("Category", "E2E")]`. Use `sealed` classes for concrete test classes.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/AuthE2ETestBase.cs`
  **Acceptance**: Base class compiles and provides `LoginAsync` / `LogoutAsync` / `AssertAuthenticatedAsync`.

- [x] 8. Create IdP Login Page Object
  **What**: Create a Playwright page object for the Duende IdP login page. The Duende quickstart UI renders a standard form with username/password inputs and a submit button. Map these with `data-testid` selectors added to the IdP's Razor pages (Task 1). Methods: `WaitForVisibleAsync()`, `FillUsernameAsync(string)`, `FillPasswordAsync(string)`, `SubmitAsync()`, `WaitForRedirectToFleetAsync()`. If Duende's login page renders a consent screen, add `GrantConsentAsync()`.
  **Files**: `tests/WeaveFleet.E2E/Pages/IdpLoginPage.cs`
  **Acceptance**: Page object compiles and selectors match the HTML rendered by the Duende IdP login page.

- [x] 9. Implement OIDC Sign-In / Sign-Out E2E Tests
  **What**: Create test class covering: (a) **Unauthenticated → sign in → dashboard**: navigate to `/` → redirected to IdP → fill credentials → submit → redirected back → dashboard visible, (b) **Authenticated `/api/user/me`**: after sign-in, verify the endpoint returns correct userId, email, displayName, (c) **CSRF token lifecycle**: after sign-in, verify `.WeaveFleet.CSRF` cookie is set on GET, and a POST without the `X-CSRF-Token` header returns 400, (d) **Sign-out → 401**: POST `/auth/logout` with CSRF → cookie cleared → `/api/user/me` returns 401. Use `WithFailureCapture` pattern from existing tests for artifact collection.
  **Files**: `tests/WeaveFleet.E2E/Tests/OidcSignInTests.cs`
  **Acceptance**: `dotnet test --filter "FullyQualifiedName~OidcSignInTests"` — all 4 tests pass.

- [x] 10. Implement `returnUrl` Deep-Link Test
  **What**: Test that `/auth/login?returnUrl=/sessions/test-123` flows through the OIDC challenge and, after successful sign-in, the browser lands on `/sessions/test-123` (SPA route). This exercises the `NormalizeReturnUrl` logic in `AuthEndpoints.cs` and the OIDC `RedirectUri` → `AuthenticationProperties.RedirectUri` round-trip.
  **Files**: `tests/WeaveFleet.E2E/Tests/OidcSignInTests.cs` (additional test method in same class)
  **Acceptance**: Test passes — after sign-in, URL contains `/sessions/test-123`.

### Phase 4: Onboarding Refresh Regression Coverage

- [x] 11. Implement Onboarding Flow E2E Tests
  **What**: Create test class for the onboarding wizard. Preconditions: `Auth.Enabled = true`, `Cloud.Enabled = true`, user has NOT completed onboarding. Tests: (a) **New user sees wizard**: sign in as `new-user` (no prior onboarding) → OnboardingGate triggers → wizard dialog is visible, (b) **Wizard step progression**: Welcome → click "Get Started" → Credential step visible → skip or enter key → Ready step → click "Start a Session" → wizard closes → `complete-onboarding` POSTed, (c) **Completed user skips wizard**: sign in as a user who already completed onboarding → wizard does not appear → dashboard renders directly.
  **Files**: `tests/WeaveFleet.E2E/Tests/OnboardingFlowTests.cs`, `tests/WeaveFleet.E2E/Pages/OnboardingWizardPage.cs`
  **Acceptance**: All 3 tests pass with `dotnet test --filter "FullyQualifiedName~OnboardingFlowTests"`.

- [x] 12. Implement Onboarding Refresh Regression Test
  **What**: The critical regression scenario: a browser refresh (F5) during the onboarding flow should not break the wizard or cause the user to get stuck. Specific test cases: (a) **Refresh mid-wizard**: sign in → wizard appears at Welcome step → click "Get Started" → at Credential step → `Page.ReloadAsync()` → wizard reappears (because `complete-onboarding` hasn't been POSTed yet) → user can continue through wizard → complete → wizard dismisses, (b) **Refresh after completion**: complete the wizard → `Page.ReloadAsync()` → wizard does NOT reappear (server state has `onboarding_completed_at` set), (c) **Refresh on Welcome step**: wizard at Welcome → `Page.ReloadAsync()` → wizard reappears at Welcome (not at a broken state). These tests verify that the `OnboardingGate` correctly re-evaluates `onboardingStatus.completed` from the server after a full page reload.
  **Files**: `tests/WeaveFleet.E2E/Tests/OnboardingRefreshRegressionTests.cs`
  **Acceptance**: All 3 regression tests pass.

### Phase 5: CI & Polish

- [x] 13. Update CI Workflow for Auth E2E Tests
  **What**: Update `.github/workflows/ci.yml` to include auth E2E tests in the existing Playwright E2E job. No separate job needed — auth E2E tests use the same Playwright/Chromium setup. Ensure the test filter includes both `Category=E2E` and `Category=AuthE2E`. Add `/etc/hosts` entries if `*.dev.localhost` doesn't resolve on CI (test with `getent hosts auth.dev.localhost`). Auth E2E tests don't need special cert trust (in-memory certs + `IgnoreHTTPSErrors`). Verify the Duende NuGet package is restored in the NuGet cache step.
  **Files**: `.github/workflows/ci.yml`
  **Acceptance**: CI pipeline runs auth E2E tests on Ubuntu and passes.

- [x] 14. Add `System.IdentityModel.Tokens.Jwt` to `Directory.Packages.props` (if not already covered by Duende transitive deps)
  **What**: Duende IdentityServer may bring in JWT dependencies transitively. Verify and add any missing package versions to central package management. Also add the Duende IdentityServer package version.
  **Files**: `Directory.Packages.props`
  **Acceptance**: `dotnet restore WeaveFleet.slnx` succeeds with all packages resolved from central management.

## Implementation Order

```
Phase 1 — Duende IdP Foundation (Tasks 1-4)
  ├── Task 1: Create IdP project (independent)
  ├── Task 2: Wire into solution (depends on 1)
  ├── Task 3: TestCertificateAuthority (independent, parallel with 1)
  └── Task 4: IdpProcessHost (depends on 1, 2, 3)

Phase 2 — Manual Browser Verification (Tasks 5-6)
  ├── Task 5: AuthFleetWebApplicationFactory (depends on 4)
  └── Task 6: Manual smoke test (depends on 5)

Phase 3 — Playwright E2E Wiring (Tasks 7-10)
  ├── Task 7: AuthE2ETestBase (depends on 5)
  ├── Task 8: IdP Login Page Object (depends on 1)
  ├── Task 9: OIDC Sign-In/Out tests (depends on 7, 8)
  └── Task 10: returnUrl deep-link test (depends on 9)

Phase 4 — Onboarding Regression (Tasks 11-12)
  ├── Task 11: Onboarding flow tests (depends on 7)
  └── Task 12: Onboarding refresh regression (depends on 11)

Phase 5 — CI & Polish (Tasks 13-14)
  ├── Task 13: CI workflow update (depends on 9, 12)
  └── Task 14: Package management cleanup (depends on 2)
```

**Loom/Tapestry parallelism opportunities:**
- Tasks 1 + 3 can run in parallel (no dependency).
- Tasks 8 + 7 can run in parallel once Phase 1 is complete.
- Tasks 13 + 14 can run in parallel.
- Phase 4 (Tasks 11-12) is independent of Tasks 9-10 — can run in parallel once Task 7 is done.

## Key Design Details

### Duende IdentityServer Config.cs

```
Clients:
  - ClientId: "fleet-test-client"
  - ClientSecret: "test-secret" (sha256 hashed)
  - AllowedGrantTypes: GrantTypes.Code
  - RequirePkce: true
  - RedirectUris: ["https://app.dev.localhost:*/auth/callback"]  (or dynamic)
  - PostLogoutRedirectUris: ["https://app.dev.localhost:*/auth/signed-out"]
  - AllowedScopes: ["openid", "profile", "email"]
  - AllowOfflineAccess: false

Identity Resources:
  - IdentityResources.OpenId
  - IdentityResources.Profile
  - IdentityResources.Email

Test Users:
  - { SubjectId: "test-user-1", Username: "testuser", Password: "password",
      Claims: [email: "test@example.com", name: "Test User"] }
  - { SubjectId: "new-user", Username: "newuser", Password: "password",
      Claims: [email: "new@example.com", name: "New User"] }
```

### Cookie / CORS Coordination

Fleet's CORS `AllowedOrigins` must include the Fleet URL itself (for SPA same-origin) and potentially the IdP URL (for cross-origin OIDC interactions). Since OIDC uses full-page redirects (not CORS-gated fetch), the primary concern is that `AllowedOrigins` includes `https://app.dev.localhost:{fleet-port}`. The IdP doesn't make cross-origin API calls to Fleet.

### Onboarding Refresh Regression — What It Tests

The `OnboardingGate` React component evaluates:
```tsx
const showWizard = clientConfig.authEnabled && clientConfig.cloudMode
  && !dismissed && currentUser?.onboardingStatus?.completed === false;
```

- `dismissed` is React state — lost on page refresh.
- `onboardingStatus.completed` comes from the server via `GET /api/user/me`.
- **Before** calling `POST /api/user/me/complete-onboarding`: a refresh re-fetches server state → `completed = false` → wizard re-renders. This is correct behavior, not a bug.
- **After** calling complete-onboarding: a refresh re-fetches → `completed = true` → wizard skipped.
- The regression test ensures both paths work and that no intermediate state (e.g., partial wizard progress) causes the wizard to render in a broken state.

## Risks & Mitigations

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| 1 | **Duende license** — Duende IdentityServer Community Edition is free for dev/testing but has [license terms](https://duendesoftware.com/license). Using it in a test project (not shipped to production) should be fine, but verify. | Medium | The IdP project lives in `tests/` — it's test infrastructure, not a production dependency. Document this in the project's README. |
| 2 | **`*.dev.localhost` DNS resolution** — While RFC 6761 reserves `.localhost`, older OS/network configs may not resolve `auth.dev.localhost`. | Medium | Add fallback: if `*.dev.localhost` doesn't resolve, fall back to `127.0.0.1` with port-based differentiation. In CI, add `/etc/hosts` entries if needed. Test resolution in CI workflow. |
| 3 | **Two-process HTTPS port coordination** — IdP and Fleet both need ephemeral ports, but OIDC `redirect_uri` must contain Fleet's port. | Medium | Start IdP first (port determined). Configure Fleet's Authority. Start Fleet (port determined). Duende's in-memory client store allows updating `redirect_uri` after Fleet's port is known. Alternatively, use a generous redirect URI pattern. |
| 4 | **Duende login UI complexity** — Full Duende quickstart UI has consent screens, MFA, etc. | Low | Use the minimal UI quickstart. Disable consent for the test client (`RequireConsent = false`). Keep the login page as simple as possible — username, password, submit. |
| 5 | **HTTPS cookies in Playwright with `IgnoreHTTPSErrors`** | Low | Well-tested pattern. Playwright explicitly supports this. |
| 6 | **Onboarding test requires `cloudMode + authEnabled`** — More factory configuration needed. | Low | Already accounted for in `AuthFleetWebApplicationFactory` design (Task 5). |
| 7 | **CI test time increase** — Auth E2E adds HTTPS handshakes, OIDC redirects, and Duende IdP startup. | Low | Budget ~45s additional per auth E2E test. IdP startup is fast (in-memory stores, no database). Tests are additive — existing E2E tests unchanged. |

### Open Questions

| # | Question | Recommendation |
|---|----------|----------------|
| 1 | **Should `IdpProcessHost` start the IdP in-process (same OS process) or as a child process?** | In-process via `WebApplication.CreateBuilder()`. Simpler lifecycle, debuggable, no port-file coordination. "Direct-process" per user constraint means hosted in the test process, not a separate exe. |
| 2 | **Should the IdP login page use Duende's quickstart UI or a custom minimal Razor page?** | Custom minimal Razor page. Duende's quickstart UI is complex and adds unnecessary pages. We need exactly: login form, logout confirmation. 2 Razor pages. |
| 3 | **Should we add a `DuendeIdP` project reference from E2E, or launch it standalone?** | Project reference from E2E. The E2E test suite creates the IdP host programmatically via `IdpProcessHost`. No standalone launch needed for automated tests. Manual verification (Task 6) also uses this. |
| 4 | **How to handle Duende's SameSite cookie behavior across `auth.dev.localhost` ↔ `app.dev.localhost`?** | These are different hostnames but both resolve to `127.0.0.1`. OIDC uses full-page redirects, not CORS. Cookies are set per-domain. Fleet's auth cookie is on `app.dev.localhost`, IdP's session cookie is on `auth.dev.localhost`. No cross-domain cookie issues. |

## Verification

- [ ] IdP project compiles: `dotnet build tests/WeaveFleet.IdP/ --configuration Release`
- [ ] Full solution compiles: `dotnet build WeaveFleet.slnx --configuration Release`
- [ ] Existing E2E tests pass: `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&Category!=AuthE2E&Category!=Benchmark" --configuration Release`
- [ ] Auth E2E tests pass: `dotnet test tests/WeaveFleet.E2E/ --filter "Category=AuthE2E" --configuration Release`
- [ ] Onboarding tests pass: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~OnboardingRefreshRegression" --configuration Release`
- [ ] No production source files modified (verify with `git diff --name-only src/`)
- [ ] CI workflow runs auth E2E tests on Ubuntu
