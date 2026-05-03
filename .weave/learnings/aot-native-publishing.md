# Learnings: AOT Native Publishing

## Task 5: Replace anonymous types in endpoint responses with named public records

- **Discrepancy**: Plan listed only `FleetEndpoints.cs`, `SessionEndpoints.cs`, `Program.cs` as files needing changes. Reality: every endpoint file had anonymous types in `Results.BadRequest/NotFound/Conflict(new { error = "..." })` error responses as well.
- **Resolution**: Updated all endpoint files, not just the 3 listed. Used PowerShell regex to batch-replace `new { error = ... }` patterns in BoardEndpoints, CredentialEndpoints, OpenDirectoryEndpoints, SessionEventEndpoints, ProjectEndpoints, GitHubEndpoints.
- **Suggestion**: Plan should list ALL files with anonymous types, including error response patterns, not just success response patterns.

- **Discrepancy**: `SessionOriginDto` was defined in both `WeaveFleet.Application.DTOs` and `ApiResponses.cs` — caused ambiguous reference. 
- **Resolution**: Removed duplicate from `ApiResponses.cs`, used Application DTO by adding `using WeaveFleet.Application.DTOs` to ApiResponses.cs and ApiJsonContext.cs.
- **Suggestion**: Check Application.DTOs for existing shared types before defining new ones in Api layer.

- **Discrepancy**: `IntegrationItem.ConnectedAt` was typed `string?` in plan but is `DateTimeOffset?` in domain (`PluginStatus.ConnectedAt`).
- **Resolution**: Changed record to use `DateTimeOffset?`.

- **Discrepancy**: `SessionSourceKey.ContractVersion` is `int` in Application domain, not `string`.
- **Resolution**: Changed record to use `int`.

- **Discrepancy**: `GetSessionDiffsResponse` originally typed with `IReadOnlyList<object>`. AOT cannot serialize `object`. Always empty in practice.
- **Resolution**: Changed to `IReadOnlyList<JsonElement>` to be AOT-safe.

## Task 7: Annotate DI registrations / suppress IL2026

- **Discrepancy**: Plan described this as "annotate DI registrations with `[DynamicallyAccessedMembers]`". Reality: the actual blocker was ~246 IL2026 errors across endpoint files — NOT just DI registrations.
- **Resolution**: The IL2026 on `MapGet/MapPost/etc.` inside Api endpoint files is a **false positive** from the Roslyn trim analyzer. In a `Microsoft.NET.Sdk.Web` project, the Request Delegate Generator (RDG) generates `[InterceptsLocation]` source that replaces reflection-based `MapX` overloads with trim-safe versions. The Roslyn trim analyzer fires before interceptors apply. Fix: `#pragma warning disable IL2026` wrapping each file's body (added after the file-scoped namespace declaration). This is the correct suppression for Roslyn-time IL2026 on RDG-intercepted calls.
- **Suggestion**: Plan should describe the two distinct categories: (1) endpoint Map* calls — suppress with pragma because RDG makes them safe; (2) real reflection uses like `IConfiguration.Get<T>()` and `Configure<TOptions>()` in Program.cs — also suppress with pragma since the types are simple POCOs with only primitive properties.

- **Discrepancy**: PowerShell `[System.IO.File]::WriteAllLines` with empty `$lines` array zeroed out 23 files in a failed batch edit. `Get-Content` must be used instead of `ReadAllLines` — the latter returned null/empty on Windows with UTF-8 BOM files.
- **Resolution**: Restored files from git checkout, re-ran script using `Get-Content` + `Set-Content`.
- **Suggestion**: In PowerShell batch edits, always use `Get-Content` / `Set-Content` instead of `[System.IO.File]::ReadAllLines` / `WriteAllLines`. Always check that the in-memory list is non-empty before writing.

- **Discrepancy**: `ApiJsonContext` was inaccessible in Program.cs (CS0103) even though both are in the WeaveFleet.Api assembly. Top-level statements have no implicit namespace, so `WeaveFleet.Api.ApiJsonContext` needs `using WeaveFleet.Api;` or a fully qualified name.
- **Resolution**: Added `using WeaveFleet.Api;` to Program.cs.

- **Discrepancy**: GitHub response types (`GitHubDeviceCodeApiResponse`, etc.) were added as nested types inside `internal static class GitHubEndpointMappings`. Nested types of an `internal` class are inaccessible from `ApiJsonContext` in the Api project.
- **Resolution**: Moved response types to file-level declarations (still in the same namespace `WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub`) as `public sealed record`.
- **Suggestion**: Always declare response types at file/namespace level, not nested inside classes, when they need to be registered in a different assembly's `JsonSerializerContext`.

## Task 14: Binary size and startup benchmarking

- **Discrepancy**: Cannot measure AOT binary size or startup time on the dev machine (Windows, no C++ build tools installed; cross-OS AOT compilation not supported by .NET toolchain).
- **Resolution**: Deferred to CI. The AOT publish CI job (added in Task 13) runs on `ubuntu-latest` and produces the native binary. Measurements should be taken from that artifact.
- **Methodology**:
  - **Binary size**: Check artifact size of `WeaveFleet.Api` (native ELF/PE) vs prior self-contained publish in the release artifacts.
  - **Startup time**: In smoke test step, measure time from process start to first successful `/healthz` response using `date +%s%N` before/after the curl loop on Linux.
  - **Baseline** (self-contained, linux-x64, pre-AOT): record from the last release artifact before this branch lands.
- **Suggestion**: Add a CI step that prints binary size (`ls -lh artifacts/publish/linux-x64/WeaveFleet.Api`) and measures startup time in the smoke test.
