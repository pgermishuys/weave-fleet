# OpenTelemetry Integration

## TL;DR
> **Summary**: Add OpenTelemetry to the WeaveFleet API — OTLP-exported distributed tracing, metrics, and logging alongside the existing Console log provider, with auto-instrumentation for ASP.NET Core and HttpClient.
> **Estimated Effort**: Short (2-4 hours)

## Context

### Original Request
Introduce OpenTelemetry into the WeaveFleet .NET 10 / C# 14 codebase for full observability: distributed tracing (ActivitySource), metrics (ASP.NET Core built-in meters + custom), and structured log export via OTLP. The existing `Microsoft.Extensions.Logging` Console provider must continue working unchanged.

### Key Findings

**Current state of the codebase:**
- Clean Architecture: Domain → Application → Infrastructure → Api (standard dependency flow)
- `Program.cs` is a minimal-API top-level-statement entry point (~73 lines) using `WebApplication.CreateBuilder`
- DI wiring follows the `AddFleetXxx` extension method pattern (`AddFleetInfrastructure` in `DependencyInjection.cs`)
- Configuration uses `FleetOptions` bound from `"Fleet"` section in `appsettings.json`
- Existing high-perf logging uses `LoggerMessage.Define<T>` in `WebSocketEndpoints.cs` (3 log messages)
- `Directory.Packages.props` manages versions centrally — 4 core + 4 test packages today
- `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, file-scoped namespaces
- `.editorconfig` enforces: `_camelCase` private fields, `Async` suffix on async methods, `var` only when type is apparent
- Domain project is dependency-free (no NuGet packages) — must remain untouched
- Test projects use xunit with `<Using Include="Xunit" />` global using
- No existing `HttpClient` registrations (no `IHttpClientFactory`) — instrumentation will be ready when they arrive

**What does NOT need to change:**
- `WebSocketEndpoints.cs` — `LoggerMessage.Define` patterns work seamlessly with OTEL logging provider
- Domain project — zero changes
- Cli project — deferred (future work)
- Test projects — no new OTEL test packages needed; existing tests remain green

## Objectives

### Core Objective
Wire up OpenTelemetry SDK for traces, metrics, and logs with OTLP export so the API is observable out of the box when an OTLP collector is available, and silently no-ops when it isn't.

### Deliverables
- [x] OpenTelemetry NuGet packages added to `Directory.Packages.props`
- [x] Package references added to `WeaveFleet.Api.csproj`
- [x] `FleetInstrumentation` static class created with `ActivitySource` and custom `Meter`
- [x] `TelemetryOptions` configuration class created
- [x] `AddFleetTelemetry` extension method wiring all OTEL services
- [x] `Program.cs` updated to call `AddFleetTelemetry`
- [x] `appsettings.json` / `appsettings.Development.json` updated with telemetry config
- [x] Solution compiles with zero warnings

### Definition of Done
- [x] `dotnet build` from solution root succeeds with 0 errors and 0 warnings
- [x] `dotnet test` passes all existing tests (no regressions)
- [x] OTEL services are registered in DI (verifiable by inspecting `builder.Services`)
- [x] Setting `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` activates export
- [x] Console log output is unchanged (OTEL adds alongside, doesn't replace)

### Guardrails (Must NOT)
- Do NOT modify the Domain project (`WeaveFleet.Domain`)
- Do NOT modify the Cli project (`WeaveFleet.Cli`) — future work
- Do NOT remove or alter the existing Console logging provider
- Do NOT add OTEL packages to Infrastructure or Application projects (only Api)
- Do NOT use non-sealed classes without justification
- Do NOT add environment-conditional `#if DEBUG` blocks — use config instead

---

## TODOs

- [x] 1. **Add OpenTelemetry package versions to central package management**
  **What**: Add 6 `<PackageVersion>` entries to `Directory.Packages.props` under a new `<!-- Observability -->` comment group, inserted between the existing `<!-- Core -->` and `<!-- Testing -->` groups.
  **Files**: `Directory.Packages.props`
  **Exact changes**:
  Insert after line 9 (`Microsoft.EntityFrameworkCore.Design` entry), before `<!-- Testing -->`:
  ```xml
    <!-- Observability -->
    <PackageVersion Include="OpenTelemetry" Version="1.15.1" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.15.1" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.1" />
    <PackageVersion Include="OpenTelemetry.Exporter.Console" Version="1.15.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.9.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.9.0" />
  ```
  **Acceptance**: `dotnet restore` succeeds; packages appear in lock files

