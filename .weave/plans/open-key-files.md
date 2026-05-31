# Open Key Files in Tool

## TL;DR
> **Summary**: Extend "Open in..." context menu so tools like Rider/Visual Studio can open specific project files (`.slnx`, `.csproj`, etc.) directly, not just the workspace directory.
> **Estimated Effort**: Large

## Context
### Original Request
Allow the "Open in..." context menu to detect key project files (solutions, project files, build files) in a workspace and present them as sub-options under compatible tools, so users can open e.g. a `.slnx` directly in Rider rather than just opening the directory.

### Key Findings
- `ToolRegistry` defines tools with `PlatformCommand` where `Args` is `Func<string, string[]>` — currently always receives a directory string. The same func signature works for file paths.
- `GetSpawnInfo(toolId, directory)` builds `ProcessStartInfo` — can be generalized to `GetSpawnInfo(toolId, path)` since most editors/IDEs accept a file path the same way they accept a directory.
- `OpenDirectoryEndpoints` validates paths under allowed workspace roots via `WorkspaceRootService.GetAllowedRootsAsync()` and `IsUnderAllowedRoot()` — same validation applies to files.
- `OpenToolContextSubmenu.vue` currently takes a `directory` prop and renders a flat list of tools. Needs conditional nesting: tools with key files get a submenu, others stay as direct-click items.
- `ToolDefinition` has no concept of "compatible file types" — needs a new field or a separate mapping.
- Tools like Visual Studio, Xcode, Android Studio, CLion are not yet in the registry.

## Objectives
### Core Objective
Enable opening specific project files directly in compatible tools from the context menu, with trumping logic to suppress project-level files when solution-level files exist.

### Deliverables
- [ ] Embedded JSON config defining key file types, compatible tools, and trumping groups
- [ ] Backend service to scan workspace for key files via `git ls-files`
- [ ] New API endpoint `GET /api/key-files?directory=...`
- [ ] New API endpoint `POST /api/open-file` for opening files in tools
- [ ] New tools in registry: Visual Studio, Xcode, CLion, Android Studio
- [ ] Updated frontend submenu with nested file entries per tool
- [ ] Simple Icons integration for brand tool icons

### Definition of Done
- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes all existing + new tests
- [ ] `npm run build` succeeds in client
- [ ] Opening a `.slnx` in Rider from the context menu works end-to-end

### Guardrails (Must NOT)
- Must NOT change behavior for terminals or file explorers (no key file concept)
- Must NOT allow opening files outside allowed workspace roots
- Must NOT make key file detection blocking — menu must open instantly, files load async
- Must NOT break the existing direct-click behavior for tools without key files

## TODOs

- [x] 1. **Add embedded JSON config for key file types**
  **What**: Create `key-file-types.json` as an embedded resource defining file extensions, compatible tool IDs, and trumping groups. Add a `KeyFileConfig` model and loader.
  **Files**:
    - `src/WeaveFleet.Application/Resources/key-file-types.json` (create)
    - `src/WeaveFleet.Application/Services/KeyFileConfig.cs` (create)
    - `src/WeaveFleet.Application/WeaveFleet.Application.csproj` (modify — add EmbeddedResource)
  **Acceptance**: Config loads at startup, deserialization test passes.

  **JSON schema**:
  ```json
  {
    "groups": [
      {
        "id": "dotnet-solution",
        "priority": 1,
        "extensions": [".sln", ".slnx"],
        "compatibleTools": ["visual-studio", "rider"],
        "trumps": ["dotnet-project"]
      },
      {
        "id": "dotnet-project",
        "priority": 2,
        "extensions": [".csproj", ".fsproj", ".vbproj"],
        "compatibleTools": ["visual-studio", "rider"]
      },
      {
        "id": "xcode-workspace",
        "priority": 1,
        "extensions": [".xcworkspace"],
        "compatibleTools": ["xcode"],
        "trumps": ["xcode-project"]
      },
      {
        "id": "xcode-project",
        "priority": 2,
        "extensions": [".xcodeproj"],
        "compatibleTools": ["xcode"]
      },
      {
        "id": "maven",
        "priority": 1,
        "fileNames": ["pom.xml"],
        "compatibleTools": ["intellij", "android-studio"]
      },
      {
        "id": "gradle",
        "priority": 1,
        "fileNames": ["build.gradle", "build.gradle.kts"],
        "compatibleTools": ["intellij", "android-studio"]
      },
      {
        "id": "cmake",
        "priority": 1,
        "fileNames": ["CMakeLists.txt"],
        "compatibleTools": ["clion"]
      },
      {
        "id": "vcxproj",
        "priority": 2,
        "extensions": [".vcxproj"],
        "compatibleTools": ["visual-studio"],
        "trumps": null
      }
    ]
  }
  ```
  Note: `trumps` references another group ID. When a group with `trumps: ["X"]` has matches, group `X` entries are suppressed. The `vcxproj` group is also trumped by `dotnet-solution` — model this by adding `"trumpedBy": ["dotnet-solution"]` or by adding `"vcxproj"` to the solution group's trumps array: `"trumps": ["dotnet-project", "vcxproj"]`.

  **C# model**:
  ```csharp
  public sealed record KeyFileGroup(
      string Id,
      int Priority,
      string[]? Extensions,
      string[]? FileNames,
      string[] CompatibleTools,
      string[]? Trumps);

  public sealed record KeyFileConfig(KeyFileGroup[] Groups);
  ```

