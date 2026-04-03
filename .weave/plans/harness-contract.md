# Harness Abstraction Layer — Contract & Registry

## TL;DR
> **Summary**: Define the pluggable harness interfaces (`IHarness`, `IHarnessInstance`, `IHarnessRegistry`), supporting value types, and the concrete registry across Domain → Application → Infrastructure → API layers. Update the frontend `HarnessInfo` type to include `displayName` and `capabilities`.
> **Estimated Effort**: Short (2-4 hours)

## Context

### Original Request
Create the contract layer for the harness abstraction — interfaces, records, enums, and the DI-based registry — so that future concrete harness implementations (OpenCode, Claude Code) can plug in without changing the contract. The `GET /api/harnesses` endpoint should return real `HarnessInfo[]` from the registry instead of an empty stub.

### Key Findings
- **Clean Architecture** is established: `WeaveFleet.Domain` (no deps) → `WeaveFleet.Application` (refs Domain) → `WeaveFleet.Infrastructure` (refs Application) → `WeaveFleet.Api` (refs Application + Infrastructure).
- **Domain/Common/** has `Result<T>`, `FleetError`, `Unit` — convention: one logical group per file, `namespace WeaveFleet.Domain.Common;`.
- **Application/Configuration/** has `FleetOptions` — convention: file-scoped namespace, sealed classes.
- **Infrastructure** has `DependencyInjection.cs` with `AddFleetInfrastructure()` extension method.
- **API/Endpoints/** has `HarnessEndpoints.cs` returning `Array.Empty<object>()` — the stub we're replacing.
- **Frontend** `HarnessInfo` in `client/src/lib/api-types.ts` has `{ name, available, reason? }` — needs `displayName` and `capabilities`.
- **Frontend** `harnesses-tab.tsx` renders `h.name` as the display label — should use `displayName` when present.
- **Build settings**: `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `LangVersion=14` — code must be clean.
- **Tests**: xunit with `<Using Include="Xunit" />` global using, one test class per concept.
- **Domain project** has no package references (pure). `IHarnessInstance` uses `IAsyncEnumerable<T>` and `System.Text.Json.JsonElement` — both are in `net10.0` BCL, no new packages needed.

## Objectives

### Core Objective
Establish the harness abstraction contract so that adding a new harness type requires only: (1) implementing `IHarness`, (2) registering it in DI. Everything else — discovery, API response, frontend adaptation — works automatically.

### Deliverables
- [x] Domain layer: `IHarnessInstance`, value types (`HarnessCapabilities`, `HarnessAvailability`, `HarnessInstanceStatus`, `HarnessEvent`, `HarnessMessage`, `HealthCheckResult`, `HarnessAttachment`)
- [x] Application layer: `IHarness`, `IHarnessRegistry`, DTOs (`HarnessSpawnOptions`, `PromptOptions`, `HarnessInfo`, `HarnessAgent`, `HarnessProvider`, `HarnessModel`)
- [x] Infrastructure layer: `HarnessRegistry` concrete class + DI registration
- [x] API layer: Update `HarnessEndpoints.cs` to inject `IHarnessRegistry` and return real data
- [x] Frontend: Update `HarnessInfo` type and `harnesses-tab.tsx` to use `displayName` and `capabilities`
- [x] Tests for `HarnessRegistry` and domain types

### Definition of Done
- [x] `dotnet build` succeeds with zero warnings across all projects
- [x] `dotnet test` passes all existing + new tests
- [x] `GET /api/harnesses` returns `HarnessInfo[]` (empty array when no harnesses registered — functionally identical to current stub but wired through the registry)
- [x] Frontend TypeScript compiles with no errors (`npm run build` in `client/`)

### Guardrails (Must NOT)
- Do NOT create concrete harness implementations (OpenCodeHarness, ClaudeCodeHarness)
- Do NOT add container/Docker types to the contract
- Do NOT add new NuGet packages (all types use BCL only)
- Do NOT change the API route path (`/api/harnesses` stays the same)
- Do NOT break any existing tests or endpoints

## TODOs

- [x] 1. **Create Domain Harness Types**
  **What**: Add the pure domain types for the harness abstraction — the instance interface, value objects, and enums. These have zero external dependencies.
  **Files**:
  - Create `src/WeaveFleet.Domain/Harnesses/IHarnessInstance.cs`
  - Create `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Domain` succeeds with no warnings.

  **`src/WeaveFleet.Domain/Harnesses/IHarnessInstance.cs`**:
  ```csharp
  using System.Text.Json;

  namespace WeaveFleet.Domain.Harnesses;

  /// <summary>
  /// A running bridge to an AI agent, one per session.
  /// Implementations are created by <c>IHarness.SpawnAsync</c>.
  /// </summary>
  public interface IHarnessInstance : IAsyncDisposable
  {
      /// <summary>Unique identifier for this running instance.</summary>
      string InstanceId { get; }

      /// <summary>The harness type that created this instance (e.g. "opencode").</summary>
      string HarnessType { get; }

      /// <summary>Current lifecycle status.</summary>
      HarnessInstanceStatus Status { get; }

      /// <summary>Gracefully stop the agent process.</summary>
      Task StopAsync(CancellationToken ct);

      /// <summary>Send a user prompt to the agent.</summary>
      Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct);

      /// <summary>Abort the current agent operation.</summary>
      Task AbortAsync(CancellationToken ct);

      /// <summary>Retrieve the message history for this instance.</summary>
      Task<IReadOnlyList<HarnessMessage>> GetMessagesAsync(CancellationToken ct);

      /// <summary>Subscribe to a real-time stream of harness events.</summary>
      IAsyncEnumerable<HarnessEvent> SubscribeAsync(CancellationToken ct);

      /// <summary>Check whether this instance is still healthy.</summary>
      Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct);
  }
  ```

  **`src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`**:
  ```csharp
  using System.Text.Json;

  namespace WeaveFleet.Domain.Harnesses;

  /// <summary>Lifecycle status of a running harness instance.</summary>
  public enum HarnessInstanceStatus
  {
      Starting,
      Running,
      Idle,
      Stopping,
      Stopped,
      Error
  }

  /// <summary>Declares what a harness supports so the frontend can adapt its UI.</summary>
  public sealed record HarnessCapabilities
  {
      public bool RequiresInitialPrompt { get; init; }
      public bool SupportsAgents { get; init; }
      public bool SupportsModelSelection { get; init; }
      public bool SupportsCommands { get; init; }
      public bool SupportsForking { get; init; }
      public bool SupportsResume { get; init; }
      public bool SupportsImageAttachments { get; init; }
      public bool SupportsStreaming { get; init; }
  }

  /// <summary>Whether a harness binary/service is available on this machine.</summary>
  public sealed record HarnessAvailability(bool Available, string? Reason);

  /// <summary>A real-time event emitted by a harness instance.</summary>
  public sealed record HarnessEvent
  {
      public required string Type { get; init; }
      public required string SessionId { get; init; }
      public required DateTimeOffset Timestamp { get; init; }
      public JsonElement? Payload { get; init; }
  }

  /// <summary>
  /// Normalized message from an agent conversation.
  /// Fields are intentionally minimal — concrete shape will be refined
  /// when the first harness implementation is built.
  /// </summary>
  public sealed record HarnessMessage
  {
      public required string Id { get; init; }
      public required string Role { get; init; }
      public required string Content { get; init; }
      public required DateTimeOffset Timestamp { get; init; }
  }

  /// <summary>Result of a health check on a harness instance.</summary>
  public sealed record HealthCheckResult(bool Healthy, string? Message);

  /// <summary>A file or image attached to a prompt.</summary>
  public sealed record HarnessAttachment(string Mime, string Filename, string Data);

  /// <summary>Options for sending a prompt to an agent.</summary>
  public sealed record PromptOptions
  {
      public string? Agent { get; init; }
      public string? ModelId { get; init; }
      public IReadOnlyList<HarnessAttachment>? Attachments { get; init; }
  }
  ```

  > **Note on `PromptOptions`**: This type is placed in Domain because it is a parameter of `IHarnessInstance.SendPromptAsync`, which is a domain interface. Keeping it co-located avoids a circular dependency.

---

- [x] 2. **Create Application-Layer Interfaces and DTOs**
  **What**: Add the application-level contracts (`IHarness`, `IHarnessRegistry`) and the DTOs that reference domain types.
  **Files**:
  - Create `src/WeaveFleet.Application/Harnesses/IHarness.cs`
  - Create `src/WeaveFleet.Application/Harnesses/IHarnessRegistry.cs`
  - Create `src/WeaveFleet.Application/Harnesses/HarnessModels.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Application` succeeds with no warnings.

  **`src/WeaveFleet.Application/Harnesses/IHarness.cs`**:
  ```csharp
  using WeaveFleet.Domain.Harnesses;

  namespace WeaveFleet.Application.Harnesses;

  /// <summary>
  /// Factory and metadata for a harness type.
  /// One instance per harness type is registered in DI.
  /// </summary>
  public interface IHarness
  {
      /// <summary>Machine-readable type identifier, e.g. "opencode", "claude-code".</summary>
      string Type { get; }

      /// <summary>Human-readable display name, e.g. "OpenCode", "Claude Code".</summary>
      string DisplayName { get; }

      /// <summary>Declares what this harness supports.</summary>
      HarnessCapabilities Capabilities { get; }

      /// <summary>Check whether this harness can be used (binary found, auth configured, etc.).</summary>
      Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct);

      /// <summary>Spawn a new agent instance for the given session.</summary>
      Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct);
  }
  ```

  **`src/WeaveFleet.Application/Harnesses/IHarnessRegistry.cs`**:
  ```csharp
  namespace WeaveFleet.Application.Harnesses;

  /// <summary>
  /// DI-based discovery of registered harnesses.
  /// Powers GET /api/harnesses.
  /// </summary>
  public interface IHarnessRegistry
  {
      /// <summary>All registered harnesses.</summary>
      IReadOnlyList<IHarness> GetAll();

      /// <summary>Find a harness by its type identifier, or null if not registered.</summary>
      IHarness? GetByType(string harnessType);

      /// <summary>Check availability of all harnesses (for API response).</summary>
      Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct);
  }
  ```

  **`src/WeaveFleet.Application/Harnesses/HarnessModels.cs`**:
  ```csharp
  using WeaveFleet.Domain.Harnesses;

  namespace WeaveFleet.Application.Harnesses;

  /// <summary>Options for spawning a new harness instance.</summary>
  public sealed record HarnessSpawnOptions
  {
      public required string SessionId { get; init; }
      public required string WorkingDirectory { get; init; }
      public string? InitialPrompt { get; init; }
      public string? Branch { get; init; }
      public IReadOnlyDictionary<string, string> Environment { get; init; }
          = new Dictionary<string, string>();
  }

  /// <summary>
  /// API-facing DTO returned by GET /api/harnesses.
  /// Combines harness metadata with runtime availability.
  /// </summary>
  public sealed record HarnessInfo(
      string Name,
      string DisplayName,
      bool Available,
      string? Reason,
      HarnessCapabilities Capabilities);

  /// <summary>An agent persona exposed by a harness.</summary>
  public sealed record HarnessAgent(string Name, string? Description, string? Mode);

  /// <summary>An AI provider supported by a harness.</summary>
  public sealed record HarnessProvider(string Id, string Name, IReadOnlyList<HarnessModel> Models);

  /// <summary>An AI model within a provider.</summary>
  public sealed record HarnessModel(string Id, string Name);
  ```

---

- [x] 3. **Create HarnessRegistry in Infrastructure**
  **What**: Implement the concrete `HarnessRegistry` that collects `IHarness` instances from DI and checks their availability.
  **Files**:
  - Create `src/WeaveFleet.Infrastructure/Harnesses/HarnessRegistry.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Infrastructure` succeeds with no warnings.

  **`src/WeaveFleet.Infrastructure/Harnesses/HarnessRegistry.cs`**:
  ```csharp
  using WeaveFleet.Application.Harnesses;

  namespace WeaveFleet.Infrastructure.Harnesses;

  /// <summary>
  /// Collects all <see cref="IHarness"/> implementations from DI
  /// and provides lookup + availability checking.
  /// </summary>
  public sealed class HarnessRegistry : IHarnessRegistry
  {
      private readonly IReadOnlyList<IHarness> _harnesses;

      public HarnessRegistry(IEnumerable<IHarness> harnesses)
      {
          _harnesses = harnesses.ToList();
      }

      /// <inheritdoc />
      public IReadOnlyList<IHarness> GetAll() => _harnesses;

      /// <inheritdoc />
      public IHarness? GetByType(string harnessType) =>
          _harnesses.FirstOrDefault(h =>
              string.Equals(h.Type, harnessType, StringComparison.OrdinalIgnoreCase));

      /// <inheritdoc />
      public async Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
      {
          var results = new List<HarnessInfo>(_harnesses.Count);
          foreach (var harness in _harnesses)
          {
              var availability = await harness.CheckAvailabilityAsync(ct).ConfigureAwait(false);
              results.Add(new HarnessInfo(
                  harness.Type,
                  harness.DisplayName,
                  availability.Available,
                  availability.Reason,
                  harness.Capabilities));
          }
          return results;
      }
  }
  ```

---

- [x] 4. **Register HarnessRegistry in DI**
  **What**: Add the `IHarnessRegistry` → `HarnessRegistry` singleton registration to `DependencyInjection.cs`.
  **Files**:
  - Modify `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Infrastructure` succeeds. The registry resolves from DI (verified by existing DI test not breaking).

  **Changes to `DependencyInjection.cs`**:
  Add using + registration:
  ```csharp
  using WeaveFleet.Application.Harnesses;
  using WeaveFleet.Infrastructure.Harnesses;
  // ...inside AddFleetInfrastructure:
  services.AddSingleton<IHarnessRegistry, HarnessRegistry>();
  ```

---

- [x] 5. **Update HarnessEndpoints to Use Registry**
  **What**: Replace the stub `Array.Empty<object>()` with a call to `IHarnessRegistry.GetAvailabilityAsync`. The endpoint becomes async and injects the registry.
  **Files**:
  - Modify `src/WeaveFleet.Api/Endpoints/HarnessEndpoints.cs`
  **Acceptance**: `dotnet build src/WeaveFleet.Api` succeeds. `GET /api/harnesses` returns `[]` (empty array from registry with no harnesses registered — same behavior as before, but now wired through the abstraction).

  **New `HarnessEndpoints.cs`**:
  ```csharp
  using WeaveFleet.Application.Harnesses;

  namespace WeaveFleet.Api.Endpoints;

  public static class HarnessEndpoints
  {
      public static WebApplication MapHarnessEndpoints(this WebApplication app)
      {
          var group = app.MapGroup("/api").WithTags("Harnesses");

          group.MapGet("/harnesses", async (
              IHarnessRegistry registry,
              CancellationToken ct) =>
          {
              var harnesses = await registry.GetAvailabilityAsync(ct);
              return Results.Ok(harnesses);
          })
          .WithName("GetHarnesses");

          return app;
      }
  }
  ```

---

- [x] 6. **Update Frontend HarnessInfo Type**
  **What**: Add `displayName` and `capabilities` to the TypeScript `HarnessInfo` interface. Add the `HarnessCapabilities` interface. Update `harnesses-tab.tsx` to display `displayName` (falling back to `name`) and expose capability info.
  **Files**:
  - Modify `client/src/lib/api-types.ts`
  - Modify `client/src/components/settings/harnesses-tab.tsx`
  **Acceptance**: `npm run build` in `client/` succeeds with no TypeScript errors. The harnesses tab shows the display name when available.

  **Changes to `client/src/lib/api-types.ts`** (replace existing `HarnessInfo`):
  ```typescript
  /** Capabilities declared by a harness — drives adaptive UI. */
  export interface HarnessCapabilities {
    requiresInitialPrompt: boolean;
    supportsAgents: boolean;
    supportsModelSelection: boolean;
    supportsCommands: boolean;
    supportsForking: boolean;
    supportsResume: boolean;
    supportsImageAttachments: boolean;
    supportsStreaming: boolean;
  }

  /** Information about a registered harness */
  export interface HarnessInfo {
    /** Harness name/type, e.g. "opencode" or "claude-code" */
    name: string;
    /** Human-readable display name, e.g. "OpenCode" or "Claude Code" */
    displayName: string;
    /** Whether the harness is currently available (binary found, auth configured, etc.) */
    available: boolean;
    /** Human-readable reason if unavailable */
    reason?: string;
    /** Capabilities this harness supports */
    capabilities: HarnessCapabilities;
  }
  ```

  **Changes to `client/src/components/settings/harnesses-tab.tsx`**:
  - Replace `{h.name}` display labels with `{h.displayName ?? h.name}` (defensive fallback)
  - In the "Available Harnesses" card, show capability badges or a summary line if desired (minimal: just swap the label)

  Specifically, change:
  - Line 65 area: `{h.name}` → `{h.displayName || h.name}`
  - Line 105 area: `{h.name}` → `{h.displayName || h.name}`

---

- [x] 7. **Add Unit Tests for HarnessRegistry**
  **What**: Test `HarnessRegistry` behavior: empty registry, single harness, multiple harnesses, `GetByType` lookup (case-insensitive), and `GetAvailabilityAsync` aggregation.
  **Files**:
  - Create `tests/WeaveFleet.Infrastructure.Tests/Harnesses/HarnessRegistryTests.cs`
  **Acceptance**: `dotnet test tests/WeaveFleet.Infrastructure.Tests` passes all new tests.

  **`tests/WeaveFleet.Infrastructure.Tests/Harnesses/HarnessRegistryTests.cs`**:
  ```csharp
  using WeaveFleet.Application.Harnesses;
  using WeaveFleet.Domain.Harnesses;
  using WeaveFleet.Infrastructure.Harnesses;

  namespace WeaveFleet.Infrastructure.Tests.Harnesses;

  public sealed class HarnessRegistryTests
  {
      [Fact]
      public void GetAll_WhenEmpty_ReturnsEmptyList()
      {
          var registry = new HarnessRegistry([]);
          Assert.Empty(registry.GetAll());
      }

      [Fact]
      public void GetByType_ReturnsMatchingHarness()
      {
          var harness = new FakeHarness("opencode", "OpenCode");
          var registry = new HarnessRegistry([harness]);

          var found = registry.GetByType("opencode");
          Assert.NotNull(found);
          Assert.Equal("opencode", found.Type);
      }

      [Fact]
      public void GetByType_IsCaseInsensitive()
      {
          var harness = new FakeHarness("opencode", "OpenCode");
          var registry = new HarnessRegistry([harness]);

          Assert.NotNull(registry.GetByType("OpenCode"));
          Assert.NotNull(registry.GetByType("OPENCODE"));
      }

      [Fact]
      public void GetByType_ReturnsNullWhenNotFound()
      {
          var registry = new HarnessRegistry([]);
          Assert.Null(registry.GetByType("nonexistent"));
      }

      [Fact]
      public async Task GetAvailabilityAsync_AggregatesAllHarnesses()
      {
          var h1 = new FakeHarness("opencode", "OpenCode", available: true);
          var h2 = new FakeHarness("claude-code", "Claude Code", available: false, reason: "Binary not found");
          var registry = new HarnessRegistry([h1, h2]);

          var results = await registry.GetAvailabilityAsync(CancellationToken.None);

          Assert.Equal(2, results.Count);
          Assert.Equal("opencode", results[0].Name);
          Assert.True(results[0].Available);
          Assert.Equal("Claude Code", results[1].DisplayName);
          Assert.False(results[1].Available);
          Assert.Equal("Binary not found", results[1].Reason);
      }

      /// <summary>Minimal fake for testing the registry — NOT a real harness.</summary>
      private sealed class FakeHarness(
          string type,
          string displayName,
          bool available = true,
          string? reason = null) : IHarness
      {
          public string Type => type;
          public string DisplayName => displayName;
          public HarnessCapabilities Capabilities => new();

          public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
              => Task.FromResult(new HarnessAvailability(available, reason));

          public Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
              => throw new NotSupportedException("FakeHarness cannot spawn.");
      }
  }
  ```

---

- [x] 8. **Add Unit Tests for Domain Types**
  **What**: Basic tests for domain value types — `HarnessCapabilities` defaults, `HarnessEvent` required properties, `HarnessInstanceStatus` enum values, record equality.
  **Files**:
  - Create `tests/WeaveFleet.Domain.Tests/Harnesses/HarnessTypesTests.cs`
  **Acceptance**: `dotnet test tests/WeaveFleet.Domain.Tests` passes all new tests.

  **`tests/WeaveFleet.Domain.Tests/Harnesses/HarnessTypesTests.cs`**:
  ```csharp
  using WeaveFleet.Domain.Harnesses;

  namespace WeaveFleet.Domain.Tests.Harnesses;

  public sealed class HarnessTypesTests
  {
      [Fact]
      public void HarnessCapabilities_DefaultsToAllFalse()
      {
          var caps = new HarnessCapabilities();
          Assert.False(caps.RequiresInitialPrompt);
          Assert.False(caps.SupportsAgents);
          Assert.False(caps.SupportsModelSelection);
          Assert.False(caps.SupportsCommands);
          Assert.False(caps.SupportsForking);
          Assert.False(caps.SupportsResume);
          Assert.False(caps.SupportsImageAttachments);
          Assert.False(caps.SupportsStreaming);
      }

      [Fact]
      public void HarnessCapabilities_WithInitReturnsNewInstance()
      {
          var caps = new HarnessCapabilities { SupportsStreaming = true, RequiresInitialPrompt = true };
          Assert.True(caps.SupportsStreaming);
          Assert.True(caps.RequiresInitialPrompt);
          Assert.False(caps.SupportsAgents);
      }

      [Fact]
      public void HarnessAvailability_RecordEquality()
      {
          var a = new HarnessAvailability(true, null);
          var b = new HarnessAvailability(true, null);
          Assert.Equal(a, b);
      }

      [Fact]
      public void HarnessInstanceStatus_HasExpectedValues()
      {
          var values = Enum.GetValues<HarnessInstanceStatus>();
          Assert.Equal(6, values.Length);
          Assert.Contains(HarnessInstanceStatus.Starting, values);
          Assert.Contains(HarnessInstanceStatus.Error, values);
      }

      [Fact]
      public void HealthCheckResult_RecordEquality()
      {
          var a = new HealthCheckResult(true, null);
          var b = new HealthCheckResult(true, null);
          Assert.Equal(a, b);
      }

      [Fact]
      public void HarnessMessage_RequiresAllProperties()
      {
          var msg = new HarnessMessage
          {
              Id = "msg-1",
              Role = "assistant",
              Content = "Hello",
              Timestamp = DateTimeOffset.UtcNow
          };
          Assert.Equal("assistant", msg.Role);
      }
  }
  ```

---

- [x] 9. **Verify Full Build and Test Suite**
  **What**: Run the complete build and test suite to ensure no regressions.
  **Files**: None (verification only).
  **Acceptance**:
  - `dotnet build` at solution root — 0 warnings, 0 errors
  - `dotnet test` at solution root — all tests pass
  - `npm run build` in `client/` — TypeScript compiles cleanly

## Verification
- [x] `dotnet build` succeeds with zero warnings (enforced by `TreatWarningsAsErrors`)
- [x] `dotnet test` passes all existing + new tests
- [x] `GET /api/harnesses` returns `[]` (same behavior, now wired through registry)
- [x] `npm run build` in `client/` succeeds with no TypeScript errors
- [x] No new NuGet packages were added
- [x] Domain layer has zero project/package references (stays pure)