- [x] 2. **Add package references to Api project**
  **What**: Add `<PackageReference>` entries (version-less, per CPM) to `WeaveFleet.Api.csproj`. Add a new `<ItemGroup>` with label comment for observability packages.
  **Files**: `src/WeaveFleet.Api/WeaveFleet.Api.csproj`
  **Exact changes**:
  Insert a new `<ItemGroup>` after the existing `<ItemGroup>` with `<ProjectReference>` entries (after line 6):
  ```xml
  <ItemGroup>
    <PackageReference Include="OpenTelemetry" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Exporter.Console" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
  </ItemGroup>
  ```
  **Acceptance**: `dotnet restore` succeeds for `WeaveFleet.Api`

- [x] 3. **Create `FleetInstrumentation` static class**
  **What**: Create a static class that holds the `ActivitySource` and `Meter` singletons used throughout the application. This is the single source of truth for trace/metric names.
  **Files**: `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs` (new file)
  **Why Application layer**: `ActivitySource` and `Meter` are from `System.Diagnostics` (BCL — no NuGet dependency). The Application layer is the right home because domain services in Application will eventually create spans/metrics. Infrastructure and Api both reference Application, so they can use these singletons.
  **Exact content**:
  ```csharp
  using System.Diagnostics;
  using System.Diagnostics.Metrics;

  namespace WeaveFleet.Application.Diagnostics;

  /// <summary>
  /// Central instrumentation constants and singletons for OpenTelemetry.
  /// Holds the <see cref="ActivitySource"/> for distributed tracing and
  /// <see cref="Meter"/> for custom metrics.
  /// </summary>
  public static class FleetInstrumentation
  {
      /// <summary>Service name used in OTEL resource attributes.</summary>
      public const string ServiceName = "weave-fleet";

      /// <summary>Service version, derived from the assembly informational version.</summary>
      public static readonly string ServiceVersion =
          typeof(FleetInstrumentation).Assembly
              .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
              ?.InformationalVersion ?? "0.0.0";

      /// <summary>ActivitySource for creating distributed trace spans.</summary>
      public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

      /// <summary>Meter for recording custom metrics.</summary>
      public static readonly Meter Meter = new(ServiceName, ServiceVersion);
  }
  ```
  **Acceptance**: `dotnet build src/WeaveFleet.Application` succeeds; no new NuGet dependencies added to Application.csproj

- [x] 4. **Create `TelemetryOptions` configuration class**
  **What**: Add a simple options class for telemetry configuration, following the same pattern as `FleetOptions`. Keeps OTEL configuration explicit in `appsettings.json` alongside standard OTEL env vars.
  **Files**: `src/WeaveFleet.Application/Configuration/TelemetryOptions.cs` (new file)
  **Exact content**:
  ```csharp
  namespace WeaveFleet.Application.Configuration;

  /// <summary>
  /// Configuration options for OpenTelemetry.
  /// Bound from the "Fleet:Telemetry" section in appsettings.json.
  /// Standard OTEL environment variables (OTEL_EXPORTER_OTLP_ENDPOINT, etc.) take precedence.
  /// </summary>
  public sealed class TelemetryOptions
  {
      public const string SectionName = "Fleet:Telemetry";

      /// <summary>Enable OpenTelemetry export. When false, OTEL SDK is not registered. Default: true.</summary>
      public bool Enabled { get; set; } = true;

      /// <summary>OTLP collector endpoint. Default: http://localhost:4317 (gRPC). Override with OTEL_EXPORTER_OTLP_ENDPOINT env var.</summary>
      public string OtlpEndpoint { get; set; } = "http://localhost:4317";

      /// <summary>Write traces/metrics/logs to console (dev only). Default: false.</summary>
      public bool ConsoleExporterEnabled { get; set; }
  }
  ```
  **Design note**: Nesting under `"Fleet:Telemetry"` keeps all Fleet config grouped. The `Enabled` flag lets operators disable OTEL entirely without removing packages. The `OtlpEndpoint` is a fallback — the OTEL SDK natively respects `OTEL_EXPORTER_OTLP_ENDPOINT`, which takes precedence.
  **Acceptance**: `dotnet build src/WeaveFleet.Application` succeeds

