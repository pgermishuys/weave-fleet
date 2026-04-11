# Learnings: Duende IdP Auth E2E Pipeline

## Task 1: Create IdP project

- **Discrepancy**: The plan referenced `Pages/Login.cshtml` and `Pages/Logout.cshtml` as flat files, but Duende's Razor Pages convention uses folder-per-page with `Index.cshtml` / `Index.cshtml.cs` inside `Pages/Login/` and `Pages/Logout/`.
- **Resolution**: Created `Pages/Login/Index.cshtml` and `Pages/Logout/Index.cshtml` following standard Razor Pages convention.
- **Suggestion**: Plan should specify folder-per-page Razor structure or note that Duende uses `Pages/{name}/Index.cshtml`.

- **Discrepancy**: `IdentityModel.JwtClaimTypes` was referenced in `Config.cs` but `IdentityModel` is not an explicit dependency of the IdP project (it's a transitive dep of Duende).
- **Resolution**: Used plain string claim type names (`"email"`, `"name"`, etc.) instead of `JwtClaimTypes.*` constants.
- **Suggestion**: Either add `IdentityModel` as an explicit package or note using string literals for claim type names.

- **Discrepancy**: `tests/Directory.Build.props` adds `<Using Include="Shouldly" />` globally, which broke the `WeaveFleet.IdP` web project.
- **Resolution**: Added a `Directory.Build.props` inside `tests/WeaveFleet.IdP/` that imports the parent and then removes the `Shouldly` using.
- **Suggestion**: The plan should note that any new web project under `tests/` needs to neutralize the test-only global usings.

## Task 2: Add IdP Project to Solution

- **Discrepancy**: Both `WeaveFleet.Api` and `WeaveFleet.IdP` define a top-level `Program` class (via top-level statements), causing a `CS0433: The type 'Program' exists in both` error in `WeaveFleet.E2E` which references both.
- **Resolution**: Added `<Aliases>IdpHost</Aliases>` to the project reference in `WeaveFleet.E2E.csproj` and used `extern alias IdpHost;` + `IdpHost::WeaveFleet.IdP.Config` in files that need IdP types.
- **Suggestion**: The plan should note the alias requirement or suggest the IdP project wrap `Program` in a namespace.

- **Discrepancy**: `Config` is `internal` (correct per C# standards), but `IdpProcessHost` in the E2E project needs it.
- **Resolution**: Added `<InternalsVisibleTo Include="WeaveFleet.E2E" />` to `WeaveFleet.IdP.csproj`.
- **Suggestion**: Plan should specify using `InternalsVisibleTo` for test-to-test cross-assembly internal access.

## Task 4: IdpProcessHost

- **Discrepancy**: The plan suggested using `extern alias` with `IdpHost::Duende.IdentityServer`, but Duende assemblies are in the default alias (transitive deps don't inherit the alias of the referencing assembly).
- **Resolution**: Used `global::Duende.IdentityServer.*` for Duende types and `IdpHost::WeaveFleet.IdP.*` only for the IdP's own types.
- **Suggestion**: Plan should clarify that only the directly aliased assembly's namespaces are under the alias — transitive deps remain in `global::`.

## Task 5: AuthFleetWebApplicationFactory

- **Discrepancy**: `WebApplicationFactory.ConfigureWebHost` uses `builder.UseUrls()` but the URL must be cleared (not just set), because Kestrel's URL is configured via `ListenOptions` in `CreateHost`. Calling `UseUrls()` with no args clears defaults so the Kestrel manual configuration in `CreateHost` takes effect.
- **Resolution**: Called `builder.UseUrls()` with no args in `ConfigureWebHost` and fully configured HTTPS binding in `CreateHost` override.
- **Suggestion**: Plan should explicitly note the two-phase URL configuration: clear in `ConfigureWebHost`, bind with cert in `CreateHost`.

- **Discrepancy**: `extern alias` declaration must appear at the top of every file that uses it — it cannot be inherited from another file or global usings.
- **Resolution**: Added `extern alias IdpHost;` at the top of both `IdpProcessHost.cs` and `AuthFleetWebApplicationFactory.cs`.
- **Suggestion**: Plan should note that `extern alias` is file-scoped.

## Task 6: Manual Smoke Test

- **Discrepancy**: CA1001 fires on a test class that owns an `IAsyncDisposable` factory field but doesn't implement `IDisposable` — even though `IAsyncLifetime.DisposeAsync()` handles disposal.
- **Resolution**: Added `#pragma warning disable CA1001` around the class declaration since disposal is correctly handled by `IAsyncLifetime`.
- **Suggestion**: This is a known pattern in xUnit async lifetime tests. The plan should acknowledge the CA1001 pragma pattern for test classes with async disposal.

## Task 7-8: AuthE2ETestBase + IdpLoginPage

- **Discrepancy**: The plan specified optional parameters for `LoginAsync` (e.g. `string username = "testuser"`), but C# standards require method overloads instead of optional parameters.
- **Resolution**: Created three `LoginAsync` overloads: `(username, password, returnUrl)`, `(username, password)`, and `(returnUrl)`.
- **Suggestion**: Plan should follow the C# coding standards requiring overloads.

- **Discrepancy**: The original `LogoutAsync` implementation looked up CSRF token from either `.WeaveFleet.Antiforgery` or `.WeaveFleet.CSRF`, but the server emits the request token in `.WeaveFleet.CSRF` (non-HttpOnly, JS-readable) and the antiforgery validation cookie in `.WeaveFleet.Antiforgery` (HttpOnly). The `X-CSRF-Token` header must match the request token.
- **Resolution**: Changed cookie lookup to specifically match `.WeaveFleet.CSRF` and added a null check that throws if the cookie is missing.
- **Suggestion**: Plan should reference the actual cookie name from `Program.cs`.

## Task 9-10: OIDC Sign-In/Sign-Out Tests + returnUrl

- No discrepancies — implemented per plan specification.
- **Note**: Tests use `IClassFixture<AuthFleetWebApplicationFactory>` and `IClassFixture<PlaywrightFixture>` for shared factory/browser lifecycle, matching existing E2E test patterns.

## Task 11-12: Onboarding Flow + Refresh Regression Tests

- **Discrepancy**: The plan did not note that onboarding wizard components have **no `data-testid` attributes**. All selectors must use text content, ARIA roles, or element IDs.
- **Resolution**: `OnboardingWizardPage` uses `GetByText`, `GetByRole(AriaRole.Button, ...)`, and `Locator("#onboard-api-key")` instead of `GetByTestId`.
- **Suggestion**: Plan should note the absence of `data-testid` on onboarding components and suggest text/role-based selectors.

- **Discrepancy**: The plan described the credential step as having a "Skip" option, but `canSkip` / `credentialsOptional` depends on `availableHarnesses.includes("claude-code")`. The TestHarness reports `Type = "opencode"`, so `canSkip` is `false` and "Skip for now" is never rendered.
- **Resolution**: Tests save a dummy API key (`sk-ant-test-dummy-key-for-e2e`) to advance past the credential step.
- **Suggestion**: Plan should note the harness type dependency and describe the credential save flow for tests.

## Task 13: CI Workflow Update

- **Discrepancy**: The plan suggested a separate filter or job for auth E2E tests, but the existing E2E filter `Category=E2E&Category!=Benchmark` already captures auth E2E tests because `AuthE2ETestBase` applies both `[Trait("Category", "E2E")]` and `[Trait("Category", "AuthE2E")]`.
- **Resolution**: No filter change needed. Added only a DNS resolution fallback step (`getent hosts auth.dev.localhost` check → `/etc/hosts` entry if missing) before the E2E test run.
- **Suggestion**: Plan should verify existing CI filters before suggesting changes.

## Task 14: Package Management Cleanup

- **Discrepancy**: `Duende.IdentityServer 7.4.7` was already added to `Directory.Packages.props` in Task 2. `System.IdentityModel.Tokens.Jwt` is a transitive dependency of Duende and is not directly referenced by any project, so no additional entry is needed.
- **Resolution**: No changes required. Task verified as already complete.
- **Suggestion**: Plan should note that JWT packages are transitive deps of Duende and only need explicit entries if directly referenced.
