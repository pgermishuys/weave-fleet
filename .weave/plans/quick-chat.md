# Quick Chat

## TL;DR
Add a "Quick Chat" option to the New Session page that creates a temp server-side directory and immediately starts a session, removing the need to pick a workspace for simple conversations.

## Context
Session creation requires a workspace directory resolved through the `SessionSourceResolutionService` provider pipeline. The system has four providers: `builtin.local` (directory), `builtin.repository`, `builtin.github`, `builtin.automation`. Each implements `ISessionSourceProvider` and returns a `ResolvedSessionSource` with a `WorkspaceIntent` (directory, isolation strategy, branch).

The UI (`NewSessionForm.vue`) presents four "Where to run" mode cards (`new-worktree`, `existing-worktree`, `repository`, `directory`). The computed `sessionSource` builds a `SessionSourceSelection` with the appropriate provider key and input payload. The form gates submission on `sessionSource` being defined and `validationMessage` being null.

The cleanest integration point is a new `ISessionSourceProvider` (`QuickChatSessionSourceProvider`) that creates a temp directory on disk and returns it as the workspace. This follows the existing provider pattern exactly and requires no changes to the orchestrator or resolution service.

### Key files
- `src/WeaveFleet.Application/SessionSources/SessionSourceContracts.cs` -- provider IDs, type names, catalog
- `src/WeaveFleet.Application/SessionSources/ISessionSourceProvider.cs` -- provider interface
- `src/WeaveFleet.Application/SessionSources/LocalDirectorySessionSourceProvider.cs` -- reference provider impl
- `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs` -- resolution pipeline
- `src/WeaveFleet.Application/JsonContext.cs` -- source-gen JSON context
- `src/WeaveFleet.Infrastructure/DependencyInjection.cs` -- provider DI registration
- `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` -- API request records
- `client/src/components/sessions/NewSessionForm.vue` -- session creation form
- `client/src/composables/use-session-actions.ts` -- `useCreateSession` composable

## Scope
- In scope:
  - New `QuickChat` session source provider (server-side temp directory creation)
  - New provider ID, source type, and catalog descriptor constants
  - "Quick Chat" card on `NewSessionForm.vue` that auto-submits
  - JSON context registration for the new input type
  - DI registration of the new provider
  - Unit tests for the new provider
- Out of scope:
  - Cleanup/garbage collection of old quick chat directories (follow-up)
  - Separate quick chat section in the session list
  - "Promote to full session" capability
  - Changes to `NewSessionDialog.vue` (only the full-page form)
- Constraints / assumptions:
  - Temp directories created at `~/.weave-fleet/quick-chats/{session-id}/` (or platform equivalent via `Environment.GetFolderPath`)
  - The provider creates the directory during `ResolveAsync`, before the orchestrator tries to use it
  - Quick chat sessions use `existing` isolation strategy (no git, no worktree)
  - The `WorkspaceRootService.ResolvePathWithinAllowedRootsAsync` check must be bypassed for quick chat since the path is server-generated (the new provider does not call it)

## Objectives
- Users can start a chat session from the New Session page with a single click
- No changes to `SessionOrchestrator`, `SessionSourceResolutionService`, or the API endpoint mapping logic
- Follows the existing provider pattern for maintainability

## Dependencies and Order
1. Backend constants and contracts first (Tasks 1-2) because the provider and UI both reference them.
2. Provider implementation (Task 3) depends on contracts.
3. DI registration (Task 4) depends on provider class existing.
4. JSON context (Task 5) depends on input record existing.
5. UI changes (Task 6) can start in parallel with backend but must reference the correct provider ID and source type strings.
6. Tests (Task 7) depend on all backend tasks.

## Tasks

