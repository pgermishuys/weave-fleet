using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The agent bridge for harness processes. Fleet auth is on, but these calls carry no user and no CSRF token:
/// a loopback address plus a process token is all they have. The caller resolver is faked; its own rules are
/// covered in the Infrastructure tests.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class CanvasBridgeEndpointTests : IClassFixture<CanvasBridgeEndpointTests.Server>, IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "oc-1";
    private const string SessionId = "sess-owner";
    private const string Owner = "owner-user";
    private const string BridgeUrl = "/api/bridge/canvas";

    private readonly ApiWebApplicationFactory _factory;
    private HttpClient? _client;

    public CanvasBridgeEndpointTests(Server server)
    {
        _factory = server.Factory;
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        // The server is shared: start each test with no canvases open, as on a fresh one.
        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync("DELETE FROM canvas_revisions; DELETE FROM canvases;");
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Browser_open_stores_the_canvas_for_the_session_owner_and_returns_the_tool_result()
    {
        var response = await PostAsync("browser-open", Token, new { harnessSessionId = HarnessSessionId, url = "http://localhost:5173/", title = "Shop" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var canvasId = body.GetProperty("metadata").GetProperty("canvasId").GetString();
        canvasId.ShouldNotBeNull();
        body.GetProperty("title").GetString().ShouldStartWith("Shop");
        body.GetProperty("metadata").GetProperty("version").GetInt32().ShouldBe(1);

        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await connection.ExecuteScalarAsync<string>("SELECT user_id FROM canvases WHERE id = @canvasId", new { canvasId })).ShouldBe(Owner);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("patch")]
    public async Task The_retired_open_and_patch_routes_are_gone(string tool)
    {
        var response = await PostAsync(tool, Token, new { harnessSessionId = HarnessSessionId, kind = "diagram", title = "Flow" });

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_screenshot_comes_back_on_the_tool_result_as_a_base64_image()
    {
        var opened = await PostAsync("browser-open", Token, new { harnessSessionId = HarnessSessionId, url = "http://localhost:5173/", title = "Shop" });
        var canvasId = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("metadata").GetProperty("canvasId").GetString();

        var response = await PostAsync("screenshot", Token, new { harnessSessionId = HarnessSessionId, canvasId, path = "", viewport = "desktop" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var attachment = body.GetProperty("attachments").EnumerateArray().ShouldHaveSingleItem();
        attachment.GetProperty("mime").GetString().ShouldBe("image/png");
        attachment.GetProperty("fileName").GetString().ShouldBe("screenshot.png");
        Convert.FromBase64String(attachment.GetProperty("base64").GetString()!).ShouldBe(FakeScreenshotter.Png);
        body.GetProperty("title").GetString().ShouldBe("Shop · 1280×800");

        // Fleet's copy for the conversation, which the tool row finds through the call's metadata.
        var screenshot = body.GetProperty("metadata").GetProperty("screenshot");
        screenshot.EnumerateObject().Select(property => property.Name).ShouldBe(["sessionId", "id", "width", "height"]);
        screenshot.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        screenshot.GetProperty("width").GetInt32().ShouldBe(1280);
        screenshot.GetProperty("height").GetInt32().ShouldBe(800);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ISessionScreenshotStore>().ReadAsync(SessionId, screenshot.GetProperty("id").GetString()!))
            .ShouldBe(FakeScreenshotter.Png);
    }

    [Fact]
    public async Task A_page_shown_in_the_conversation_is_named_in_the_metadata_and_served_with_fleet_s_theme()
    {
        var folder = Directory.CreateTempSubdirectory("fleet-conversation-page-");
        try
        {
            var file = Path.Combine(folder.FullName, "times.html");
            await File.WriteAllTextAsync(file, "<!doctype html><html><head><title>CI</title></head><body><p>Times</p></body></html>");

            var response = await PostAsync("page-show", Token, new { harnessSessionId = HarnessSessionId, path = file, placement = "conversation", title = "CI test times" });

            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("title").GetString().ShouldBe("CI test times");
            var metadata = body.GetProperty("metadata");
            metadata.GetProperty("canvasId").ValueKind.ShouldBe(JsonValueKind.Null);
            var page = metadata.GetProperty("page");
            page.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "entry"]);
            page.GetProperty("entry").GetString().ShouldBe("times.html");

            var served = await (await _client!.GetAsync($"/pages/{page.GetProperty("id").GetString()}/times.html")).Content.ReadAsStringAsync();
            served.ShouldBe("<!doctype html><html><head>" + PageTheme.Bootstrap + "<title>CI</title></head><body><p>Times</p></body></html>");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Other_tools_carry_no_screenshot_in_their_metadata()
    {
        var canvasId = await OpenShopAsync();

        var response = await PostAsync("read", Token, new { harnessSessionId = HarnessSessionId, canvasId });

        var metadata = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("metadata");
        metadata.EnumerateObject().Select(property => property.Name).ShouldBe(["canvasId", "version"]);
    }

    [Fact]
    public async Task Read_list_and_focus_answer_with_tool_text()
    {
        var canvasId = await OpenShopAsync();

        var read = await ReadToolAsync(await PostAsync("read", Token, new { harnessSessionId = HarnessSessionId, canvasId }));
        var listed = await ReadToolAsync(await PostAsync("list", Token, new { harnessSessionId = HarnessSessionId }));
        var focused = await ReadToolAsync(await PostAsync("focus", Token, new { harnessSessionId = HarnessSessionId, canvasId }));

        read.ShouldBe($"browser {canvasId} \"Shop\" v1\nurl http://localhost:5173/");
        listed.ShouldBe($"{canvasId} browser \"Shop\" v1");
        focused.ShouldBe($"Showing \"Shop\" ({canvasId}) at v1.");
    }

    [Theory]
    [InlineData(null, HarnessSessionId)]
    [InlineData("token-2", HarnessSessionId)]
    [InlineData(Token, "oc-2")]
    public async Task A_call_Fleet_cannot_place_returns_not_found(string? token, string harnessSessionId)
    {
        var response = await PostAsync("list", token, new { harnessSessionId });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Fact]
    public async Task A_call_from_another_machine_returns_not_found_even_with_a_valid_token()
    {
        await using var factory = CreateFactory(simulateLocalhostRequest: false);
        using var client = factory.CreateClient();
        await SeedSessionAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BridgeUrl}/list")
        {
            Content = JsonContent.Create(new { harnessSessionId = HarnessSessionId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Canvas_errors_come_back_with_their_message_and_a_matching_status()
    {
        var missing = await PostAsync("read", Token, new { harnessSessionId = HarnessSessionId, canvasId = "cv_missing" });
        var noId = await PostAsync("focus", Token, new { harnessSessionId = HarnessSessionId });

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(missing)).ShouldBe("No canvas cv_missing in this session. Call fleet_canvas_list to see the open canvases.");
        noId.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorAsync(noId)).ShouldStartWith("\"canvasId\" is required.");
    }

    public sealed class Server : IAsyncLifetime
    {
        public ApiWebApplicationFactory Factory { get; } = CreateFactory(simulateLocalhostRequest: true);

        public Task InitializeAsync() => SeedSessionAsync(Factory);

        public async Task DisposeAsync() => await Factory.DisposeAsync();
    }

    private static ApiWebApplicationFactory CreateFactory(bool simulateLocalhostRequest)
        => new(
            authEnabled: true,
            simulateLocalhostRequest: simulateLocalhostRequest,
            configureTestServices: services =>
            {
                services.AddSingleton<IHarnessCanvasCallerResolver>(new FakeCallers());
                services.AddSingleton<IScreenshotter>(new FakeScreenshotter());
            });

    /// <summary>Drives no browser: every capture is the same little PNG.</summary>
    private sealed class FakeScreenshotter : IScreenshotter
    {
        public static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

        public Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default)
            => Task.FromResult(ScreenshotOutcome.Ok(Png, request.Width, request.Height));
    }

    private static async Task SeedSessionAsync(ApiWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-owner', '/ws-owner', 'Owner', '2026-09-01T00:00:00+00:00', 'owner-user');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-owner', 0, NULL, '/ws-owner', '', 'running', '2026-09-01T00:00:00+00:00', 'owner-user');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('sess-owner', 'ws-owner', 'inst-owner', 'oc-1', 'Owner', 'active', '/ws-owner',
                    'running', 'active', '2026-09-01T10:00:00+00:00', 'owner-user');
            """);
    }

    private async Task<string> OpenShopAsync()
    {
        var response = await PostAsync("browser-open", Token, new { harnessSessionId = HarnessSessionId, url = "http://localhost:5173/", title = "Shop" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("metadata").GetProperty("canvasId").GetString()!;
    }

    private async Task<HttpResponseMessage> PostAsync(string tool, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BridgeUrl}/{tool}") { Content = JsonContent.Create(body) };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private static async Task<string?> ReadToolAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("output").GetString();
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == Token && harnessSessionId == HarnessSessionId
                ? new HarnessCanvasCaller(SessionId, Owner)
                : null);
    }
}
