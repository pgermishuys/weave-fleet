# OIDC E2E Test Infrastructure

## TL;DR
> **Summary**: Add a local OIDC Identity Provider to the E2E test suite so Playwright tests can perform real browser-based sign-in flows against Fleet running with `Auth.Enabled = true`, validating the full cookie/CSRF/OIDC pipeline without external dependencies.
> **Estimated Effort**: Large

## Context

### Original Request
Introduce a local test IdP and Playwright E2E tests that perform a real OIDC sign-in flow against Fleet, exercising the auth cookies, CSRF tokens, redirect URIs, and authorization middleware end-to-end.

### Key Findings

**Existing E2E architecture** (all in `tests/WeaveFleet.E2E/`):
- `FleetWebApplicationFactory` boots a real Kestrel server on `http://127.0.0.1:0` (ephemeral port).
- `PlaywrightFixture` launches headless Chromium and creates isolated browser contexts per test.
- `E2ETestBase` wires them together: per-test page, tracing, failure artifacts.
- All current tests run with `Auth.Enabled = false` (local mode) — the `FleetOptions` constructed in the factory never sets auth properties.
- The test factory replaces `IHarness` with `TestHarness` — this pattern is clean and extensible.

**Auth flow in production** (`Program.cs`):
- When `Auth.Enabled = true`: cookie auth (default scheme) + OIDC challenge scheme (Clerk-like).
- Auth cookie: `SecurePolicy = CookieSecurePolicy.Always` → **requires HTTPS**.
- CSRF cookie: `Secure = true` → **requires HTTPS**.
- Antiforgery validation cookie: `HttpOnly = true`, no explicit `Secure` → inherits framework default.
- `SameSite = Lax` on auth cookie, `SameSite = Strict` on CSRF cookies.
- Endpoints under `/api` and `/ws` are gated by `RequireAuthorization("FleetUser")` via `EndpointExtensions.MapFleetEndpoints`.
- `/auth/login` triggers OIDC challenge to the Authority; `/auth/callback` receives the code.
- CORS in auth mode restricts to `AllowedOrigins` with `AllowCredentials()`.

**Client-side auth** (`client/src/app.tsx`):
- `AuthGate` component calls `GET /api/user/me` on mount.
- On 401 → redirects browser to `/auth/login?returnUrl=...`.
- `/auth/login` endpoint issues `Results.Challenge(…, [OpenIdConnectDefaults.AuthenticationScheme])` which redirects to the IdP.

**Blockers identified**:
1. **HTTPS required**: Auth cookie `SecurePolicy.Always` and CSRF cookie `Secure = true` mean HTTP-only Kestrel won't set cookies. Browser rejects `Set-Cookie` with `Secure` over plain HTTP.
2. **OIDC metadata fetch**: The OIDC handler fetches `/.well-known/openid-configuration` from `Authority` — this must be reachable and serve valid metadata.
3. **Dynamic ports**: Fleet listens on `:0`, but OIDC `redirect_uri` and CORS `AllowedOrigins` need to match the actual bound address.
4. **No test IdP exists**: Need a lightweight, in-process OIDC provider that issues real tokens.

**Existing test auth pattern** (`tests/WeaveFleet.Api.Tests/Infrastructure/ApiWebApplicationFactory.cs`):
- Already has `TestAuthHandler` (fake auth scheme) and `UnauthorizedAuthHandler` for API integration tests.
- This bypasses OIDC entirely — useful for API tests but not for E2E browser flows.

## Objectives

### Core Objective
Enable real browser-based OIDC E2E tests that exercise the complete authentication pipeline: browser → Fleet → IdP login form → callback → cookie → authenticated SPA.