- [x] 5. **Create `AddFleetTelemetry` extension method**
  **What**: Create a `TelemetryExtensions` static class with an `AddFleetTelemetry` extension method on `IHostApplicationBuilder` that wires up the OTEL logging provider, tracing, and metrics. This lives in the Api project since it references all OTEL NuGet packages.
  **Files**: `src/WeaveFleet.Api/Telemetry/TelemetryExtensions.cs` (new file)
  **Why Api layer**: The OTEL SDK packages (`OpenTelemetry.Extensions.Hosting`, exporters, instrumentation) are only referenced by the Api project. The extension method needs access to `IHostApplicationBuilder` (composition root concern). Keeping it in Api avoids leaking OTEL package dependencies into Infrastructure.
  **Exact content**:
   ```csharp
   using Microsoft.Extensions.Hosting;
   using OpenTelemetry;
   using OpenTelemetry.Logs;
   using OpenTelemetry.Metrics;
   using OpenTelemetry.Resources;
   using OpenTelemetry.Trace;
   using WeaveFleet.Application.Configuration;
   using WeaveFleet.Application.Diagnostics;

  namespace WeaveFleet.Api.Telemetry;

  /// <summary>
  /// Extension methods for registering OpenTelemetry services (traces, metrics, logs).
  /// </summary>
  public static class TelemetryExtensions
  {
      /// <summary>
      /// Adds OpenTelemetry tracing, metrics, and logging to the host.
      /// Reads configuration from the "Fleet:Telemetry" section and standard OTEL environment variables.
      /// </summary>
      public static IHostApplicationBuilder AddFleetTelemetry(this IHostApplicationBuilder builder)
      {
          var telemetryOptions = builder.Configuration
              .GetSection(TelemetryOptions.SectionName)
              .Get<TelemetryOptions>() ?? new TelemetryOptions();

          if (!telemetryOptions.Enabled)
          {
              return builder;
          }

          // Resource: service name + version + environment
          var resourceBuilder = ResourceBuilder.CreateDefault()
              .AddService(
                  serviceName: FleetInstrumentation.ServiceName,
                  serviceVersion: FleetInstrumentation.ServiceVersion);

          // --- Tracing ---
          builder.Services.AddOpenTelemetry()
              .ConfigureResource(r => r.AddService(
                  FleetInstrumentation.ServiceName,
                  serviceVersion: FleetInstrumentation.ServiceVersion))
              .WithTracing(tracing =>
              {
                  tracing
                      .AddSource(FleetInstrumentation.ServiceName)
                      .AddAspNetCoreInstrumentation()
                      .AddHttpClientInstrumentation()
                      .AddOtlpExporter(otlp =>
                      {
                          otlp.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                      });

                  if (telemetryOptions.ConsoleExporterEnabled)
                  {
                      tracing.AddConsoleExporter();
                  }
              })
              .WithMetrics(metrics =>
              {
                  metrics
                      .AddMeter(FleetInstrumentation.ServiceName)
                      .AddAspNetCoreInstrumentation()
                      .AddHttpClientInstrumentation()
                      .AddOtlpExporter(otlp =>
                      {
                          otlp.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                      });

                  if (telemetryOptions.ConsoleExporterEnabled)
                  {
                      metrics.AddConsoleExporter();
                  }
              });

          // --- Logging ---
          // Add OTEL logging provider ALONGSIDE existing Console provider.
          // The Console provider is configured by default in WebApplication.CreateBuilder;
          // we just add the OTEL provider on top.
          builder.Logging.AddOpenTelemetry(logging =>
          {
              logging.SetResourceBuilder(resourceBuilder);
              logging.IncludeFormattedMessage = true;
              logging.IncludeScopes = true;

              logging.AddOtlpExporter(otlp =>
              {
                  otlp.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
              });

              if (telemetryOptions.ConsoleExporterEnabled)
              {
                  logging.AddConsoleExporter();
              }
          });

          return builder;
      }
  }
  ```
  **Key design decisions in this code**:
  1. `ConfigureResource` + `AddService` on the `OpenTelemetryBuilder` — this sets resource attributes for traces and metrics
  2. Separate `ResourceBuilder` for logging — the logging pipeline has its own resource builder
  3. `IncludeFormattedMessage = true` — ensures the rendered message appears in OTLP, not just structured parameters
  4. `IncludeScopes = true` — propagates logging scopes (e.g., request IDs) through to OTLP
  5. `AddSource(FleetInstrumentation.ServiceName)` — subscribes the OTEL SDK to our custom ActivitySource
  6. `AddMeter(FleetInstrumentation.ServiceName)` — subscribes the OTEL SDK to our custom Meter
  7. `AddAspNetCoreInstrumentation()` — auto-instruments inbound HTTP requests
  8. `AddHttpClientInstrumentation()` — auto-instruments outbound HTTP calls (ready for when `IHttpClientFactory` is added)
  9. `AddOtlpExporter` — default is gRPC on port 4317; env var `OTEL_EXPORTER_OTLP_ENDPOINT` overrides
  10. Early return when `Enabled = false` — zero overhead when OTEL is disabled
  **Acceptance**: File compiles as part of the Api project; no CS warnings

