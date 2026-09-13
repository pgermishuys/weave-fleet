# WeaveFleet E2E Tests

A small set of Playwright tests that drive the real Fleet UI in headless Chromium against the real backend: the Vue SPA, the API, SignalR, and session orchestration. A scripted `TestHarness` stands in for OpenCode, so no harness binary or internet access is needed.

These tests cover what no other suite can: the path between the browser and the server. Server behaviour belongs in `WeaveFleet.Api.Tests` and `WeaveFleet.IntegrationTests`, and component behaviour in the client's vitest suite. Add an E2E test only when a bug could live in the gap between them.

## What's covered

| Test | Guards |
|------|--------|
| `GoldenPathTests` | Create a session, send a prompt, see the streamed reply, session goes idle |
| `SessionNavigationStreamingTests` | Navigating away and back mid-stream keeps live events flowing (SignalR topic subscribe/unsubscribe ordering) |
| `SignalRTransportTests` | Losing the SignalR connection mid-stream, then catching up on reconnect |
| `DelegationReplayE2ETests` | A delegated child session streams live over SignalR without polling |
| `QuestionToolTests` | Answering a question in the browser reaches the harness, and the answered state renders |
| `OidcSignInTests` | Sign-in through the IdP, and the returnUrl deep link, in a real browser |

## Architecture

```
Playwright browser
      │  HTTP + SignalR
      ▼
WeaveFleet.Api (real Kestrel server, temporary SQLite)
      │  IHarness / IHarnessSession
      ▼
WeaveFleet.TestHarness  ←── per-test scenario configuration
```

The `TestHarness` replaces all production harness registrations via `WebApplicationFactory<Program>`. Every layer above the harness runs as normal.

## Running locally

> **Caution:** these tests boot the real API. On a machine with Fleet installed, point `HOME` at a scratch directory first: the API's legacy-data migration looks under `$HOME/.weave`.

```bash
# 1. Build the SPA once (the API serves it from wwwroot/)
cd client && npm ci && npm run build && cd ..

# 2. Build the E2E project and install Chromium
./tests/WeaveFleet.E2E/playwright-setup.sh

# 3. Run
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"

# Visible browser, for debugging
HEADED=1 dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"

# One class
dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E&FullyQualifiedName~GoldenPathTests"
```

## Writing a test

```csharp
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
        });
    }
}
```

- Tag every class `[Trait("Category", "E2E")]`; CI runs that category.
- Wrap the body in `WithFailureCapture` so a screenshot and trace are saved on failure.
- Select by `data-testid` or accessible role, never CSS classes. Role names match as substrings, and a button's name includes its description text, so anchor or scope the match.
- Don't guard browser hooks with `?.` (`window.$router?.push`, `window.__WEAVE_SOCKET_TEST_API?.suspend`): if the hook disappears the step silently does nothing and the test passes without testing anything.
- Use **Shouldly** for assertions.

### Configuring the TestHarness

Call `ConfigureScenario(...)` before the session is created:

```csharp
// Simple text response on the next prompt
ConfigureScenario(b =>
    b.WithSimpleTextResponse("_placeholder_", "msg-1", "Hello from the mock harness!"));

// Custom event sequence
ConfigureScenario(b => b.WithPromptResponse(r => r
    .AddEvent(myHarnessEvent, delay: TimeSpan.FromMilliseconds(50))
    .AddEvent(myIdleEvent)));
```

Tests can also reach the live `TestHarnessSession` through `InstanceTracker` and push events directly with `PushEventAsync`.

### Page objects

| Class | Page |
|-------|------|
| `FleetDashboardPage` | `/`: new session button, empty state |
| `NewSessionFormPage` | `/sessions/new`: the New Session composer (message box, Folder and "…" chips) |
| `SessionDetailPage` | `/sessions/{id}`: messages, prompt input, status indicator |
| `FleetSidebarPage` | Sidebar session tree, for client-side navigation |
| `FleetLoginPage`, `IdpLoginPage` | Fleet's sign-in page and the test IdP |

## Test artifacts

On failure each test saves a screenshot and a Playwright trace to `bin/{config}/net10.0/test-results/`. Set `ALWAYS_SAVE_TRACE=1` to keep traces for passing tests too. View a trace with:

```bash
pwsh tests/WeaveFleet.E2E/bin/Release/net10.0/playwright.ps1 show-trace <trace.zip>
```

## CI

The `E2E` job in `.github/workflows/ci.yml` builds the SPA and the E2E project, installs Chromium, and runs `dotnet test --filter "Category=E2E"`. It fails if no tests ran: `dotnet test` exits 0 when its filter matches nothing, which once hid a month of E2E runs that executed zero tests.

## Troubleshooting

- **`playwright.ps1` not found:** it's placed next to the test assembly by the build. Run `dotnet build tests/WeaveFleet.E2E/ --configuration Release`.
- **Browser launch fails:** install OS dependencies with `pwsh tests/WeaveFleet.E2E/bin/Release/net10.0/playwright.ps1 install-deps chromium`.
- **Timeouts:** check `client/dist/` exists and is non-empty, run with `HEADED=1`, and open the trace.
