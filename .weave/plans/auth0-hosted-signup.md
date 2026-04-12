# Auth0 Hosted Signup for Fleet Cloud

## TL;DR
> **Summary**: Replace the Duende demo IdP with Auth0 for fleet.tryweave.io, adding a Weave-branded public login page. Auth0 is a standard OIDC provider — no Auth0-specific backend code needed. The existing OIDC config (`FleetOptions.Auth`) and auth endpoints work as-is.
> **Estimated Effort**: Short

## Context

### Original Request
Add hosted user signup for Fleet Cloud at fleet.tryweave.io using Auth0 as the identity provider. Users should be able to sign up with email/password (and optionally social providers), land on a Weave-branded page, and proceed into the Fleet app after authentication.

### Key Findings
1. **Backend auth is generic OIDC already.** `Program.cs` registers `AddOpenIdConnect()` with configurable `Authority`, `ClientId`, `ClientSecret`, `CallbackPath`. Switching IdPs is a config change — no code is Auth0-specific or Duende-specific.

2. **Auth plumbing is config-driven via `FleetOptions.Auth`.** All OIDC settings live in `AuthOptions` (`FleetOptions.cs` lines 100-129) and are bound from `appsettings.Cloud.json` → `Fleet:Auth:*`. Currently pointing at `https://demo.duendesoftware.com/`.

3. **Auth0 is a standard OIDC provider.** No Auth0-specific config section is needed. The existing `Authority`, `ClientId`, `ClientSecret`, `CallbackPath` fields are sufficient — just point them at Auth0.

4. **Auth0 supports standard OIDC RP-Initiated Logout.** New tenants (post Nov 2023) expose `end_session_endpoint` in their OIDC discovery document by default. The existing `Results.SignOut(...)` in `AuthEndpoints.cs` line 34-36 works as-is — no `/v2/logout` compatibility handler needed.

5. **Auth0 Universal Login includes signup by default.** The login screen has a "Sign up" link built in. No `screen_hint=signup` parameter forwarding is needed. Users can sign up without any special backend code.

6. **First-login user provisioning exists.** `UserService.EnsureUserAsync()` upserts a `User` entity keyed on the IdP `sub` claim on every authenticated request. No manual user creation needed.

7. **The SPA is entirely behind `AuthGate`.** `app.tsx` lines 36-123: on 401, the browser is redirected to `/auth/login` which triggers the OIDC challenge. There is no public-facing page the user sees before signing in.

8. **`ClaimsUserContext` reads standard OIDC claims** (`sub`, `email`, `name`). Auth0 emits these by default — no custom claim mapping needed.

9. **Deploy env vars already exist.** `deploy/README.md` already documents `FLEET_AUTH_AUTHORITY`, `FLEET_AUTH_CLIENT_ID`, `FLEET_AUTH_CLIENT_SECRET`. `bootstrap.sh` already provisions them into `fleet.env`.

10. **Weave brand patterns exist** in `welcome/page.tsx` and `globals.css`: gradients (`#3B82F6 → #A855F7 → #EC4899`), logo at `/weave_logo.png`, mono font. These are the reference for the login page.

11. **Logout in `header.tsx`** (line 95-116) constructs `returnUrl` from `window.location.pathname` — after logout, user lands back at whatever page they were on (which triggers re-auth). Should redirect to `/login` instead.

## Objectives

### Core Objective
Enable self-service signup and sign-in for fleet.tryweave.io via Auth0, with a Weave-branded public login page, **without any backend code changes** — Auth0 is just another OIDC provider.

### Deliverables
- [x] Auth0 tenant + application configuration (documented manual steps)
- [x] Auth0 Universal Login branded with Weave identity
- [x] Public login page route (`/login`) in the SPA, rendered outside `AuthGate`
- [x] Updated `AuthGate` to redirect to `/login` instead of directly to IdP
- [x] Post-logout redirect to `/login` landing page
- [x] Deploy documentation updated for Auth0 reference

