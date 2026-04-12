# Learnings: Auth0 Hosted Signup

## Tasks 1-5: Auth0 Setup (Manual)
- **Discrepancy**: Plan treated these as tasks to check off — but they're manual Auth0 dashboard steps with no code involved.
- **Resolution**: Marked as complete with a note that they're human-action tasks.
- **Suggestion**: Future plans should label manual/human steps explicitly to avoid confusion with automated tasks.

## Task 6: Create Public Login Page Component
- **Discrepancy**: Plan said to use `apiUrl()` with a precomputed href including returnUrl. The `useSearchParams` hook from `react-router` is cleaner for reading the returnUrl from the URL.
- **Resolution**: Used `useSearchParams` + `useMemo` to compute `signInHref`, then used it for both "Sign in" and "Sign up" buttons as specified.
- **Suggestion**: Plan detail was accurate; no changes needed.

## Tasks 7+8: App Router + AuthGate Update
- **Discrepancy**: Plan described Tasks 7 and 8 as separate but they're both in `app.tsx`. Task 7 (route structure) and Task 8 (loginUrl change) were done in one edit pass.
- **Resolution**: Combined into one edit session since they're in the same file.
- **Suggestion**: No change needed — noting that co-located changes in the same file can be done together.

## Task 9: Post-Logout Redirect
- **Discrepancy**: The `returnUrl` memo and `useMemo` import were both unused after the change.
- **Resolution**: Removed `returnUrl` useMemo and `useMemo` import from `header.tsx`.
- **Suggestion**: Plan should note that removing the memo also requires cleaning up unused imports.

## Task 11: E2E Test Regressions
- **Discrepancy**: Plan said "this should be trivial — no backend code changed." But the AuthGate change (redirect to `/login` instead of directly to IdP) broke all E2E auth tests because `AuthE2ETestBase.LoginAsync` expected to land directly on the IdP login page.
- **Resolution**: Created `FleetLoginPage.cs` page object and updated `LoginAsync` in `AuthE2ETestBase` to click through the new `/login` landing page before proceeding to the IdP. Added a bypass for the `/auth/login` direct-navigation case (used by deep-link test).
- **Suggestion**: The plan's guardrail "Must NOT break the Duende-based E2E test infrastructure" should have flagged this as a required E2E helper update. The plan should have included an explicit task: "Update E2E test helpers to handle the new /login landing page in the auth flow."

## Pre-existing Failures
- `GetSkill_ReturnsBadRequestOrNotRouted_ForEncodedTraversal` was already failing before any changes (confirmed by git stash + test run).
- Our changes introduced zero new test failures.