### Deliverables
- [ ] In-process test OIDC Identity Provider that serves valid OpenID Connect metadata and token endpoints
- [ ] HTTPS-enabled Kestrel configuration for E2E tests using self-signed dev certificates
- [ ] Auth-aware `FleetWebApplicationFactory` variant (or configurable mode) that boots Fleet with `Auth.Enabled = true`
- [ ] Playwright page objects for the IdP login page
- [ ] E2E test class covering the full sign-in, authenticated API access, sign-out, and 401-redirect flows
- [ ] CI workflow support (dev cert generation, HTTPS trust)

### Definition of Done
- [ ] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&FullyQualifiedName~OidcSignIn"` passes
- [ ] Existing (non-auth) E2E tests continue to pass unchanged
- [ ] Tests work in headless CI (GitHub Actions Ubuntu) without manual certificate trust
- [ ] No production code changes weaken security (e.g., no removal of `Secure` cookie flags)

### Guardrails (Must NOT)
- Must NOT remove or conditionally disable `CookieSecurePolicy.Always` in production code
- Must NOT remove `Secure = true` from CSRF cookies in production code
- Must NOT add test-only backdoor auth schemes to the production `Program.cs`
- Must NOT require an external IdP or internet access to run tests
- Must NOT break existing local-mode E2E tests

## Architecture Decision

### Chosen Approach: In-Process Test IdP + HTTPS Kestrel

```
Playwright (Chromium)
    │  HTTPS (browser trusts test CA)
    ▼
Fleet Kestrel (https://127.0.0.1:{port})
    │  Auth.Enabled = true
    │  OIDC Authority = https://127.0.0.1:{idp-port}
    ▼
TestIdpHost (ASP.NET Core minimal API, Kestrel HTTPS)
    │  /.well-known/openid-configuration
    │  /connect/authorize  (renders HTML login form)
    │  /connect/token      (issues JWT id_token + code exchange)
    │  /connect/userinfo   (returns claims)
    └  /connect/endsession (logout)
```

**Why in-process custom IdP instead of Duende IdentityServer?**
- Duende IdentityServer requires a paid license for non-OSS commercial use — license compliance unclear.
- A minimal custom IdP (just the 5 OIDC endpoints above) is ~300 lines and gives us full control.
- We only need authorization code flow with a single test client — no complex token scenarios.
- We can render a trivially simple login form (username + submit button) that Playwright can fill.

**Why not OpenIddict?**
- OpenIddict is a full framework requiring EF Core setup, server/client separation, etc.
- Overkill for a test harness that needs exactly one client, one user, and deterministic behavior.

**Why HTTPS (not HTTP with relaxed cookies)?**
- The entire point is to test the real security configuration. Relaxing `Secure` flags would test a different code path than production.
- ASP.NET Core dev certs (`dotnet dev-certs https`) solve local trust. For CI, we generate a self-signed CA + leaf cert and configure Kestrel programmatically.

### Certificate Strategy

**Approach: Programmatic X509 certificates — no filesystem, no `dotnet dev-certs`**.

Generate certs at test startup using `System.Security.Cryptography`:
1. Create a self-signed CA certificate (RSA 2048, `BasicConstraints(CA=true)`).
2. Issue a leaf certificate from the CA for `127.0.0.1` (SAN: IP:127.0.0.1).
3. Configure both Fleet Kestrel and TestIdpHost Kestrel with the leaf cert via `ListenOptions.UseHttps(leafCert)`.
4. Create an `HttpClientHandler` with a custom `ServerCertificateCustomValidationCallback` that trusts the CA cert — inject this into the OIDC handler's `BackchannelHttpHandler`.
5. Configure Playwright's Chromium with `--ignore-certificate-errors` launch arg (standard for E2E test infrastructure).

This approach:
- Requires zero filesystem state or OS trust store modifications.
- Works identically on Windows, macOS, and Linux CI.
- Is fully self-contained per test run.

## TODOs