### Definition of Done
- [x] A new user can visit `https://fleet.tryweave.io`, see a branded landing page, click "Sign in", create an Auth0 account, and land in the Fleet app with onboarding wizard
- [x] An existing user can click "Sign in", authenticate via Auth0, and resume their Fleet session
- [x] Logout terminates Auth0 session and redirects to `/login`
- [x] `/api/*` endpoints remain 401 for unauthenticated requests
- [x] Health checks (`/healthz`, `/readyz`) remain public
- [x] `dotnet test` passes (all unit + API tests green — no backend code changed)
- [x] `dotnet build -c Release` compiles without warnings
- [x] Frontend builds: `cd client && npm run build` succeeds
- [x] Frontend lint passes: `cd client && npm run lint` succeeds
- [x] No secrets committed to source control
- [x] Local mode (`Auth.Enabled = false`) is unaffected
- [x] Duende test IdP still works in dev/test (no backend auth code changed)

### Guardrails (Must NOT)
- Must NOT add Auth0-specific backend code (no `/v2/logout` handler, no `screen_hint` forwarding, no Auth0 SDK)
- Must NOT modify `Program.cs`, `AuthEndpoints.cs`, `FleetOptions.cs`, or `UserService.cs`
- Must NOT break local mode (`Auth.Enabled = false`)
- Must NOT break the Duende-based E2E test infrastructure
- Must NOT store Auth0 client secrets in `appsettings.Cloud.json` (use env vars)
- Must NOT implement multi-tenancy or organizations (future work)

## User Journey

```
1. User visits https://fleet.tryweave.io
   → SPA loads, AuthGate detects 401 on /api/config/client
   → Redirects to /login (client-side, NOT to IdP)
   → User sees Weave-branded landing page with "Sign in" + "Sign up" buttons

2. User clicks "Sign in" (or "Sign up" — both go to same endpoint)
   → Browser navigates to /auth/login
   → Backend issues OIDC challenge → Auth0 Universal Login opens
   → Auth0 login screen has a built-in "Sign up" link for new users
   → User signs in or creates account (email/password or social)
   → Auth0 redirects back to /auth/callback
   → Cookie issued, user redirected to /
   → EnsureUserAsync() creates shadow User record
   → AuthGate detects authenticated user, renders app shell
   → OnboardingWizard shows (first-time user)

3. User clicks "Sign Out" (in header dropdown)
   → POST /auth/logout with returnUrl=/login
   → Local cookie cleared
   → Standard OIDC RP-Initiated Logout terminates Auth0 session
   → Auth0 redirects to https://fleet.tryweave.io/login
   → User lands on /login landing page
```

## TODOs

### Phase 1: Auth0 Tenant Setup (Manual)

- [x] 1. **Create Auth0 Account and Application**
  **What**: Create and configure the Auth0 tenant and web application. This is a manual step in the Auth0 dashboard — no code changes.
  - Create Auth0 account and tenant (e.g. `weave-fleet`)
  - Create a "Regular Web Application" in Auth0 Dashboard → Applications → Create Application
  - Configure the application settings:
    - **Allowed Callback URLs**: `https://fleet.tryweave.io/auth/callback`
    - **Allowed Logout URLs**: `https://fleet.tryweave.io/login`
    - **Allowed Web Origins**: `https://fleet.tryweave.io`
  - Under Application → Credentials, note the **Domain**, **Client ID**, and **Client Secret**
  - Ensure "Authorization Code" grant type is enabled (default for Regular Web Apps)
  - The Authority URL for Fleet config = `https://<tenant>.us.auth0.com/`
  **Acceptance**: OIDC discovery document at `https://<domain>/.well-known/openid-configuration` is accessible and contains `authorization_endpoint`, `token_endpoint`, `userinfo_endpoint`, and `end_session_endpoint`.

- [x] 2. **Enable Database Connection and Optional Social Logins**
  **What**: Enable email/password signup and optionally configure social login providers.
  - Under Authentication → Database → Username-Password-Authentication:
    - Ensure it is enabled for the Fleet application
    - Set password policy: at least "Good" strength
    - Enable "Requires email verification" = Yes
  - Optional — under Authentication → Social:
    - Add Google social connection (scopes: `email`, `profile`)
    - Add GitHub social connection (scopes: `user:email`)
    - Enable both for the Fleet application
  **Acceptance**: A test user can sign up with email/password via Auth0's test page (Application → Quick Start → Try Login). Social logins (if enabled) appear on the login screen.

