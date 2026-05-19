# PR Refresh Experience

## TL;DR
> **Summary**: Replace hover-to-refresh with a panel-level countdown timer, add user-initiated review comment diagnosis, and remove the ReviewCommentQueue system and backend ReviewCommentWatcherService entirely.
> **Estimated Effort**: Medium

## Context
### Original Request
Improve the smart links refresh UX: add a visual countdown timer, remove hover-to-refresh, add a "diagnose" action for review comments (like CI diagnose), remove the ReviewCommentQueue approval system, and delete the backend ReviewCommentWatcherService.

### Key Findings
- `use-smart-links.ts` polls every 30s via `setInterval` but exposes no countdown state. Returns `void`.
- `SmartLinkItem.vue` has hover-to-refresh (1s delay timer, `refreshing` ref, `LoaderCircle` spinner in CI row).
- `SmartLinksPanel.vue` imports `ReviewCommentQueue`, `useReviewCommentQueue`, and `useReviewCommentQueueStore`.
- `ReviewCommentWatcherService.cs` injects prompts via `sessionOrchestrator.PromptSessionAsync()` — the frontend already fetches review threads during resolution, making this redundant.
- DI registration at `DependencyInjection.cs:132`.
- Files to delete: `ReviewCommentQueue.vue`, `use-review-comment-queue.ts`, `review-comment-queue.ts` (store), `parse-review-proposals.ts`, and related test files.
- The CI diagnose pattern in `SmartLinkItem.vue` (lines 141-222) is the template for the review comment diagnose feature.
- `ReviewThread` type has: `threadNodeId`, `path`, `line`, `comments[]` (each with `body`, `authorLogin`, `url`, `databaseId`).

## Objectives
### Core Objective
Streamline the refresh experience and shift review comment handling from auto-injection to user-initiated action.

### Deliverables
- [ ] Circular arc countdown timer in SmartLinksPanel header
- [ ] Hover-to-refresh removed from SmartLinkItem
- [ ] User-initiated "diagnose" button on review threads
- [ ] ReviewCommentQueue system fully removed (frontend + backend)

### Definition of Done
- [x] `npm run build` succeeds with no errors
- [x] `npm run test` passes (after removing deleted test files)
- [x] `dotnet build` succeeds
- [x] No dangling imports: `grep -r "ReviewCommentQueue\|review-comment-queue\|parse-review-proposals\|ReviewCommentWatcher" --include="*.ts" --include="*.vue" --include="*.cs"` returns nothing
- [x] No regressions in CI diagnose feature
