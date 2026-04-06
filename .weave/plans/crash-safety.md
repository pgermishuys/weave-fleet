# Crash-Safety Hardening

## TL;DR
> **Summary**: Harden the Fleet backend so that crash (OOM, kill -9, power loss) is the assumed failure mode — not graceful shutdown. Simplify drain logic, add shutdown timeouts, document the crash-safety contract.
> **Estimated Effort**: Short

## Context
### Original Request
Tighten crash-safety for the ASP.NET Core 10 Fleet backend. The process can crash at any time and graceful shutdown is never guaranteed. The startup recovery path (`Program.cs` lines 72-81) already reconciles stale DB state. This plan addresses remaining gaps: analytics drain complexity, OTEL flush timeout, host shutdown timeout, child process orphaning, and documentation.

### Key Findings

1. **AnalyticsWriterService** (`src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`): Buffers events in a `Channel<AnalyticsEventEnvelope>` (capacity 10,000 in `AnalyticsCollector`, flush interval 2s, max batch 50). The `DrainRemainingAsync` method (lines 93-101) runs during graceful shutdown to flush remaining buffered events. On crash, these are lost. The channel already uses `DropOldest` — analytics are already best-effort. The drain is low-complexity (just `TryRead` + flush) but the `catch (OperationCanceledException)` on line 82-86 has a subtlety: `stoppingToken` is already cancelled when it catches, so passing it to `DrainRemainingAsync` is safe (it's not used inside). The drain call is fine as-is — it's fast, synchronous reads only.

2. **OTEL SDK shutdown** (`src/WeaveFleet.Api/Telemetry/TelemetryExtensions.cs`): Using OTEL .NET SDK 1.15.1. The SDK registers an `IHostedService` that calls `TracerProvider.Shutdown()` and `MeterProvider.Shutdown()` during host stop. These internally call `ForceFlush` on the batch processors. The OTLP exporter has a default export timeout of 30s (`OTEL_BSP_EXPORT_TIMEOUT`). If the collector is unreachable, shutdown blocks for up to 30s per signal. The SDK does NOT expose a `ShutdownTimeout` property on the provider builders. The correct approach is to set `OTEL_BSP_EXPORT_TIMEOUT` and `OTEL_BLRP_EXPORT_TIMEOUT` environment variables or configure `BatchExportProcessorOptions.ExporterTimeoutMilliseconds` via the `AddOtlpExporter` overload.

3. **Host shutdown timeout** (`src/WeaveFleet.Api/Program.cs`): No `HostOptions.ShutdownTimeout` is configured. Default is 30 seconds. This is the total budget for all `IHostedService.StopAsync` calls plus `IHostedLifecycleService.StoppingAsync/StoppedAsync`. With the OTEL flush potentially consuming 30s and harness shutdown taking up to 10s, the worst case is much longer than desirable.

4. **Child process orphaning**: Both `ClaudeCodeProcessManager` and `OpenCodeProcessManager` implement `IAsyncDisposable` with `StopAsync` + `Kill`. The harness instances (`ClaudeCodeHarnessInstance`, `OpenCodeHarnessInstance`) also implement `IAsyncDisposable` and call through to process managers. However, `InstanceTracker` (singleton) does NOT implement `IDisposable`/`IAsyncDisposable` and is not disposed at shutdown. Neither is there a hosted service that iterates tracked instances and disposes them. On graceful shutdown, the `HarnessEventRelay` cancels subscriptions but does NOT stop/dispose instances. **On crash, child processes are orphaned.** No Job Object or process group is used. On Linux, orphans get reparented to init/systemd. On Windows, orphans persist indefinitely.

5. **Startup recovery** (`Program.cs` lines 72-81, `InstanceService.MarkAllStoppedAsync`): Correctly marks all DB records as stopped. But orphaned OS processes from a previous crash are NOT killed — only DB state is reconciled. The PIDs are stored in the `instances` table (`Pid` column) but no code attempts to kill stale processes at startup.

## Objectives
### Core Objective
Ensure crash is a safe, recoverable event — and that graceful shutdown completes quickly without hanging.

### Deliverables
- [ ] Document analytics as best-effort with explicit comment, simplify drain path
- [ ] Configure OTEL export timeout to 3 seconds so shutdown isn't blocked by unreachable collector
- [ ] Reduce host shutdown timeout to 10 seconds
- [ ] Document the crash-safety contract in `Program.cs`
- [ ] Document child process orphaning as a known/accepted behavior

### Definition of Done
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with no warnings
- [ ] `dotnet test` passes all existing tests
- [ ] Graceful shutdown completes in ≤10 seconds even when OTLP collector is unreachable

### Guardrails (Must NOT)
- Do NOT add a Windows Job Object or process group — that's a separate, more complex feature
- Do NOT add stale process killing at startup — PIDs may have been reused (dangerous)
- Do NOT change the analytics Channel capacity or flush interval
- Do NOT remove the `DrainRemainingAsync` method entirely — it's cheap and still useful during graceful shutdown

## TODOs

- [ ] 1. **Reduce host shutdown timeout to 10 seconds**
  **What**: Configure `HostOptions.ShutdownTimeout` to 10 seconds in `Program.cs`. This is the simplest, safest change and sets a hard upper bound on graceful shutdown duration.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Details**: Add after line 31 (after `builder.AddFleetTelemetry();`):
  ```csharp
  builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
  {
      opts.ShutdownTimeout = TimeSpan.FromSeconds(10);
  });
  ```
  Add `using Microsoft.Extensions.Hosting;` if not already imported (check — `WebApplication.CreateBuilder` brings in the namespace via implicit usings, so it should already be available).
  **Acceptance**: `Program.cs` compiles. Verify by searching for `ShutdownTimeout` in the file.

- [ ] 2. **Configure OTEL batch processor export timeout to 3 seconds**
  **What**: Set the OTLP exporter timeout to 3 seconds for traces, metrics, and logs so that an unreachable collector doesn't block shutdown. This is done via the `BatchExportProcessorOptions<Activity>.ExporterTimeoutMilliseconds` for traces, and analogous options for metrics. For the OTLP exporter specifically, configure `TimeoutMilliseconds` on the `OtlpExporterOptions` (which controls the HTTP request timeout).
  **Files**: `src/WeaveFleet.Api/Telemetry/TelemetryExtensions.cs`
  **Details**:
  - For each `AddOtlpExporter` call, add `otlp.TimeoutMilliseconds = 3_000;` — this controls the per-export HTTP timeout. Default is 10,000ms.
  - For the traces signal, additionally configure the batch processor schedule delay and export timeout via the second overload parameter of `AddOtlpExporter` that accepts `BatchExportProcessorOptions<Activity>`. The simplest approach is setting `otlp.TimeoutMilliseconds = 3_000;` on all three `AddOtlpExporter` calls (traces, metrics, logs).
  - The batch export processor's `ExporterTimeoutMilliseconds` defaults to 30,000ms. To reduce this, use the `AddOtlpExporter` overload that takes a second Action parameter for `BatchExportActivityProcessorOptions` on traces:
    ```csharp
    .AddOtlpExporter(otlp =>
    {
        otlp.Endpoint = new Uri($"{baseEndpoint}/v1/traces");
        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
        otlp.TimeoutMilliseconds = 3_000;
    });
    ```
  - Repeat for metrics and logs exporters.
  - Add a comment: `// Short timeout: telemetry loss during shutdown is acceptable (crash-safety design).`
  **Acceptance**: `TelemetryExtensions.cs` compiles. All three exporters have `TimeoutMilliseconds = 3_000`.

- [ ] 3. **Document analytics as best-effort in AnalyticsWriterService**
  **What**: Add a clear XML doc comment on `AnalyticsWriterService` stating that buffered analytics events will be lost on crash and that this is by design. Also add a brief inline comment on the `DrainRemainingAsync` call explaining it's an optimization, not a correctness requirement.
  **Files**: `src/WeaveFleet.Infrastructure/Analytics/AnalyticsWriterService.cs`
  **Details**:
  - Update the existing XML summary on the class (lines 15-20) to include:
    ```
    /// <para>
    /// <b>Crash-safety:</b> Buffered events are lost on process crash. This is acceptable
    /// because analytics are best-effort telemetry — the drain-on-shutdown path is an
    /// optimization, not a correctness requirement.
    /// </para>
    ```
  - On line 84-85, update the comment from:
    ```
    // App is shutting down — drain remaining items without a deadline
    ```
    to:
    ```
    // Graceful shutdown — drain remaining items. This is an optimization;
    // on crash, buffered events are simply lost (acceptable for best-effort telemetry).
    ```
  **Acceptance**: File compiles. Comments are present.

- [ ] 4. **Document the crash-safety contract in Program.cs**
  **What**: Add a comment block near the recovery logic explaining the design philosophy.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Details**: Replace the comment on line 72 with a more comprehensive block:
  ```csharp
  // ---------------------------------------------------------------------------
  // Crash-safety contract
  // ---------------------------------------------------------------------------
  // This application is designed to be crash-safe. The process can be killed at
  // any time (OOM, kill -9, power loss) and the startup recovery path below
  // reconciles any state left behind by an unclean shutdown:
  //   - All instance DB records are marked stopped (MarkAllStoppedAsync)
  //   - All non-terminal session DB records are marked stopped
  // The graceful shutdown path is an optimization, not a correctness requirement.
  // Buffered analytics events and in-flight telemetry may be lost on crash.
  // Child harness processes may be orphaned on crash — on Linux they are
  // reparented to init; on Windows they persist until they exit naturally.
  // ---------------------------------------------------------------------------
  ```
  **Acceptance**: Comment is present above the recovery block.

- [ ] 5. **Document child process orphaning as accepted behavior**
  **What**: Add a brief comment in `InstanceTracker.cs` explaining that instances are not disposed on crash and that this is by design, with orphan cleanup handled by startup recovery (DB) and the OS (processes).
  **Files**: `src/WeaveFleet.Application/Services/InstanceTracker.cs`
  **Details**: Update the existing XML summary on the class (lines 7-10) to include:
  ```
  /// <para>
  /// <b>Crash-safety:</b> On unclean shutdown, tracked instances are NOT disposed — child
  /// processes may be orphaned. The startup recovery path in <c>Program.cs</c> reconciles
  /// DB state. Orphaned OS processes are reparented to init (Linux) or persist (Windows)
  /// until they exit naturally. This is accepted behavior; see crash-safety contract.
  /// </para>
  ```
  **Acceptance**: File compiles. Comment is present.

## Verification
- [ ] `dotnet build src/WeaveFleet.Api` succeeds with zero warnings
- [ ] `dotnet test` passes all existing tests (no regressions)
- [ ] Manual verification: `ShutdownTimeout` is configured to 10s in `Program.cs`
- [ ] Manual verification: All three OTLP exporters have `TimeoutMilliseconds = 3_000`
- [ ] Manual verification: crash-safety comments are present in `Program.cs`, `AnalyticsWriterService.cs`, `InstanceTracker.cs`
