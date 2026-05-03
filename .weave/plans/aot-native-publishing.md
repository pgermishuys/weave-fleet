# Native AOT Publishing for WeaveFleet.Api

## TL;DR
> **Summary**: Enable `PublishAot=true` for WeaveFleet.Api by remediating trim warnings (source-generated JSON, public request/response types, DI annotations), then enabling trimming, then AOT, and finally integrating into CI.
> **Estimated Effort**: Large

## Context
### Original Request
Enable Native AOT publishing for the WeaveFleet.Api project to produce smaller, faster-starting, single-file executables without a JIT dependency.

### Key Findings
- **Target framework**: net10.0 (good — full AOT support)
- **Trim blockers** (from prior attempt documented in learnings):
  - `RDG012`: Many request/response types in endpoint files are `internal sealed record` — the Request Delegate Generator needs them `public` to source-gen parameter bindings
  - `IL2026`: ~59 call sites use reflection-based `JsonSerializer.Serialize/Deserialize` without source-generated `JsonSerializerContext`
  - `IL2091`: Generic DI registrations (e.g. `AddScoped<IFoo, Bar>()`) lack `[DynamicallyAccessedMembers]` annotations
- **JSON serialization sites span 3 projects**: `WeaveFleet.Api`, `WeaveFleet.Application`, `WeaveFleet.Infrastructure`
- **Third-party packages**:
  - `Dapper` — uses reflection for column mapping; AOT-incompatible without Dapper.AOT source generator
  - `Microsoft.Data.Sqlite` — AOT-compatible (bundles native lib)
  - `dbup-sqlite` — reflection-based migration runner; likely needs `[UnconditionalSuppressMessage]` or alternative
  - `NATS.Net` — has AOT support via source-generated serializers
  - `Microsoft.AspNetCore.Authentication.OpenIdConnect` — AOT-compatible in .NET 9+
  - `OpenTelemetry.*` — AOT support added in 1.7+; verify package versions
- **DI registrations**: ~80+ registrations in `DependencyInjection.cs`, mostly concrete types (safe for trim) but generic interface bindings need annotations
- **Request/Response types**: ~40+ types across `WeaveFleet.Api/Endpoints/` (internal) and `WeaveFleet.Application/DTOs/` (public)
- **Custom JsonConverters**: `ModelRefJsonConverter` in SessionEndpoints.cs, `OpenCodeModelRefConverter` in OpenCodeModels.cs — need AOT-safe rewrites

## Objectives
### Core Objective
Produce a working AOT-published binary for all release RIDs (linux-x64, osx-arm64, win-x64, win-arm64) with zero trim/AOT warnings.

### Deliverables
- [ ] All JSON serialization uses source-generated `JsonSerializerContext`
- [ ] All minimal API request/response types are publicly accessible
- [ ] Trimming passes cleanly (`PublishTrimmed=true`)
- [ ] AOT publish succeeds (`PublishAot=true`)
- [ ] Release workflow uses AOT publish
- [ ] E2E tests pass against AOT binary

### Definition of Done
- [ ] `dotnet publish src/WeaveFleet.Api/WeaveFleet.Api.csproj -c Release -r linux-x64 /p:PublishAot=true` succeeds with zero warnings
- [ ] Smoke test passes (healthz returns 200)
- [ ] All existing tests pass

### Guardrails (Must NOT)
- Must not break the existing development workflow (`dotnet run`)
- Must not change API contracts (HTTP shapes)
- Must not remove functionality to satisfy trimming (suppress only when provably safe)
- Must not force AOT on referenced test projects

## TODOs

- [x] 1. **Add source-generated JsonSerializerContext for Application layer**
  **What**: Create `WeaveFleet.Application/JsonContext.cs` with a `[JsonSerializable]` context covering all DTOs, domain entities, and `MessagePart`/`HarnessEvent` types used in `JsonSerializer` calls across Application and Infrastructure. Replace all `JsonSerializer.Serialize/Deserialize` calls in Application with context-aware overloads.
  **Files**: `src/WeaveFleet.Application/JsonContext.cs`, `src/WeaveFleet.Application/Services/MessagePersistenceService.cs`, `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs`, `src/WeaveFleet.Application/Services/ReasoningFilter.cs`, `src/WeaveFleet.Application/Services/ConfigService.cs`, `src/WeaveFleet.Application/SessionSources/LocalDirectorySessionSourceProvider.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Application` produces zero IL2026 warnings with `<IsTrimmable>true</IsTrimmable>` set

