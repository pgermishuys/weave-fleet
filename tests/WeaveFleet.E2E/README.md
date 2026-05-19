# WeaveFleet E2E Tests

Playwright-based end-to-end tests that exercise the full Weave Fleet stack — backend API, WebSocket events, and the Next.js SPA — in a headless Chromium browser, without any real OpenCode binary or internet access.

## Architecture

```
Playwright browser
      │  HTTP + WebSocket
      ▼
WeaveFleet.Api (real Kestrel server, in-memory SQLite)
      │  IHarness / IHarnessInstance
      ▼
WeaveFleet.TestHarness  ←── per-test scenario configuration
```

The `TestHarness` replaces all production harness registrations via `WebApplicationFactory<Program>`. Every layer above the harness — session orchestration, event relay, WebSocket broadcast, REST endpoints, and the SPA — runs as normal.

## Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 10.0+ |
| Node.js | 22+ |
| npm | 11+ |
| PowerShell Core (`pwsh`) | 7+ |

## Quick Start

### 1. Build the frontend SPA

The E2E tests use the **integrated mode** (backend serves the pre-built SPA from `wwwroot/`). You must build the frontend at least once before running E2E tests:

```bash
cd client
npm ci
npm run build
```

### 2. Install Playwright browsers

```bash
# From the repo root — builds the E2E project and installs Chromium
./tests/WeaveFleet.E2E/playwright-setup.sh
```

### 3. Run E2E tests

```bash
# Headless (default — for CI and most local runs)
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"

# Headed (visible browser window — for debugging)
HEADED=1 dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"

# Run a single test class
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&FullyQualifiedName~GoldenPathTests"
```

### 4. Run only unit tests (skip E2E)

```bash
dotnet test --filter "Category!=E2E&Category!=Benchmark"
```

### 5. Run performance benchmarks

Benchmark tests are tagged with `[Trait("Category", "Benchmark")]` and are **excluded** from normal E2E runs.

```bash
# Run benchmarks only
dotnet test tests/WeaveFleet.E2E/ --filter "Category=Benchmark"

# Run benchmarks with detailed output (shows metrics table)
dotnet test tests/WeaveFleet.E2E/ --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

Benchmark tests create ~10 synthetic sessions and measure UI interaction latencies under load. They take longer than regular E2E tests and should be run separately.

> **Note**: To exclude benchmarks from E2E runs, use `--filter "Category=E2E&Category!=Benchmark"` if needed (benchmarks inherit the `E2E` trait from `E2ETestBase`, but their `Benchmark` trait allows precise filtering).

## Writing New E2E Tests

## Assertion Conventions

- Use **Shouldly** for assertions in new and touched E2E tests.
- Migrate existing xUnit `Assert` usage incrementally rather than as a wholesale rewrite.
- Keep NSubstitute interaction assertions such as `Received()` and `DidNotReceive()` unchanged.
- Prefer actual-first forms such as `actual.ShouldBe(expected)` and `value.ShouldNotBeNull()`.

### Minimal test class

```csharp
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

[Trait("Category", "E2E")]
public sealed class MyTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public MyTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task MyTest()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            await dashboard.WaitForEmptyStateAsync();
        });
    }
}
```

Key rules:
- Always extend `E2ETestBase` and declare both `IClassFixture<FleetWebApplicationFactory>` and `IClassFixture<PlaywrightFixture>`.
- Always tag with `[Trait("Category", "E2E")]` (inherited from `E2ETestBase`, but keep the class-level attribute too for clarity).
- Wrap test body in `await WithFailureCapture(async () => { ... })` so screenshots and traces are saved on failure.
- Use `data-testid` selectors (e.g. `Page.Locator("[data-testid='session-card']")`). **Never** couple to CSS class names.

### Configuring the TestHarness scenario

Call `ConfigureScenario(...)` before navigating to any page:

```csharp
// Simple text response on the next prompt
ConfigureScenario(b =>
    b.WithSimpleTextResponse("session-id", "msg-1", "Hello from the mock harness!")
);

// Pre-loaded message history
ConfigureScenario(b => b
    .WithUserMessage("msg-u1", "What is 2+2?")
    .WithAssistantMessage("msg-a1", "The answer is 4.")
);

// Simulate a spawn failure
ConfigureScenario(b => b.WithSpawnFailure());

// Simulate a prompt failure
ConfigureScenario(b => b.WithSendPromptFailure());

// Custom event sequence
ConfigureScenario(b => b.WithPromptResponse(r => r
    .AddEvent(myHarnessEvent, delay: TimeSpan.FromMilliseconds(50))
    .AddEvent(myIdleEvent)
));
```

### Page objects

| Class | Page |
|-------|------|
| `FleetDashboardPage` | `/` — session list, summary bar, new session button |
| `NewSessionDialog` | The "New Session" modal |
| `SessionDetailPage` | `/sessions/{id}` — messages, prompt input, status indicator, abort |
| `AnalyticsPage` | `/analytics` |

All page objects use `data-testid` attributes and Playwright auto-waiting. Add new page objects in `tests/WeaveFleet.E2E/Pages/`.

## Test Artifacts

On failure, each test saves:

| Artifact | Location |
|----------|----------|
| Screenshot | `bin/{config}/net10.0/test-results/{TestClass}-{timestamp}-screenshot.png` |
| Playwright trace | `bin/{config}/net10.0/test-results/{TestClass}-{timestamp}-trace.zip` |

To also save a Playwright trace when a test passes, opt in with `ALWAYS_SAVE_TRACE=1`:

```bash
ALWAYS_SAVE_TRACE=1 dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"
```

This keeps the artifact path unchanged:

| Artifact | Location |
|----------|----------|
| Playwright trace on success (opt-in) | `bin/{config}/net10.0/test-results/{TestClass}-{timestamp}-trace.zip` |

Passing runs do not save screenshots unless the test fails.

View traces with:

```bash
pwsh tests/WeaveFleet.E2E/bin/Release/net10.0/playwright.ps1 show-trace <trace.zip>
```

`test-results/` directories are excluded from git (see `.gitignore`).

## CI

E2E tests run on every PR and push to `main` via `.github/workflows/e2e-tests.yml`. The workflow:

1. Builds the frontend SPA (`npm ci && npm run build`)
2. Builds the .NET solution
3. Caches Playwright browsers (keyed on `Directory.Packages.props`)
4. Runs `dotnet test --filter "Category=E2E"`
5. Uploads test results and failure artifacts

## Troubleshooting

### `playwright.ps1 not found`

The Playwright CLI script is placed next to the test assembly only after a successful build. Run:

```bash
dotnet build tests/WeaveFleet.E2E/ --configuration Release
```

### Browser launch fails

Playwright requires OS-level dependencies. On Ubuntu/Debian:

```bash
pwsh tests/WeaveFleet.E2E/bin/Release/net10.0/playwright.ps1 install-deps chromium
```

### Tests are timing out

- Check that the frontend SPA was built (`client/dist/` exists and is non-empty).
- Run with a visible browser to watch what's happening: `HEADED=1 dotnet test ...`
- Inspect the Playwright trace zip for the failing test.

### `dotnet test` runs unit tests AND E2E tests (slow)

Use `--filter "Category=E2E"` for E2E only, or `--filter "Category!=E2E"` for unit tests only.
