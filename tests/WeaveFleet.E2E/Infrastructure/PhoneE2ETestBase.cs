using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>A Fleet that asks every request for a token, as behind <c>tailscale serve</c>, so phones can pair.</summary>
public sealed class PhoneFleetWebApplicationFactory : FleetWebApplicationFactory
{
    protected override bool RequireToken => true;
}

/// <summary>
/// Phone E2E tests: a desktop browser signed in with the machine token, and phone contexts (390×844, touch) that pair
/// and then use their own device token. Each context is closed after the test. Each test class sets its own category.
/// </summary>
public abstract class PhoneE2ETestBase : IAsyncLifetime
{
    private readonly PhoneFleetWebApplicationFactory _factory;
    private readonly PlaywrightFixture _playwright;
    private readonly List<IBrowserContext> _contexts = [];

    protected PhoneE2ETestBase(PhoneFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    {
        _factory = factory;
        _playwright = playwright;
    }

    protected string ServerUrl => _factory.ServerUrl;

    protected IServiceProvider Services => _factory.KestrelServices;

    protected string MachineToken => Services.GetRequiredService<ILocalTokenAuthService>().Token;

    protected string MachineId => Services.GetRequiredService<MachineIdentityStore>().Get().Id;

    public async Task InitializeAsync()
    {
        await _factory.EnsureStartedAsync();
        await using var scope = Services.CreateAsyncScope();
        var roots = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        await roots.AddRootAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
    }

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
            await context.DisposeAsync();
    }

    /// <summary>A desktop browser signed in as the owner with the machine token.</summary>
    protected async Task<IPage> DesktopAsync()
    {
        var context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = ServerUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        _contexts.Add(context);
        var login = await context.APIRequest.PostAsync("/auth/token-login", new APIRequestContextOptions { DataObject = new { token = MachineToken } });
        login.Status.ShouldBe(200);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(10_000);
        return page;
    }

    /// <summary>A phone-sized browser with nothing signed in.</summary>
    protected async Task<IPage> PhoneAsync()
    {
        var context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = ServerUrl,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            IsMobile = true,
            HasTouch = true,
            UserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36",
        });
        _contexts.Add(context);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(10_000);
        return page;
    }

    /// <summary>Pairs <paramref name="phone"/> through the real pages: a code from the owner, the QR link, Connect.</summary>
    protected async Task PairAsync(IPage phone)
    {
        using var owner = OwnerClient();
        var created = await owner.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = ServerUrl });
        created.EnsureSuccessStatusCode();
        var url = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        await phone.GotoAsync(url);
        await phone.GetByTestId("pair-connect").ClickAsync();
        await phone.WaitForURLAsync("**/phone/setup");
    }

    /// <summary>An HTTP client presenting the machine token.</summary>
    protected HttpClient OwnerClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MachineToken);
        return client;
    }

    /// <summary>Starts a session on the test harness and returns its ids and the live harness session.</summary>
    protected async Task<(string SessionId, string InstanceId, TestHarnessSession Harness)> StartSessionAsync(string title)
    {
        using var owner = OwnerClient();
        var response = await owner.PostAsJsonAsync("/api/sessions", new { directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), title, harnessType = "opencode" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var session = body.GetProperty("session");
        var sessionId = session.GetProperty("id").GetString()!;
        var instanceId = body.GetProperty("instanceId").GetString()!;
        var harness = Services.GetRequiredService<InstanceTracker>().Get(instanceId).ShouldBeOfType<TestHarnessSession>();
        return (sessionId, instanceId, harness);
    }

    /// <summary>The agent starts a turn and says something, as a harness would report it.</summary>
    protected static async Task StartTurnAsync(TestHarnessSession harness, string sessionId, string text)
    {
        await harness.PushEventAsync(Event(harness, sessionId, "session.status", new { sessionId = harness.InstanceId, status = new { type = "busy" } }));
        var messageId = $"msg-{Guid.NewGuid():N}";
        await harness.PushEventAsync(Event(harness, sessionId, "message.updated", new
        {
            info = new { id = messageId, sessionID = harness.InstanceId, role = "assistant", time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
        }));
        await harness.PushEventAsync(Event(harness, sessionId, "message.part.updated", new
        {
            sessionID = harness.InstanceId,
            part = new { type = "text", id = $"{messageId}-t", sessionID = harness.InstanceId, messageID = messageId, text },
        }));
    }

    protected static HarnessEvent Event(TestHarnessSession harness, string sessionId, string type, object payload) => new()
    {
        Type = type,
        SessionId = harness.InstanceId,
        FleetSessionId = sessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(payload),
    };

    /// <summary>Waits for <paramref name="condition"/>, polling.</summary>
    protected static async Task EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(100);
        condition().ShouldBeTrue();
    }
}