- [x] 2. **Add source-generated JsonSerializerContext for Infrastructure layer**
  **What**: Create `WeaveFleet.Infrastructure/JsonContext.cs` covering OpenCode/ClaudeCode models, NATS event payloads, analytics DTOs, and repository JSON. Replace all `JsonSerializer` calls in Infrastructure with context-aware overloads. Migrate custom `JsonConverter<T>` implementations to be AOT-compatible (use `JsonConverter` factories or type-info-based converters).
  **Files**: `src/WeaveFleet.Infrastructure/JsonContext.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeModels.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHttpClient.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeModels.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeStdioClient.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeMapper.cs`, `src/WeaveFleet.Infrastructure/Nats/NatsEventPublisher.cs`, `src/WeaveFleet.Infrastructure/Nats/WebSocketFanOutSubscriber.cs`, `src/WeaveFleet.Infrastructure/Nats/ProjectionListener.cs`, `src/WeaveFleet.Infrastructure/Services/InProcessOutboxDispatcher.cs`, `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`, `src/WeaveFleet.Infrastructure/Services/HarnessEventPersistenceService.cs`, `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs`, `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`, `src/WeaveFleet.Infrastructure/Analytics/AnalyticsRepository.cs`, `src/WeaveFleet.Infrastructure/SessionSources/RepositorySessionSourceProvider.cs`, `src/WeaveFleet.Infrastructure/SessionSources/GitHubSessionSourceProvider.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Infrastructure` produces zero IL2026 warnings with `<IsTrimmable>true</IsTrimmable>` set

- [x] 3. **Add source-generated JsonSerializerContext for Api layer**
  **What**: Create `WeaveFleet.Api/JsonContext.cs` covering WebSocket message shapes, SSE payloads, and any anonymous types used in endpoint responses. Replace anonymous type responses with named public records where possible (needed for RDG anyway). Wire the context into `builder.Services.ConfigureHttpJsonOptions(...)`.
  **Files**: `src/WeaveFleet.Api/JsonContext.cs`, `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/ClientPayloadSanitizer.cs`, `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Api` produces zero IL2026 warnings

- [x] 4. **Make request/response types public for Request Delegate Generator**
  **What**: Change all `internal sealed record` request/response types in `src/WeaveFleet.Api/Endpoints/*.cs` to `public sealed record`. This resolves RDG012. Types affected: `CreateSessionApiRequest`, `PreviewSessionSourceApiRequest`, `AddSessionSourceApiRequest`, `SendPromptApiRequest`, `ForkSessionApiRequest`, `SendCommandApiRequest`, `RenameWorkspaceRequest`, `AddWorkspaceRootRequest`, `InstallSkillRequest`, `OpenDirectoryRequest`, `ClientConfigResponse`, `UserMeResponse`, `PollRequest`, `BookmarkRequest`, `BookmarkSyncRequest`, all Board*Request/Response types, all Credential*Request/Response types.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/WorkspaceEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/WorkspaceRootEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/OpenDirectoryEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/ConfigEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/UserEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/GitHubAuthEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/GitHubEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/BoardEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/CredentialEndpoints.cs`
  **Acceptance**: `dotnet publish src/WeaveFleet.Api -c Release -r linux-x64 /p:PublishTrimmed=true` produces zero RDG012 warnings

- [x] 5. **Replace anonymous types in endpoint responses with named public records**
  **What**: Minimal API endpoints using anonymous types (e.g. `Results.Ok(new { version = ..., commit = ... })`) cannot be source-generated by RDG/STJ. Extract these into named public record types in a `src/WeaveFleet.Api/Endpoints/Responses/` folder or alongside each endpoint file. Register all in the Api JsonSerializerContext.
  **Files**: `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: Zero anonymous-type `Results.Ok(new { ... })` patterns remain in endpoint handlers

- [x] 6. **Add Dapper.AOT source generator**
  **What**: Add `Dapper.AOT` package reference to Infrastructure. Add `[DapperAot]` attribute to repository classes or assembly-level. This replaces Dapper's runtime reflection with compile-time generated mappers. Verify `DefaultTypeMap.MatchNamesWithUnderscores` behaviour is preserved (Dapper.AOT uses `[Column]` attributes or naming conventions).
  **Files**: `src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProjectRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`
  **Acceptance**: All Dapper queries execute correctly in integration tests; no IL2026/IL2075 warnings from Dapper