- [ ] 1. Create TestIdpHost — Minimal OIDC Identity Provider
  **What**: Build a minimal ASP.NET Core host that implements the 5 required OIDC endpoints. It must: (a) serve `/.well-known/openid-configuration` with valid metadata, (b) render an HTML login form at the authorization endpoint that Playwright can interact with, (c) exchange authorization codes for tokens at the token endpoint, (d) return user claims at the userinfo endpoint, (e) handle end-session. The IdP uses in-memory RSA key pairs for signing JWTs. Support configurable test users (userId, email, displayName).
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/TestIdp/TestIdpHost.cs`, `tests/WeaveFleet.E2E/Infrastructure/TestIdp/TestIdpConfiguration.cs`, `tests/WeaveFleet.E2E/Infrastructure/TestIdp/OidcEndpoints.cs`, `tests/WeaveFleet.E2E/Infrastructure/TestIdp/TokenService.cs`
  **Acceptance**: TestIdpHost can be started, its `/.well-known/openid-configuration` returns valid JSON, and a manual HTTP request to the token endpoint returns a signed JWT.

- [ ] 2. Create TestCertificateAuthority — Programmatic TLS Certificates
  **What**: Build a utility class that generates: (a) a self-signed CA cert, (b) a leaf cert for `127.0.0.1` signed by the CA. Expose the leaf cert as `X509Certificate2` for Kestrel HTTPS binding. Expose the CA cert for custom `ServerCertificateCustomValidationCallback`. All in-memory, no disk I/O. Use `CertificateRequest`, `RSA.Create(2048)`, and `SubjectAlternativeNameBuilder`.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/TestCertificateAuthority.cs`
  **Acceptance**: Unit test can create certs, bind a Kestrel endpoint with HTTPS, and make a trusted request using the custom callback.

- [ ] 3. Create AuthFleetWebApplicationFactory — Auth-Enabled Fleet Host
  **What**: Create a variant/mode of the factory that boots Fleet with `Auth.Enabled = true`, HTTPS binding, and OIDC configured to point at the TestIdpHost. Key configuration: (a) `UseUrls("https://127.0.0.1:0")` with the leaf cert, (b) `Fleet:Auth:Enabled = true`, (c) `Fleet:Auth:Authority = {TestIdpHost.Url}`, (d) `Fleet:Auth:ClientId = test-client`, (e) `Fleet:Auth:ClientSecret = test-secret`, (f) `Fleet:Auth:AllowedOrigins` set to the dynamically assigned Fleet URL (requires two-phase startup: start IdP first, then Fleet). Configure OIDC handler's `BackchannelHttpHandler` with the custom cert validator so the OIDC metadata fetch trusts the test CA. Set `Fleet:Auth:AllowedOrigins:0` to the Fleet HTTPS URL after port assignment.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/AuthFleetWebApplicationFactory.cs`
  **Acceptance**: Factory starts, Fleet serves HTTPS, navigating to any `/api` endpoint returns 401, navigating to `/auth/login` redirects to the TestIdpHost's authorize endpoint.