- [x] 2. **Add new tools to ToolRegistry**
  **What**: Register Visual Studio, Xcode, CLion, and Android Studio with platform-specific commands. Visual Studio uses `devenv` on Windows. Xcode uses `open` on macOS. CLion and Android Studio use JetBrains launcher scripts.
  **Files**:
    - `src/WeaveFleet.Application/Services/ToolRegistry.cs` (modify)
  **Acceptance**: New tools appear in `BuiltinTools`. Detection finds them when installed.

  **Details**:
  - `visual-studio`: win32 only, command `devenv`, detect `devenv`. Args: `dir => [dir]` for directory, file path works the same way (`devenv Foo.slnx` opens the solution).
  - `xcode`: darwin only, `open -a Xcode <path>`. No detection binary needed (use `open`). Mark `AlwaysAvailable` only on macOS? No — use presence of `/Applications/Xcode.app`. Use custom detection or skip binary detection and check app path existence. Simplest: add a detect entry that probes `xcodebuild` or `xcrun`.
  - `clion`: follows JetBrains pattern identical to `intellij`, command `clion`.
  - `android-studio`: win32 `studio`, darwin `open -a "Android Studio"`, linux `studio`. Detect `studio`.

- [x] 3. **Create KeyFileScanner service**
  **What**: Service that scans a directory for key files using `git ls-files` filtered by configured extensions/filenames. Implements trumping logic and sort order (fewest path segments first, then alphabetical). Caches results with 5-minute TTL keyed by directory.
  **Files**:
    - `src/WeaveFleet.Application/Services/KeyFileScanner.cs` (create)
    - `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (modify — register service)
  **Acceptance**: Unit tests verify: scanning finds files, trumping suppresses correctly, sort order correct, cache works.

  **Algorithm**:
  1. Run `git ls-files` in the directory, capture output.
  2. For each configured group, filter files matching extensions or filenames.
  3. Determine which groups are "active" (have matches).
  4. Apply trumping: if group A trumps group B and group A is active, remove group B's matches.
  5. Build result: for each detected tool, list its compatible key files.
  6. Sort files: by path segment count ascending, then alphabetical.

  **Return type**:
  ```csharp
  public sealed record KeyFileResult(
      IReadOnlyDictionary<string, IReadOnlyList<string>> FilesByToolId);
  // Key = tool ID, Value = sorted list of relative file paths
  ```

  **Cache**: `ConcurrentDictionary<string, (DateTime timestamp, KeyFileResult result)>` with 5-min TTL. Or use same `SemaphoreSlim` pattern as `ToolDetector`.

- [x] 4. **Add `GET /api/key-files` endpoint**
  **What**: New endpoint accepting `directory` query parameter. Validates directory under allowed roots, calls `KeyFileScanner`, returns key files grouped by tool ID.
  **Files**:
    - `src/WeaveFleet.Api/Endpoints/KeyFileEndpoints.cs` (create)
    - `src/WeaveFleet.Api/Program.cs` or startup (modify — map new endpoint group)
  **Acceptance**: Returns correct JSON for a directory containing `.slnx` files. Returns empty for directory with no key files. Rejects paths outside workspace roots.

  **API contract**:
  ```
  GET /api/key-files?directory=/path/to/workspace

  Response 200:
  {
    "filesByTool": {
      "rider": ["WeaveFleet.slnx"],
      "visual-studio": ["WeaveFleet.slnx"]
    }
  }

  Response 400:
  { "error": "Directory does not exist." }
  ```

- [x] 5. **Add `POST /api/open-file` endpoint**
  **What**: New endpoint to open a specific file in a tool. Validates the file path is under allowed workspace roots (reuse `IsUnderAllowedRoot`). Calls `ToolRegistry.GetSpawnInfo(toolId, filePath)` — this already works because `GetSpawnInfo` just passes the path string to `Args` func.
  **Files**:
    - `src/WeaveFleet.Api/Endpoints/OpenDirectoryEndpoints.cs` (modify — add endpoint in same group, or create `OpenFileEndpoints.cs`)
  **Acceptance**: POSTing a valid file + tool spawns the correct process. Invalid paths rejected.

  **API contract**:
  ```
  POST /api/open-file
  { "filePath": "/path/to/WeaveFleet.slnx", "tool": "rider" }

  Response 200: { "ok": true }
  Response 400: { "error": "File does not exist." }
  Response 400: { "error": "Path is outside allowed workspace roots." }
  ```

  **Implementation note**: Very similar to `open-directory` handler. Consider extracting shared path-validation logic into a helper method. The key difference: validate `File.Exists` instead of `Directory.Exists`.

- [x] 6. **Add frontend composable `use-key-files.ts`**
  **What**: Composable that fetches key files for a given directory. Lazy-loaded (fetched when menu opens, not eagerly). Returns reactive state with `filesByTool` map.
  **Files**:
    - `client/src/composables/use-key-files.ts` (create)
  **Acceptance**: Fetches on demand, caches result, exposes loading state.

  **Interface**:
  ```typescript
  interface UseKeyFilesResult {
    filesByTool: Readonly<Ref<Record<string, string[]>>>;
    isLoading: Readonly<ShallowRef<boolean>>;
    error: Readonly<ShallowRef<string | undefined>>;
    fetch: (directory: string) => Promise<void>;
  }
  ```

- [x] 7. **Add frontend composable `use-open-file.ts`**
  **What**: Composable that POSTs to `/api/open-file`. Mirrors `use-open-directory.ts` pattern.
  **Files**:
    - `client/src/composables/use-open-file.ts` (create)
  **Acceptance**: Successfully calls API, handles errors.

  **Interface**:
  ```typescript
  export function useOpenFile(): {
    openFile: (filePath: string, tool: string) => Promise<void>;
    isOpening: Readonly<ShallowRef<boolean>>;
    error: Readonly<ShallowRef<string | undefined>>;
  }
  ```

- [x] 8. **Install Simple Icons and create icon mapping**
  **What**: Install `simple-icons` npm package. Create an icon component or mapping that renders SVG icons for brand tools (VS Code, Rider, Visual Studio, IntelliJ, etc.) and falls back to lucide icons for terminals/explorers.
  **Files**:
    - `client/package.json` (modify — add `simple-icons` dependency)
    - `client/src/components/icons/ToolIcon.vue` (create — component that picks correct icon)
  **Acceptance**: `<ToolIcon tool-id="rider" />` renders the Rider brand icon. Terminals/explorers still use lucide icons.

  **Implementation notes**:
  - Simple Icons exports individual SVGs. Import only needed icons to keep bundle small.
  - Create a mapping: `{ "rider": siJetbrains, "vscode": siVisualstudiocode, ... }`.
  - The `ToolIcon` component accepts `toolId` and `size` props, renders either the simple-icon SVG or a lucide icon.
  - Update `ToolDefinition.IconName` values on the backend? No — keep backend icon names as-is. The frontend mapping uses `toolId` directly to pick the right icon, ignoring `iconName` for tools that have a Simple Icons entry.

- [x] 9. **Redesign `OpenToolContextSubmenu.vue` with nested submenus**
  **What**: Rework the context menu component to support nested submenus for tools that have key files. When key files exist for a tool, clicking the tool opens a submenu with "Open directory" + separator + file entries. Tools without key files remain direct-click. Fetch key files when the "Open in..." submenu opens.
  **Files**:
    - `client/src/components/sessions/OpenToolContextSubmenu.vue` (modify)
  **Acceptance**: Tools with key files show nested submenu. Tools without key files work as before. File entries show relative paths with a file icon.

  **UX details**:
  - Each tool with key files becomes a `ContextMenuSub` with `ContextMenuSubTrigger` (tool name + icon) → `ContextMenuSubContent` containing:
    1. `ContextMenuItem` "Open directory" (with folder icon)
    2. `ContextMenuSeparator`
    3. `ContextMenuItem` per key file (with `FileText` lucide icon), showing relative path
  - Each tool without key files remains a plain `ContextMenuItem` (direct click opens directory).
  - Terminals and explorers remain unchanged (plain `ContextMenuItem`).
  - Key files fetched lazily: trigger fetch in `onMounted` or when the parent "Open in..." submenu opens. Use the `use-key-files` composable.
  - While loading, show a spinner in place of file entries.

- [x] 10. **Backend unit tests**
  **What**: Test KeyFileConfig deserialization, KeyFileScanner trumping/sorting logic, and endpoint validation.
  **Files**:
    - `tests/WeaveFleet.Application.Tests/Services/KeyFileScannerTests.cs` (create)
    - `tests/WeaveFleet.Application.Tests/Services/KeyFileConfigTests.cs` (create)
  **Acceptance**: Tests cover: config loading, trumping (solutions suppress projects), sort order, empty directories, non-git directories.

  **Key test cases**:
  - Directory with `.slnx` and `.csproj` → only `.slnx` returned for rider/visual-studio
  - Directory with only `.csproj` → `.csproj` returned
  - Sort: root files before nested files
  - Non-git directory → graceful fallback (empty result or `Directory.EnumerateFiles` fallback)
  - Files grouped correctly by compatible tool

- [ ] 11. **Integration / E2E smoke test**
  **What**: Verify the full flow: `GET /api/key-files` returns correct data for a test workspace, `POST /api/open-file` validates paths correctly.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/KeyFileEndpointTests.cs` (create)
  **Acceptance**: Endpoint returns expected key files for test fixture directory. Path validation rejects out-of-root paths.

## Verification
- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` — all existing + new tests pass
- [ ] `npm run build` in `client/` succeeds
- [ ] Manual: right-click session → "Open in..." → Rider shows submenu with `.slnx` file → clicking it opens Rider with the solution
- [ ] Manual: tool without key files (e.g. VS Code) still opens directory directly on click
- [ ] Security: attempting to open a file outside workspace roots returns 400