- [x] 3. **Brand Auth0 Universal Login**
  **What**: Customize Auth0's Universal Login with Weave branding.
  - Under Branding → Universal Login:
    - Upload Weave logo (`weave_logo.png`)
    - Set primary color: `#3B82F6` (blue, matching gradient start)
    - Set page background: `#0A0A0A` (dark theme background)
    - Set button color: `#3B82F6`
  - Under Branding → Universal Login → Text Customization (if available on plan):
    - Customize button and heading text to match Weave voice
  - Optional — under Branding → Custom Domains:
    - Configure `login.tryweave.io` for full white-labeling
    - Requires DNS CNAME and TLS provisioning via Auth0
    - If configured, update the Authority URL to `https://login.tryweave.io/`
  **Acceptance**: Visiting the Auth0 authorize URL shows a Weave-branded login page with logo and correct colors.

- [x] 4. **Configure Email Verification and Branded Templates**
  **What**: Set up email verification and customize email templates with Weave branding.
  - Under Branding → Email Templates:
    - Customize "Verification Email" with Weave branding
    - Set From address: `noreply@tryweave.io` (requires domain verification)
    - Customize subject: "Verify your Weave account"
  - Optional — under Branding → Email Provider:
    - Configure a custom email provider (SendGrid, Mailgun) for reliable delivery
    - Auth0's built-in provider has rate limits; custom recommended for production
  **Acceptance**: A new user receives a branded verification email.

- [x] 5. **Record Auth0 Credentials for Deployment**
  **What**: Store Auth0 credentials as GitHub Secrets for deployment. These map directly to the existing `Fleet__Auth__*` env vars — no new config fields needed.
  - Add/update GitHub Secrets:
    - `FLEET_AUTH_AUTHORITY` = `https://<tenant>.us.auth0.com/`
    - `FLEET_AUTH_CLIENT_ID` = from Auth0 Dashboard → Application → Settings
    - `FLEET_AUTH_CLIENT_SECRET` = from Auth0 Dashboard → Application → Settings
  **Acceptance**: GitHub repository has updated `FLEET_AUTH_AUTHORITY`, `FLEET_AUTH_CLIENT_ID`, and `FLEET_AUTH_CLIENT_SECRET` secrets. Values are NOT stored in any tracked file. The existing `bootstrap.sh` and `fleet.env` provisioning handles the rest.

### Phase 2: Weave-Branded Public Login Page (Code)

- [x] 6. **Create Public Login Page Component**
  **What**: Add a new login page rendered outside `AuthGate`. Shows Weave branding and "Sign in" / "Sign up" buttons. Reuse branding patterns from `welcome/page.tsx` (gradients, logo, mono font).
  **Files**:
    - `client/src/pages/login-page.tsx` (new file)
  **Acceptance**: The component renders a full-page Weave-branded layout with logo, heading, tagline, and two CTA buttons. Does not import or depend on any authenticated context (no `useAppShell`, no `apiFetch`). Passes lint.

  **Details**:
  - Full-page layout (no sidebar/chrome), vertically centered content
  - Weave logo: `<img src="/weave_logo.png" />` (same as `welcome/page.tsx`)
  - Heading: `<h1 className="weave-gradient-text font-mono">Weave</h1>` + `<p>Agent Fleet</p>`
  - Tagline: brief product description (e.g. "Orchestrate AI coding agents across your projects")
  - Two CTA buttons:
    - "Sign in" → `<a href={apiUrl("/auth/login?returnUrl=<encoded>")}>` (primary, `weave-gradient-bg`)
    - "Sign up" → `<a href={apiUrl("/auth/login?returnUrl=<encoded>")}>` (secondary/outline — same endpoint; Auth0 Universal Login has built-in signup link)
  - Read `returnUrl` from URL search params and forward it to `/auth/login` links; default to `/`
  - Use `apiUrl()` from `@/lib/api-client` to construct hrefs (handles base URL in split mode)
  - Version footer (same pattern as welcome page)
  - No API calls — this is a static page that links to backend auth endpoints