- [ ] 4. Create AuthE2ETestBase — Base Class for Auth E2E Tests
  **What**: Create a base class for auth-aware E2E tests, analogous to `E2ETestBase` but using `AuthFleetWebApplicationFactory`. Key differences: (a) browser context is created with `IgnoreHTTPSErrors = true` (Playwright option, cleaner than `--ignore-certificate-errors`), (b) provides `LoginAsync(username, password)` helper that navigates to a protected page, waits for redirect to IdP login form, fills credentials, and submits, (c) provides `AssertAuthenticatedAsync()` helper that verifies `/api/user/me` returns 200.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/AuthE2ETestBase.cs`
  **Acceptance**: Base class compiles and can be inherited by test classes.

- [ ] 5. Create IdP Login Page Object
  **What**: Create a Playwright page object for the TestIdp login form. The form will have a username input, a display-name input (optional), and a submit button. Use `data-testid` selectors on the IdP's rendered HTML. Methods: `FillUsernameAsync(string)`, `FillDisplayNameAsync(string)`, `SubmitAsync()`, `WaitForVisibleAsync()`.
  **Files**: `tests/WeaveFleet.E2E/Pages/TestIdpLoginPage.cs`
  **Acceptance**: Page object compiles and matches the HTML rendered by TestIdpHost.

- [ ] 6. Implement OIDC Sign-In E2E Tests
  **What**: Create the first auth E2E test class covering: (a) unauthenticated user navigates to `/` → redirected to IdP login → fills form → submits → redirected back to Fleet → sees dashboard, (b) authenticated user's `/api/user/me` returns correct userId/email/displayName, (c) CSRF tokens are set and work for POST requests, (d) sign-out clears the session (POST `/auth/logout` with CSRF → cookie cleared → subsequent `/api/user/me` returns 401). These tests exercise the full `AuthGate → /auth/login → OIDC challenge → IdP authorize → callback → cookie → authenticated SPA` flow.
  **Files**: `tests/WeaveFleet.E2E/Tests/OidcSignInTests.cs`
  **Acceptance**: All 4 tests pass with `dotnet test --filter "FullyQualifiedName~OidcSignInTests"`.

- [ ] 7. Implement Authenticated Session Lifecycle E2E Test
  **What**: Create a test that performs a full authenticated session workflow: sign in → create session → send prompt → receive response → verify user identity in session metadata. This proves that the auth cookie, CSRF token, and `ClaimsUserContext` all work together for the core Fleet functionality.
  **Files**: `tests/WeaveFleet.E2E/Tests/AuthenticatedSessionTests.cs`
  **Acceptance**: Test passes; session is created under the authenticated user's identity.

- [ ] 8. Update PlaywrightFixture for HTTPS Certificate Errors
  **What**: Add a configuration option to `PlaywrightFixture` or create a parallel fixture that launches Chromium with `--ignore-certificate-errors` for auth E2E tests. Alternatively, rely on Playwright's `BrowserNewContextOptions.IgnoreHTTPSErrors = true` (per-context, no browser-level arg needed). Verify this works with the self-signed leaf cert.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/PlaywrightFixture.cs`
  **Acceptance**: Browser context trusts the test HTTPS endpoint without errors.

- [ ] 9. Handle Dynamic Port + AllowedOrigins Coordination
  **What**: The OIDC `redirect_uri` sent during the authorization request contains Fleet's base URL. CORS `AllowedOrigins` must also match. Since both Fleet and IdP use ephemeral ports, implement a two-phase startup: (a) Start TestIdpHost → capture its HTTPS URL, (b) Configure Fleet's `Auth.Authority` and start Fleet → capture its HTTPS URL, (c) Register Fleet's URL as a valid `redirect_uri` in the TestIdpHost (via a setter or in-memory configuration). The TestIdpHost should accept any `redirect_uri` starting with the Fleet base URL (or just accept any `redirect_uri` — this is a test-only IdP). Fleet's `AllowedOrigins` is set to its own URL before startup via `UseSetting`.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/AuthFleetWebApplicationFactory.cs`, `tests/WeaveFleet.E2E/Infrastructure/TestIdp/TestIdpConfiguration.cs`
  **Acceptance**: Authorization code flow completes with dynamically assigned ports on both sides.

- [ ] 10. Update CI Workflow for Auth E2E Tests
  **What**: Ensure `ci.yml` E2E job runs auth tests alongside existing E2E tests. No special cert trust steps needed (certs are in-memory, Chromium uses `IgnoreHTTPSErrors`). Verify that the OIDC backchannel HTTP handler bypasses OS cert trust. May need to add `System.Security.Cryptography` test on Linux CI to verify RSA key generation works.
  **Files**: `.github/workflows/ci.yml`
  **Acceptance**: CI pipeline runs auth E2E tests and passes on Ubuntu.

- [ ] 11. Update E2E README with Auth Test Documentation
  **What**: Document: (a) architecture of auth E2E tests (TestIdpHost + HTTPS + cert strategy), (b) how to run auth tests locally, (c) how to debug auth flow issues (trace artifacts show redirects), (d) how to add new test users to the IdP.
  **Files**: `tests/WeaveFleet.E2E/README.md`
  **Acceptance**: README accurately describes the auth E2E infrastructure.

## Implementation Order

```
Phase 1 — Foundation (Tasks 1-2, parallel)
  ├── Task 1: TestIdpHost (OIDC endpoints)
  └── Task 2: TestCertificateAuthority (TLS certs)

