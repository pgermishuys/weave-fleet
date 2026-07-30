# SignalR Transport E2E Tests

This document describes the SignalR-specific E2E test suite and how to run it.

## Overview

The `SignalRTransportTests` class contains E2E tests that verify the SignalR transport implementation works correctly. These tests ensure that:

1. All core functionality works with SignalR instead of WebSocket
2. SignalR-specific reconnection scenarios are handled properly
3. Message streaming and catch-up work correctly
4. Transport toggling between WebSocket and SignalR works

## Test Coverage

### Core Functionality Tests

- **SignalR_GoldenPath_CreateSessionAndReceiveResponse**
  - Verifies the basic flow: create session, send prompt, receive response
  - Ensures SignalR transport is active via `localStorage.setItem('fleet:transport', 'signalr')`

- **SignalR_StreamingResponse_ReceivesAllDeltas**
  - Tests streaming responses with multiple deltas
  - Verifies all chunks are received and assembled correctly

### Reconnection Tests

- **SignalR_Reconnect_ShowsDisconnectedStateAndCatchesUp**
  - Tests disconnection detection and UI state
  - Verifies catch-up after reconnection without page reload
  - Ensures no messages are lost during disconnection

- **SignalR_DisconnectDuringStreaming_RecoversFullResponse**
  - Tests disconnection during an active streaming response
  - Verifies full message recovery after reconnection
  - Ensures user prompts don't duplicate

- **SignalR_MultipleRapidReconnects_NoMessageLoss**
  - Tests multiple disconnect/reconnect cycles in quick succession
  - Verifies no message loss across multiple reconnections
  - Ensures all messages are eventually delivered

### Transport Toggle Tests

- **SignalR_TransportToggle_WorksAfterPageReload**
  - Tests switching from WebSocket to SignalR mid-session
  - Verifies messages persist across transport changes
  - Ensures both transports work in the same session lifecycle

## Running the Tests

### Prerequisites

1. Build the frontend SPA (required for E2E tests):
   ```bash
   cd client
   bun install
   bun run build
   ```

2. Install Playwright browsers (one-time setup):
   ```bash
   ./tests/WeaveFleet.E2E/playwright-setup.sh
   ```

### Run All SignalR Tests

```bash
# Headless mode (default)
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&FullyQualifiedName~SignalRTransportTests"

# Headed mode (visible browser for debugging)
$env:HEADED=1; dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&FullyQualifiedName~SignalRTransportTests"
```

### Run a Single SignalR Test

```bash
# Example: Run only the golden path test
dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SignalRTransportTests.SignalR_GoldenPath_CreateSessionAndReceiveResponse"

# Example: Run only reconnection tests
dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SignalRTransportTests&FullyQualifiedName~Reconnect"
```

### Run All E2E Tests (Including SignalR)

```bash
# Run all E2E tests (WebSocket + SignalR)
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"
```

### Skip Frontend Build (Faster Iteration)

If you've already built the frontend and just want to iterate on tests:

```bash
dotnet test tests/WeaveFleet.E2E/ -p:SkipFrontendBuild=true --filter "FullyQualifiedName~SignalRTransportTests"
```

## Test Implementation Details

### Transport Activation

All SignalR tests activate the SignalR transport by setting localStorage before navigation:

```csharp
await Page.AddInitScriptAsync("window.localStorage.setItem('fleet:transport', 'signalr')");
```

This ensures the client uses SignalR instead of WebSocket for all connections.

### Connection Control

Tests use the `__WEAVE_SOCKET_TEST_API` to control the connection:

```csharp
// Suspend the connection (simulates network loss)
await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.suspend()");

// Resume the connection (simulates network recovery)
await Page.EvaluateAsync("window.__WEAVE_SOCKET_TEST_API?.resume()");
```

This API works for both WebSocket and SignalR transports.

### Message Injection

Tests inject messages directly into the TestHarness to simulate server-side events:

```csharp
await PushDurableAssistantMessageAsync(harness, harnessSessionId, sessionId, messageId, text);
```

This allows testing catch-up scenarios where messages arrive while the client is disconnected.

## Debugging Failed Tests

### View Playwright Traces

When a test fails, Playwright saves a trace and screenshot:

```bash
# View the trace
pwsh tests/WeaveFleet.E2E/bin/Debug/net10.0/playwright.ps1 show-trace tests/WeaveFleet.E2E/bin/Debug/net10.0/test-results/SignalRTransportTests-*-trace.zip
```

### Always Save Traces (Even on Success)

```bash
$env:ALWAYS_SAVE_TRACE=1; dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SignalRTransportTests"
```

### Run in Headed Mode

```bash
$env:HEADED=1; dotnet test tests/WeaveFleet.E2E/ --filter "FullyQualifiedName~SignalRTransportTests"
```

This opens a visible browser window so you can watch the test execution.

## CI Integration

The SignalR tests are included in the standard E2E test suite and run automatically in CI via the `Category=E2E` filter. No special configuration is needed.

## Known Limitations

1. **Server Requirement**: These tests require a running Kestrel server with SignalR hub configured. The `FleetWebApplicationFactory` handles this automatically.

2. **Timing Sensitivity**: Reconnection tests use timeouts to wait for disconnection/reconnection states. If tests are flaky, increase the timeout values.

3. **TestHarness Only**: These tests use the `TestHarness` mock implementation. For real harness testing, see `HarnessSmokeTests`.

## Related Files

- **Test Implementation**: `tests/WeaveFleet.E2E/Tests/SignalRTransportTests.cs`
- **SignalR Hub**: `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`
- **Client Transport**: `client/src/lib/transport/signalr-transport.ts`
- **Transport Factory**: `client/src/lib/transport/transport-factory.ts`
- **E2E Test Base**: `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs`