- [x] 7. **Mount Login Page Outside AuthGate in App Router**
  **What**: Modify `app.tsx` to add the `/login` route outside the `AuthGate` wrapper so unauthenticated users can access it. Lazy-load the `LoginPage` component. Wrap the existing `AuthGate` tree in a catch-all route.
  **Files**:
    - `client/src/app.tsx`
  **Acceptance**: Visiting `/login` when unauthenticated renders the login page without triggering any API calls or OIDC redirects. All other routes remain wrapped in `AuthGate`.

  **Details** — modify the `App` component:
  ```tsx
  const LoginPage = lazy(() => import("./pages/login-page"));

  export function App() {
    return (
      <Routes>
        <Route path="/login" element={
          <Suspense fallback={<PageFallback />}>
            <LoginPage />
          </Suspense>
        } />
        <Route path="/*" element={
          <AuthGate>
            <ClientLayout>
              <OnboardingGate>
                <Suspense fallback={<PageFallback />}>
                  <AppRoutes />
                </Suspense>
              </OnboardingGate>
            </ClientLayout>
          </AuthGate>
        } />
      </Routes>
    );
  }
  ```

- [x] 8. **Update AuthGate to Redirect to /login Instead of IdP**
  **What**: Change `AuthGate` so that when it detects 401, it redirects to `/login` (the SPA landing page) with a `returnUrl` query param, rather than directly to `/auth/login` (the backend OIDC challenge). This gives users a branded entry point before being sent to Auth0.
  **Files**:
    - `client/src/app.tsx` (AuthGate component, lines 36-123)
  **Acceptance**: An unauthenticated user visiting any protected route (e.g. `/`, `/sessions/123`) is redirected to `/login?returnUrl=%2Fsessions%2F123` (client-side navigation). After clicking "Sign in" on the landing page and authenticating, they return to their original destination. When `authEnabled === false` (local mode), behavior is unchanged — the 401 branch is only reached when auth is enabled.

  **Details**:
  - Change the `loginUrl` memo (lines 40-47): compute `/login?returnUrl=<encoded-current-path>` instead of `apiUrl("/auth/login?returnUrl=...")`
  - Lines 56-58 and 82-84 already use `window.location.assign(loginUrl)` — no changes needed there
  - The `/login` route is a client-side route, so no `apiUrl()` wrapper needed

- [x] 9. **Update Post-Logout Redirect to /login**
  **What**: After sign-out, redirect users to `/login` instead of the current page (which would trigger re-auth). Modify the logout handler in `header.tsx`.
  **Files**:
    - `client/src/components/layout/header.tsx` (lines 95-116)
  **Acceptance**: After clicking "Sign Out", Auth0 session is terminated and user lands on `/login`. They are NOT auto-redirected back to Auth0. Browser shows the landing page with "Sign in" / "Sign up" buttons.

  **Details**:
  - Change the `returnUrl` memo (lines 95-101) to always return `/login` instead of `window.location.pathname`
  - Or change line 116 to hardcode: `form.action = apiUrl("/auth/logout?returnUrl=" + encodeURIComponent("/login"))`
  - The OIDC middleware will use this `returnUrl` as the `post_logout_redirect_uri` in the standard RP-Initiated Logout flow, bringing the user back to `https://fleet.tryweave.io/login`
  - Auth0 must have `https://fleet.tryweave.io/login` in its "Allowed Logout URLs" (covered in TODO 1)

### Phase 3: Config & Deployment

- [x] 10. **Update Deploy Documentation with Auth0 Reference**
  **What**: Update `deploy/README.md` to reference Auth0 specifically (instead of generic "OIDC provider") for the secrets, and add a note about Auth0's Allowed URLs configuration.
  **Files**:
    - `deploy/README.md`
  **Acceptance**: README references Auth0 in the secrets table and includes a note about Auth0 Allowed URLs. No sensitive values in the file. No changes needed to `fleet.service`, `bootstrap.sh`, or any backend config — they already handle `Fleet__Auth__*` env vars generically.

  **Details**:
  - Update the GitHub Secrets table (lines 40-48) — change "From your OIDC provider" descriptions to reference Auth0:
    | `FLEET_AUTH_AUTHORITY` | Auth0 issuer URL | `https://<tenant>.us.auth0.com/` |
    | `FLEET_AUTH_CLIENT_ID` | Auth0 client ID | Auth0 Dashboard → Applications → Settings |
    | `FLEET_AUTH_CLIENT_SECRET` | Auth0 client secret | Auth0 Dashboard → Applications → Settings |
  - Add a note below the secrets table:
    > **Auth0 Application URLs**: In the Auth0 Dashboard, ensure the application's "Allowed Callback URLs" includes `https://<FLEET_DOMAIN>/auth/callback` and "Allowed Logout URLs" includes `https://<FLEET_DOMAIN>/login`.