Phase 2 — Integration (Tasks 3, 8-9, sequential)
  ├── Task 3: AuthFleetWebApplicationFactory (depends on 1, 2)
  ├── Task 9: Dynamic port coordination (integrated into Task 3)
  └── Task 8: PlaywrightFixture HTTPS support

Phase 3 — Tests (Tasks 4-7, sequential)
  ├── Task 4: AuthE2ETestBase (depends on 3)
  ├── Task 5: IdP Login Page Object (depends on 1)
  ├── Task 6: OidcSignInTests (depends on 4, 5)
  └── Task 7: AuthenticatedSessionTests (depends on 6)

Phase 4 — CI & Docs (Tasks 10-11, parallel)
  ├── Task 10: CI workflow
  └── Task 11: README
```

## Key Design Details

### TestIdpHost Endpoint Specifications

**`GET /.well-known/openid-configuration`** — returns:
```json
{
  "issuer": "https://127.0.0.1:{port}",
  "authorization_endpoint": "https://127.0.0.1:{port}/connect/authorize",
  "token_endpoint": "https://127.0.0.1:{port}/connect/token",
  "userinfo_endpoint": "https://127.0.0.1:{port}/connect/userinfo",
  "end_session_endpoint": "https://127.0.0.1:{port}/connect/endsession",
  "jwks_uri": "https://127.0.0.1:{port}/.well-known/jwks",
  "response_types_supported": ["code"],
  "subject_types_supported": ["public"],
  "id_token_signing_alg_values_supported": ["RS256"],
  "scopes_supported": ["openid", "profile", "email"],
  "grant_types_supported": ["authorization_code"],
  "code_challenge_methods_supported": ["S256"]
}
```

**`GET /.well-known/jwks`** — returns the RSA public key in JWK format.

**`GET /connect/authorize`** — renders a minimal HTML login form:
```html
<form method="post" action="/connect/authorize">
  <input type="hidden" name="client_id" value="{from_query}" />
  <input type="hidden" name="redirect_uri" value="{from_query}" />
  <input type="hidden" name="state" value="{from_query}" />
  <input type="hidden" name="nonce" value="{from_query}" />
  <input type="hidden" name="scope" value="{from_query}" />
  <input type="hidden" name="response_type" value="code" />
  <input data-testid="login-username" name="username" value="test-user" />
  <input data-testid="login-display-name" name="display_name" value="Test User" />
  <button data-testid="login-submit" type="submit">Sign In</button>
</form>
```

**`POST /connect/authorize`** — validates form, generates authorization code, redirects to `redirect_uri?code={code}&state={state}`.

**`POST /connect/token`** — exchanges code for `id_token` + `access_token` (JWT signed with the test RSA key). Claims: `sub`, `email`, `name`, `aud`, `iss`, `iat`, `exp`, `nonce`.

**`GET /connect/userinfo`** — returns `{ sub, email, name }` for the authenticated user (validates bearer token).

### AuthFleetWebApplicationFactory Key Configuration

```csharp
// In ConfigureWebHost:
builder.UseSetting("Fleet:Auth:Enabled", "true");
builder.UseSetting("Fleet:Auth:Authority", testIdpHost.Url);
builder.UseSetting("Fleet:Auth:ClientId", "test-client");
builder.UseSetting("Fleet:Auth:ClientSecret", "test-secret");
builder.UseSetting("Fleet:Auth:CallbackPath", "/auth/callback");
builder.UseSetting("Fleet:Auth:AllowedOrigins:0", serverUrl); // set after port known