- [x] 6. **Update `Program.cs` to call `AddFleetTelemetry`**
  **What**: Add a single line to `Program.cs` to wire up telemetry. Insert it after `AddFleetInfrastructure` and before `AddHealthChecks`, keeping the logical grouping (config → services → infra → telemetry → health → CORS).
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Exact changes**:
  1. Add `using WeaveFleet.Api.Telemetry;` to the using directives (after the existing usings, line 16)
  2. Add `builder.AddFleetTelemetry();` after `builder.Services.AddFleetInfrastructure(fleetOptions);` (after line 28, before `builder.Services.AddHealthChecks();`)
  The resulting Program.cs should read:
  ```
  using WeaveFleet.Api.Endpoints;
  using WeaveFleet.Api.Telemetry;
  using WeaveFleet.Application.Configuration;
  using WeaveFleet.Infrastructure;

  var builder = WebApplication.CreateBuilder(args);

  // Bind Fleet options
  var fleetOptions = builder.Configuration
      .GetSection(FleetOptions.SectionName)
      .Get<FleetOptions>() ?? new FleetOptions();

  // Configure services
  builder.Services.Configure<FleetOptions>(
      builder.Configuration.GetSection(FleetOptions.SectionName));
  builder.Services.AddFleetInfrastructure(fleetOptions);
  builder.AddFleetTelemetry();
  builder.Services.AddHealthChecks();
  ...
  ```
  **Note**: `AddFleetTelemetry` takes `IHostApplicationBuilder` (which is `builder`), not `builder.Services`. This is intentional — the OTEL hosting extensions operate on the builder, not just the service collection, because they also configure `builder.Logging`.
  **Acceptance**: `Program.cs` compiles; telemetry services are registered

- [x] 7. **Update `appsettings.json` with telemetry configuration**
  **What**: Add the `"Telemetry"` section nested under `"Fleet"` in the base appsettings. Production defaults: enabled but console exporter off.
  **Files**: `src/WeaveFleet.Api/appsettings.json`
  **Exact changes**:
  Modify the `"Fleet"` section to include `"Telemetry"`:
  ```json
  {
    "Fleet": {
      "Port": 3000,
      "Host": "127.0.0.1",
      "Telemetry": {
        "Enabled": true,
        "OtlpEndpoint": "http://localhost:4317",
        "ConsoleExporterEnabled": false
      }
    },
    "Urls": "http://127.0.0.1:3000",
    "Logging": {
      "LogLevel": {
        "Default": "Information",
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "AllowedHosts": "*"
  }
  ```
  **Acceptance**: JSON is valid; config binds correctly to `TelemetryOptions`

- [x] 8. **Update `appsettings.Development.json` with dev telemetry overrides**
  **What**: Enable the console exporter in Development so developers see trace/metric output in the terminal without needing a collector.
  **Files**: `src/WeaveFleet.Api/appsettings.Development.json`
  **Exact changes**:
  ```json
  {
    "Fleet": {
      "Telemetry": {
        "ConsoleExporterEnabled": true
      }
    },
    "Logging": {
      "LogLevel": {
        "Default": "Debug",
        "Microsoft.AspNetCore": "Information"
      }
    }
  }
  ```
  **Acceptance**: JSON is valid; dev override merges cleanly

- [x] 9. **Build and verify compilation**
  **What**: Run `dotnet build` from the solution root and confirm zero errors + zero warnings. Run `dotnet test` to confirm no regressions.
  **Files**: None (verification step)
  **Commands**:
  ```bash
  dotnet build
  dotnet test
  ```
  **Acceptance**: Both commands exit with code 0

---

## Implementation Order