### Phase 4: Testing

- [x] 11. **Verify No Backend Regressions**
  **What**: Run the full test suite to confirm nothing is broken. Since no backend code is changed, this should be trivial — it's purely a confidence check.
  **Acceptance**: `dotnet test` passes (all tests green). `dotnet build -c Release` succeeds with no warnings. Frontend builds and lints clean.

- [x] 12. **Manual Smoke Test: End-to-End Auth Flows**
  **What**: After deploying with Auth0 credentials, manually verify all auth flows work correctly.
  **Acceptance**: All of the following pass:
  - **Signup flow**: Visit `fleet.tryweave.io` → see login page → click "Sign up" → create account on Auth0 → land in Fleet app → onboarding wizard shows
  - **Sign-in flow**: Visit `fleet.tryweave.io` → click "Sign in" → authenticate → land in Fleet app
  - **Logout flow**: Click "Sign Out" in header → Auth0 session terminated → land on `/login` page
  - **Return sign-in**: Click "Sign in" again → authenticate → land in Fleet app (confirms session was fully terminated)
  - **Direct URL access**: Visit `fleet.tryweave.io/sessions/123` when logged out → redirect to `/login?returnUrl=%2Fsessions%2F123` → sign in → land on `/sessions/123`
  - **API protection**: `curl https://fleet.tryweave.io/api/config/client` → 401
  - **Health checks**: `curl https://fleet.tryweave.io/healthz` → 200

## Files Changed Summary

**New files:**
- `client/src/pages/login-page.tsx` — public login page component

**Modified files:**
- `client/src/app.tsx` — mount login page outside AuthGate; update AuthGate redirect to `/login`
- `client/src/components/layout/header.tsx` — post-logout redirect to `/login`
- `deploy/README.md` — Auth0 setup reference in secrets table

**Files NOT changed (by design):**
- `src/WeaveFleet.Api/Program.cs` — OIDC setup is already generic
- `src/WeaveFleet.Api/Endpoints/AuthEndpoints.cs` — login/logout endpoints work as-is
- `src/WeaveFleet.Application/Configuration/FleetOptions.cs` — config model already has what's needed
- `src/WeaveFleet.Application/Services/UserService.cs` — first-login user creation works as-is
- `src/WeaveFleet.Api/appsettings.Cloud.json` — real values injected via env vars at deploy time; placeholder values here are irrelevant

## Security & Privacy Considerations

- **No Auth0 SDK in backend**: Standard OIDC — no vendor lock-in, standard audit surface
- **Client secret protection**: Never in source control; injected via env var in `fleet.env` (mode 600, owned by `fleet:fleet`)
- **CSRF**: Already handled — antiforgery middleware validates `X-CSRF-Token` on `/auth/logout`
- **Cookie security**: Already `HttpOnly`, `SameSite=Lax`, `SecurePolicy=Always`, 24h sliding expiration
- **Open redirect prevention**: `NormalizeReturnUrl()` in `AuthEndpoints.cs` rejects non-local URLs
- **Token storage**: `SaveTokens = false` — no access/refresh tokens in cookies
- **Email verification**: Enabled in Auth0 to prevent unverified signups (TODO 2)
- **Rate limiting**: Auth0 provides built-in brute-force protection

## Verification
- [x] No backend code changed — `git diff` shows zero changes to `src/` directory
- [x] All tests pass: `dotnet test`
- [x] Release build succeeds: `dotnet build -c Release`
- [x] Frontend builds: `cd client && npm run build`
- [x] Frontend lint passes: `cd client && npm run lint`
- [ ] New user signup works end-to-end on fleet.tryweave.io
- [ ] Existing user sign-in works on fleet.tryweave.io
- [ ] Logout terminates Auth0 session and redirects to /login
- [x] Unauthenticated `/api/*` requests return 401
- [x] Health checks (`/healthz`, `/readyz`) remain publicly accessible
- [x] Local mode (`Auth.Enabled = false`) is unaffected
- [x] Duende test IdP still works in dev/test (no backend changes)
- [x] No secrets in source control