// OIDC handler backchannel must trust the test CA:
builder.ConfigureServices(services =>
{
    services.PostConfigure<OpenIdConnectOptions>(
        OpenIdConnectDefaults.AuthenticationScheme,
        options =>
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                    TestCertificateAuthority.Validate(cert, caCert)
            };
        });
});

// Kestrel HTTPS with leaf cert:
builder.UseKestrel(k => k.ListenLocalhost(0, o => o.UseHttps(leafCert)));
```

### Cookie Handling in Tests

Since Fleet sets `Secure` cookies and we run over HTTPS:
- Auth cookie (`.WeaveFleet.Auth`): Set by ASP.NET Cookie auth handler after OIDC callback. `Secure=true`, `HttpOnly=true`, `SameSite=Lax`.
- CSRF cookie (`.WeaveFleet.CSRF`): Set by inline middleware on safe GET requests. `Secure=true`, `HttpOnly=false`, `SameSite=Strict`.
- Antiforgery cookie (`.WeaveFleet.Antiforgery`): Set by ASP.NET antiforgery. `HttpOnly=true`, `SameSite=Strict`.

All cookies will be set correctly over HTTPS. Playwright's cookie jar handles them natively.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| In-memory certs don't work on all .NET 10 + OS combos | Tests fail on specific platforms | Use `X509Certificate2` with `RSA.Create()` which is cross-platform since .NET 6. Add CI matrix test if needed. |
| OIDC middleware rejects test IdP tokens | Auth flow breaks silently | Extensive logging in TestIdpHost. Verify JWT claims match exactly what ASP.NET expects (iss, aud, nonce, etc.). |
| Playwright `IgnoreHTTPSErrors` deprecated or behavior changes | Browser rejects test certs | Currently stable in Playwright 1.51. Fallback: `--ignore-certificate-errors` browser arg. |
| Race condition in two-phase startup (IdP → Fleet) | Flaky test initialization | Sequential async startup with explicit readiness checks (HTTP probe to IdP before starting Fleet). |
| Chromium may not follow 302 redirects through OIDC correctly in headless mode | Sign-in flow hangs | Playwright handles 302 redirects natively. Add explicit `WaitForURL` assertions at each redirect hop. |
| Auth E2E tests are slower than local-mode tests (extra TLS handshakes, redirects) | CI time increases | Auth tests are additive (local-mode tests unchanged). Budget ~30s per auth test. Consider xUnit parallelization. |
| OIDC nonce/state validation issues | Callback rejected by ASP.NET | TestIdpHost must correctly round-trip `state` and `nonce` from the authorize request to the token response. |

### Fallback Options

1. **If in-process IdP is too complex**: Use [DuendeServer.Testing](https://github.com/DuendeSoftware/IdentityServer) test host — requires Duende license acceptance but is well-tested. However, the custom minimal IdP is preferred for simplicity and license freedom.

2. **If HTTPS proves problematic in CI**: Add a `E2E_AUTH_SKIP` environment variable that skips auth E2E tests in environments where cert generation fails. This is a last resort — the in-memory cert approach should work everywhere.

3. **If two-phase startup is too fragile**: Merge IdP and Fleet into a single Kestrel host by mounting the IdP endpoints under a `/test-idp/` prefix in the Fleet app (test-only middleware). This eliminates port coordination but mixes concerns. Reserve as fallback.

## Verification
- [ ] All existing E2E tests pass: `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E" --configuration Release`
- [ ] Auth E2E tests pass: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~OidcSignIn" --configuration Release`
- [ ] Auth session test passes: `dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~AuthenticatedSession" --configuration Release`
- [ ] No production source files have `Secure` or `SecurePolicy` changes
- [ ] `dotnet build WeaveFleet.slnx --configuration Release` compiles with zero warnings
- [ ] CI workflow passes on Ubuntu