- [x] 1. Add quick chat constants to `SessionSourceContracts.cs`
  - **What**: Add `QuickChat = "builtin.quickchat"` to `SessionSourceProviderIds`. Add `QuickChat = "quick-chat"` to `SessionSourceTypeNames`. Add a `QuickChatStartSession` descriptor to `SessionSourceCatalog` with no required input fields (empty input fields list), `ProducesWorkspace: true`, `ProducesContext: false`, `RequiresConfirmation: false`. Add it to `CoreDescriptors`.
  - **Files**: `src/WeaveFleet.Application/SessionSources/SessionSourceContracts.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `SessionSourceProviderIds.QuickChat` exists and equals `"builtin.quickchat"`
    - `SessionSourceTypeNames.QuickChat` exists and equals `"quick-chat"`
    - `SessionSourceCatalog.QuickChatStartSession` descriptor exists with correct key
    - `CoreDescriptors` includes the new descriptor

- [x] 2. Create the `QuickChatSourceInput` record
  - **What**: Add an internal sealed record `QuickChatSourceInput` (empty, no properties needed since the server generates everything). Register it in `ApplicationJsonContext` with `[JsonSerializable(typeof(QuickChatSourceInput))]`.
  - **Files**: `src/WeaveFleet.Application/JsonContext.cs`
  - **Depends on**: None
  - **Acceptance**:
    - `QuickChatSourceInput` record exists in the `WeaveFleet.Application` namespace
    - `ApplicationJsonContext` has the `[JsonSerializable]` attribute for it

- [x] 3. Create `QuickChatSessionSourceProvider`
  - **What**: New class implementing `ISessionSourceProvider` in the `WeaveFleet.Application/SessionSources/` directory. `ProviderId` returns `SessionSourceProviderIds.QuickChat`. `GetDescriptors()` returns `[SessionSourceCatalog.QuickChatStartSession]`. `ResolveAsync` does the following:
    1. Validate the selection key matches `QuickChatStartSession.Key`
    2. Determine the base path: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave-fleet", "quick-chats")`
    3. Generate a unique directory name using `Guid.NewGuid().ToString("N")` (not session ID since it is not available here)
    4. Create the directory with `Directory.CreateDirectory(fullPath)`
    5. Return a `ResolvedSessionSource` with:
       - Descriptor: `SessionSourceCatalog.QuickChatStartSession`
       - `WorkspaceIntent`: the created directory, `"existing"` isolation, no branch
       - No `ContextEnvelope`
       - `ProvenanceRecord` with provider ID, source type `"quick-chat"`, action `"start-session"`, title `"Quick Chat"`
  - **Files**: `src/WeaveFleet.Application/SessionSources/QuickChatSessionSourceProvider.cs`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Class compiles and implements `ISessionSourceProvider`
    - `ResolveAsync` creates a directory under `~/.weave-fleet/quick-chats/`
    - Returns a valid `ResolvedSessionSource` with a `WorkspaceIntent`
    - Does NOT call `WorkspaceRootService.ResolvePathWithinAllowedRootsAsync`

- [x] 4. Register the provider in DI
  - **What**: Add `services.AddSingleton<ISessionSourceProvider, QuickChatSessionSourceProvider>();` in `DependencyInjection.cs` alongside the other provider registrations.
  - **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - **Depends on**: Task 3
  - **Acceptance**:
    - The provider is registered in the DI container
    - Registration is near the other `ISessionSourceProvider` registrations (around line 141-144)

- [x] 5. Add "Quick Chat" card to `NewSessionForm.vue`
  - **What**:
    1. Add `"quick-chat"` to the `WhereToRunMode` type union
    2. Add a new card button at the TOP of the "Where to run" grid (before "New worktree"). Use the same card pattern as existing buttons. Label: "Quick Chat". Description: "Start chatting without picking a project or directory".
    3. Update `sourceKind` computed: when `whereToRunMode === 'quick-chat'`, return a new value (or keep `"directory"` -- does not matter since `sessionSource` handles it)
    4. Update `sessionSource` computed: add a branch at the top that returns a `SessionSourceSelection` when `whereToRunMode.value === 'quick-chat'`:
       ```ts
       {
         key: {
           providerId: "builtin.quickchat",
           sourceType: "quick-chat",
           actionId: "start-session",
           contractVersion: 1,
         },
         input: {},
       }
       ```
    5. Update `validationMessage` computed: when `whereToRunMode === 'quick-chat'`, return `null` (always valid)
    6. When the quick-chat card is selected, auto-submit: add a watcher that calls `handleSubmit()` on `nextTick` when `whereToRunMode` changes to `'quick-chat'`. This gives the user a one-click experience.
    7. The GitHub preset card should disable the quick-chat option (same as directory mode).
  - **Files**: `client/src/components/sessions/NewSessionForm.vue`
  - **Depends on**: Task 1 (for provider ID strings)
  - **Acceptance**:
    - "Quick Chat" card appears as the first option in the "Where to run" section
    - Clicking it immediately creates a session and navigates to it
    - No directory/repository/branch fields are shown when quick-chat is selected
    - The card is disabled when a GitHub preset is active
    - Form validation passes without any user input for quick-chat mode

- [x] 6. Write unit tests for `QuickChatSessionSourceProvider`
  - **What**: Create a test class in `tests/WeaveFleet.Application.Tests/Services/` (or `SessionSources/` if that directory exists). Test:
    1. `ResolveAsync` with matching key returns success and creates a directory
    2. `ResolveAsync` with non-matching key returns validation error
    3. The created directory actually exists on disk after resolve
    4. `GetDescriptors()` returns exactly one descriptor matching the catalog entry
    5. Clean up created directories in test teardown
  - **Files**: `tests/WeaveFleet.Application.Tests/SessionSources/QuickChatSessionSourceProviderTests.cs`
  - **Depends on**: Tasks 1, 2, 3
  - **Acceptance**:
    - All tests pass with `dotnet test tests/WeaveFleet.Application.Tests --filter "FullyQualifiedName~QuickChatSessionSourceProvider"`
    - Tests clean up temp directories

## Verification
1. Backend builds: `dotnet build src/WeaveFleet.Api -c Debug`
2. Backend tests pass: `dotnet test tests/WeaveFleet.Application.Tests --filter "FullyQualifiedName~QuickChatSessionSourceProvider"`
3. Frontend builds: `cd client && bun run build`
4. Frontend type-checks: `cd client && bunx vue-tsc --noEmit`
5. Manual smoke test: open New Session page, click "Quick Chat" card, verify session starts immediately and conversation panel loads
