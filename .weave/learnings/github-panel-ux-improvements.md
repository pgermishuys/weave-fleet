# Learnings: GitHub Panel UX Improvements

## Task 1: Replace Repo Selector with Filterable Combobox
- **Discrepancy**: The plan referenced `src/plugins/builtin/github/GitHubPanel.vue`, but the actual Vue client source lives under `client/src/...`.
- **Resolution**: Verified the combobox work in `client/src/plugins/builtin/github/GitHubPanel.vue`.
- **Suggestion**: Use `client/src/...` paths in future plans for this workspace.

## Task 2: Create Center Content Detail Views
- **Discrepancy**: The plan referenced `src/components/pages/...`, but the actual page components belong under `client/src/components/pages/...`.
- **Resolution**: Verified the new detail pages in `client/src/components/pages/GitHubIssuePage.vue` and `client/src/components/pages/GitHubPullRequestPage.vue`.
- **Suggestion**: Align page-component paths with the client app root.

## Task 3: Add Dedicated GitHub Routes for Issue/PR Detail
- **Discrepancy**: The plan expected a single `src/routes/github.ts` file, but the app uses TanStack file-based routing under `client/src/routes`, which required separate route files and regenerated route metadata.
- **Resolution**: Verified dedicated issue and pull-request routes in `client/src/routes/github.$owner.$repo.issues.$number.tsx` and `client/src/routes/github.$owner.$repo.pulls.$number.tsx`, plus the supporting `client/src/components/pages/GitHubWorkItemDetailPage.vue` route page.
- **Suggestion**: Reference the file-based route convention directly and note any generated route-tree updates.

## Task 4: Wire Panel Items to Navigate
- **Discrepancy**: The plan again referenced `src/plugins/...`, but the actual components live in `client/src/plugins/...`.
- **Resolution**: Verified in-app navigation in `client/src/plugins/builtin/github/IssueItem.vue` and `client/src/plugins/builtin/github/PullRequestItem.vue`.
- **Suggestion**: Keep plugin file references rooted at `client/src/...` for this app.

## Task 5: Verification
- **Discrepancy**: The plan's final verification required all tests to pass, but the workspace already had unrelated failing Pinia-based tests outside the GitHub feature area.
- **Resolution**: Confirmed GitHub UX changes pass `npm run typecheck` and `npm run build`; separately verified panel/layout boundaries for regressions before proceeding to fix remaining failing tests.
- **Suggestion**: Call out pre-existing failing test files in the plan when full green test runs are not the current repository baseline.

## Task 6: Test Suite Cleanup
- **Discrepancy**: The only remaining unchecked verification item depended on unrelated existing frontend test failures rather than the GitHub feature work itself.
- **Resolution**: Fixed the shared Pinia test setup and aligned session-related expectations so `client/npm run test` passes cleanly.
- **Suggestion**: Separate baseline test-suite repair from feature work when the failures are known in advance.
