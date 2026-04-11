# Learnings: cloud-track-2-runtime-credentials

## Task 26: Frontend credential settings page

- **Discrepancy**: Plan specified `client/src/app/settings/credentials/page.tsx` as a standalone page, but the existing settings shell uses in-page tabs (not Next.js nested routing). The page was created as a `CredentialsTab` component exported from that path, and surfaced via a new "API Keys" tab in the main settings page.
- **Resolution**: Exported `CredentialsTab` from the credentials page file; imported it into `app/settings/page.tsx`. Tab conditionally renders only when `clientConfig.authEnabled`.
- **Suggestion**: Plan should clarify whether the credentials page is a new route or a new tab on the existing settings page.

## Task 27: Onboarding wizard

- **Discrepancy**: The plan mentioned completing onboarding marks `OnboardingCompletedAt`, but no `POST /api/user/me/complete-onboarding` endpoint existed. The wizard needed to call something to persist completion.
- **Resolution**: Added `CompleteOnboardingAsync` to `UserService` and wired a new `POST /api/user/me/complete-onboarding` endpoint in `UserEndpoints.cs`. The wizard calls this after the credential or skip step.
- **Suggestion**: Plan should list the complete-onboarding endpoint as a deliverable alongside the status endpoint.

## Task 28: Simplify session creation UI for cloud mode

- **Discrepancy**: The directory picker was already hidden in cloud mode via `visibleSources` filter. The main gap was showing guidance when a harness validation error occurs.
- **Resolution**: Added a "Add an API key in Settings" link that appears inside the error banner when `isCloudMode` is true and a session-create error exists.
- **Suggestion**: Plan is accurate; implementation was simpler than expected since most work was already done.

## Task 29: Login/logout UI chrome

- **Discrepancy**: The `Header` component already had a full `UserMenu` with avatar, name, email, and sign-out button as part of earlier Track 1 work. Task 29 was effectively already complete.
- **Resolution**: Verified the implementation is complete; marked the task done without additional code.
- **Suggestion**: Plan should note that login/logout chrome may already be implemented by Track 1 headers work.