```
Step 1 (Directory.Packages.props)     ← no dependencies
Step 2 (Api.csproj)                   ← depends on Step 1
Step 3 (FleetInstrumentation.cs)      ← no dependencies (pure BCL)
Step 4 (TelemetryOptions.cs)          ← no dependencies (pure BCL)
Step 5 (TelemetryExtensions.cs)       ← depends on Steps 2, 3, 4
Step 6 (Program.cs)                   ← depends on Step 5
Step 7 (appsettings.json)             ← depends on Step 4 (schema match)
Step 8 (appsettings.Development.json) ← depends on Step 7
Step 9 (build + test)                 ← depends on all above
```

Steps 1+3+4 can be done in parallel. Steps 7+8 can be done in parallel.

## Files Changed Summary

| File | Action | Layer |
|------|--------|-------|
| `Directory.Packages.props` | Modify — add 6 package versions | Root |
| `src/WeaveFleet.Api/WeaveFleet.Api.csproj` | Modify — add 6 package references | Api |
| `src/WeaveFleet.Application/Diagnostics/FleetInstrumentation.cs` | **Create** | Application |
| `src/WeaveFleet.Application/Configuration/TelemetryOptions.cs` | **Create** | Application |
| `src/WeaveFleet.Api/Telemetry/TelemetryExtensions.cs` | **Create** | Api |
| `src/WeaveFleet.Api/Program.cs` | Modify — add 2 lines | Api |
| `src/WeaveFleet.Api/appsettings.json` | Modify — add Telemetry section | Api |
| `src/WeaveFleet.Api/appsettings.Development.json` | Modify — add Telemetry override | Api |

**Files NOT changed** (intentionally):
- `src/WeaveFleet.Domain/*` — no dependencies allowed
- `src/WeaveFleet.Infrastructure/*` — no OTEL packages needed
- `src/WeaveFleet.Cli/*` — deferred to future work
- `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs` — LoggerMessage.Define works seamlessly
- `tests/*` — no changes needed; existing tests remain green

## Pitfalls & Mitigations

| Risk | Mitigation |
|------|-----------|
| OTEL SDK fails to connect to collector at startup | OTEL SDK is fire-and-forget by default — connection failures are logged as warnings, not exceptions. No app crash. |
| `TreatWarningsAsErrors` + OTEL analyzer warnings | Pin exact package versions (1.15.1 / 1.9.0) that are known-clean on .NET 10. If warnings appear, add targeted `<NoWarn>` in Api.csproj only. |
| Console exporter floods terminal output in dev | Disabled by default in `appsettings.json`; only enabled in `appsettings.Development.json`. Developer can override with `Fleet:Telemetry:ConsoleExporterEnabled=false` env var. |
| `AddOtlpExporter` gRPC requires HTTP/2 | gRPC on `http://` (not HTTPS) requires `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`. On .NET 10 this is enabled by default for the OTEL SDK's internal gRPC client, but if it causes issues, switch to `OtlpExportProtocol.HttpProtobuf` with port 4318. |
| Duplicate resource builder in traces vs logging | The code intentionally creates resource info in two places (`.ConfigureResource` for traces/metrics, `SetResourceBuilder` for logging) because the OTEL .NET SDK treats the logging pipeline separately. This is the correct pattern per OTEL .NET docs. |

## Future Enhancements (Out of Scope)

These are NOT part of this plan but are natural next steps:
- Add custom spans in endpoint handlers using `FleetInstrumentation.ActivitySource.StartActivity()`
- Add custom metrics (e.g., `sessions.active` counter, `request.duration` histogram) using `FleetInstrumentation.Meter`
- Add OTEL to the Cli project
- Add `Baggage` propagation for cross-service correlation
- Add Exemplars linking metrics to traces
- Add OTEL instrumentation for EF Core when database layer is added

## Verification

- [ ] `dotnet build` exits 0 with no warnings
- [ ] `dotnet test` exits 0 with all tests passing
- [ ] `FleetInstrumentation.ActivitySource.Name` equals `"weave-fleet"`
- [ ] `FleetInstrumentation.Meter.Name` equals `"weave-fleet"`
- [ ] `TelemetryOptions` binds from `Fleet:Telemetry` config section
- [ ] Setting `Fleet:Telemetry:Enabled=false` skips all OTEL registration
- [ ] Console log output is unchanged when OTEL is enabled
- [ ] No new dependencies added to Domain, Infrastructure, or Application NuGet references
