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
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The agent bridge for pooled OpenCode. Fleet auth is on, but these calls carry no user and no CSRF token:
/// a loopback address plus a process token is all they have. The caller resolver is faked; its own rules are
/// covered in the Infrastructure tests.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class CanvasBridgeEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Token = "token-1";
    private const string OpenCodeSessionId = "oc-1";
    private const string SessionId = "sess-owner";
    private const string Owner = "owner-user";
    private const string BridgeUrl = "/api/bridge/opencode/canvas";

    private static readonly object Flow = new
    {
        nodes = new[] { new { id = "n1", label = "NuCode session" }, new { id = "n2", label = "SessionEventsHub" } },
        edges = new[] { new { id = "e1", from = "n1", to = "n2", label = "publishes" } },
    };

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = CreateFactory(simulateLocalhostRequest: true);
        _client = _factory.CreateClient();
        await SeedSessionAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Open_stores_the_canvas_for_the_session_owner_and_returns_the_tool_result()
    {
        var response = await PostAsync("open", Token, new { openCodeSessionId = OpenCodeSessionId, kind = "diagram", title = "Flow", state = Flow });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var canvasId = body.GetProperty("metadata").GetProperty("canvasId").GetString();
        canvasId.ShouldNotBeNull();
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["title", "output", "metadata"]);
        body.GetProperty("title").GetString().ShouldBe("Flow · +2 boxes, +1 edge · v1");
        body.GetProperty("output").GetString().ShouldBe($"Opened \"Flow\" ({canvasId}) at v1.");
        body.GetProperty("metadata").GetProperty("version").GetInt32().ShouldBe(1);

        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await connection.ExecuteScalarAsync<string>("SELECT user_id FROM canvases WHERE id = @canvasId", new { canvasId })).ShouldBe(Owner);
    }

    [Fact]
    public async Task A_screenshot_comes_back_on_the_tool_result_as_a_base64_image()
    {
        var opened = await PostAsync("browser-open", Token, new { openCodeSessionId = OpenCodeSessionId, url = "http://localhost:5173/", title = "Shop" });
        var canvasId = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("metadata").GetProperty("canvasId").GetString();

        var response = await PostAsync("screenshot", Token, new { openCodeSessionId = OpenCodeSessionId, canvasId, path = "", viewport = "desktop" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var attachment = body.GetProperty("attachments").EnumerateArray().ShouldHaveSingleItem();
        attachment.GetProperty("mime").GetString().ShouldBe("image/png");
        attachment.GetProperty("fileName").GetString().ShouldBe("screenshot.png");
        Convert.FromBase64String(attachment.GetProperty("base64").GetString()!).ShouldBe(FakeScreenshotter.Png);
        body.GetProperty("title").GetString().ShouldBe("Shop · 1280×800");
    }

    [Fact]
    public async Task Patch_read_list_and_focus_answer_with_tool_text()
    {
        var canvasId = await OpenFlowAsync();

        var patched = await ReadToolAsync(await PostAsync("patch", Token, new
        {
            openCodeSessionId = OpenCodeSessionId,
            canvasId,
            ops = new object[] { new { op = "addNode", id = "n3", label = "Client" } },
        }));
        var read = await ReadToolAsync(await PostAsync("read", Token, new { openCodeSessionId = OpenCodeSessionId, canvasId }));
        var listed = await ReadToolAsync(await PostAsync("list", Token, new { openCodeSessionId = OpenCodeSessionId }));
        var focused = await ReadToolAsync(await PostAsync("focus", Token, new { openCodeSessionId = OpenCodeSessionId, canvasId }));

        patched.ShouldBe("Updated to v2 (+1 box).");
        read.ShouldBe($"diagram {canvasId} \"Flow\" v2 TB\nn1 NuCode session\nn2 SessionEventsHub\nn3 Client\ne1 n1 -> n2 publishes");
        listed.ShouldBe($"{canvasId} diagram \"Flow\" v2");
        focused.ShouldBe($"Showing \"Flow\" ({canvasId}) at v2.");
    }

    [Theory]
    [InlineData(null, OpenCodeSessionId)]
    [InlineData("token-2", OpenCodeSessionId)]
    [InlineData(Token, "oc-2")]
    public async Task A_call_Fleet_cannot_place_returns_not_found(string? token, string openCodeSessionId)
    {
        var response = await PostAsync("list", token, new { openCodeSessionId });

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
            Content = JsonContent.Create(new { openCodeSessionId = OpenCodeSessionId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Canvas_errors_come_back_with_their_message_and_a_matching_status()
    {
        var canvasId = await OpenFlowAsync();
        using (var scope = _factory!.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            (await scope.ServiceProvider.GetRequiredService<ICanvasService>().CloseAsync(SessionId, canvasId)).IsSuccess.ShouldBeTrue();

        var badKind = await PostAsync("open", Token, new { openCodeSessionId = OpenCodeSessionId, kind = "chart", title = "Other", state = Flow });
        var missing = await PostAsync("read", Token, new { openCodeSessionId = OpenCodeSessionId, canvasId = "cv_missing" });
        var closed = await PostAsync("patch", Token, new { openCodeSessionId = OpenCodeSessionId, canvasId, ops = Array.Empty<object>() });

        badKind.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(missing)).ShouldBe("No canvas cv_missing in this session. Call fleet_canvas_list to see the open canvases.");
        closed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorAsync(closed)).ShouldBe($"Refused: the user closed \"Flow\" ({canvasId}). Call fleet_canvas_open with its title to reopen it.");
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

    private async Task<string> OpenFlowAsync()
    {
        var response = await PostAsync("open", Token, new { openCodeSessionId = OpenCodeSessionId, kind = "diagram", title = "Flow", state = Flow });
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
            => Task.FromResult(bridgeToken == Token && harnessSessionId == OpenCodeSessionId
                ? new HarnessCanvasCaller(SessionId, Owner)
                : null);
    }
}