- [x] 7. **Annotate DI registrations for trim safety**
  **What**: For generic DI registrations like `AddScoped<IFoo, Bar>()`, the linker needs to know which members to preserve. Add `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]` constraints where needed, or use the concrete-type overloads. Review all registrations in `DependencyInjection.cs` and `Program.cs`.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: `dotnet publish` with trimming produces zero IL2091 warnings

- [x] 8. **Handle dbup-sqlite trim/AOT compatibility**
  **What**: `dbup-sqlite` uses reflection to discover embedded resources and instantiate migration scripts. Add `[DynamicDependency]` attributes or `[UnconditionalSuppressMessage]` with justification on the migration runner call sites. Alternatively, evaluate switching to a trim-safe migration approach (manual resource enumeration).
  **Files**: `src/WeaveFleet.Infrastructure/Data/MigrationRunner.cs`, `src/WeaveFleet.Infrastructure/Analytics/AnalyticsMigrationRunner.cs`
  **Acceptance**: Migrations run successfully in AOT binary; suppressions documented

- [x] 9. **Enable PublishTrimmed and validate**
  **What**: Add `<PublishTrimmed>true</PublishTrimmed>` to the Api csproj (conditional on publish). Run a full publish for linux-x64. Fix any remaining warnings. Run the integration/E2E test suite against the trimmed binary.
  **Files**: `src/WeaveFleet.Api/WeaveFleet.Api.csproj`
  **Acceptance**: `dotnet publish src/WeaveFleet.Api -c Release -r linux-x64 /p:PublishTrimmed=true` succeeds with zero warnings; healthz smoke test passes

- [x] 10. **Enable PublishAot and fix AOT-specific issues**
  **What**: Add `<PublishAot>true</PublishAot>` to the Api csproj. AOT introduces additional constraints beyond trimming: no `Reflection.Emit`, no runtime code generation, stricter generic instantiation requirements. Address any new warnings. Verify OpenTelemetry packages have AOT-compatible versions (≥1.7). Verify NATS.Net serializer configuration is AOT-safe.
  **Files**: `src/WeaveFleet.Api/WeaveFleet.Api.csproj`, `src/WeaveFleet.Api/Telemetry/FleetTelemetry.cs`
  **Acceptance**: `dotnet publish src/WeaveFleet.Api -c Release -r linux-x64 /p:PublishAot=true` succeeds; binary starts and serves /healthz

- [x] 11. **Verify OpenIdConnect AOT compatibility**
  **What**: `Microsoft.AspNetCore.Authentication.OpenIdConnect` gained AOT annotations in .NET 9. Verify it works without warnings in the AOT publish. If warnings remain, add targeted suppressions with justification or conditionally compile the OIDC path.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: AOT publish with auth code path produces no unhandled warnings

- [x] 12. **Update release workflow to use AOT publish**
  **What**: Change the `dotnet publish` step in `.github/workflows/release.yml` to pass `/p:PublishAot=true`. Remove `--self-contained true` (implied by AOT). For cross-compilation (`win-arm64` from `windows-latest`), verify AOT cross-compile works or switch to a native runner. Update smoke tests if binary name changes (AOT produces native executable, not `.dll`+runtime).
  **Files**: `.github/workflows/release.yml`
  **Acceptance**: Release workflow succeeds on all 4 RID matrix entries; smoke tests pass

- [x] 13. **Add AOT publish to CI validation (PR builds)**
  **What**: Add a CI job or step that runs `dotnet publish /p:PublishAot=true` on PRs to catch regressions early. This can be a single RID (linux-x64) to keep CI fast.
  **Files**: `.github/workflows/ci.yml` (or equivalent)
  **Acceptance**: PR CI fails if new AOT-incompatible code is introduced

- [x] 14. **Binary size and startup benchmarking**
  **What**: Document baseline binary size (current self-contained) vs AOT binary. Measure cold-start time (time to first /healthz response). Store in `.weave/learnings/` for future reference.
  **Acceptance**: Measurements documented with methodology

## Verification
- [ ] `dotnet publish src/WeaveFleet.Api/WeaveFleet.Api.csproj -c Release -r linux-x64 /p:PublishAot=true` — zero warnings, produces native binary
- [ ] All unit/integration tests pass (`dotnet test`)
- [ ] Smoke test: AOT binary starts, /healthz returns 200, /version returns correct version
- [ ] Release workflow CI passes on all 4 RIDs
- [ ] No regressions in `dotnet run` development workflow (JIT mode still works)
