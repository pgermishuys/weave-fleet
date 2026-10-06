using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Fleet's MCP server, at <c>/mcp</c> under a harness process's <c>/agent/{token}</c> prefix, on a Fleet that requires its
/// access token. The harness's side (which session a token and a call are) is faked; its own rules are covered in the
/// Infrastructure tests.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class McpEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Token = "bridge-1";
    private const string HarnessSessionId = "cc-1";
    private const string SessionId = "sess-owner";
    private const string Owner = "owner-user";
    private const string Url = $"/agent/{Token}/mcp";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = CreateFactory(simulateLocalhost: true);
        _client = _factory.CreateClient();
        await SeedSessionAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    // ── The handshake ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_answers_in_the_clients_version_with_the_tools_capability()
    {
        var result = await ResultAsync(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "claude-code", version = "2.1.290" } });

        result.GetProperty("protocolVersion").GetString().ShouldBe("2025-11-25");
        result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean().ShouldBeFalse();
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("fleet");
    }

    [Fact]
    public async Task A_version_Fleet_doesnt_know_is_answered_in_the_newest_it_does()
    {
        var result = await ResultAsync(1, "initialize", new { protocolVersion = "2099-01-01" });

        result.GetProperty("protocolVersion").GetString().ShouldBe(WeaveFleet.Api.Endpoints.McpEndpoints.LatestProtocolVersion);
    }

    [Fact]
    public async Task A_response_is_JSON_RPC_with_the_requests_id()
    {
        var response = await PostAsync("""{"jsonrpc":"2.0","id":"ping-7","method":"ping"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        body.GetProperty("id").GetString().ShouldBe("ping-7");
        body.GetProperty("result").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notification_is_accepted_with_nothing_to_say()
    {
        var response = await PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_method_Fleet_doesnt_have_is_not_found()
    {
        // Claude Code 2.1.290 tries the 2026-07-28 discovery first, and falls back to initialize on this.
        var error = await ErrorAsync(1, "server/discover", new { });

        error.GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task A_request_that_isnt_JSON_is_a_parse_error_with_no_id()
    {
        var body = await (await PostAsync("{not json")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
        body.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_batch_is_an_invalid_request()
    {
        var body = await (await PostAsync("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
    }

    // ── Who can reach it ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Without_an_agents_prefix_there_is_no_MCP_server(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp")
        {
            Content = method == "GET" ? null : new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("fleet_canvas");
    }

    [Fact]
    public async Task A_token_no_process_has_is_not_found()
    {
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");

        (await _client!.PostAsync("/agent/not-a-token/mcp", content)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_valid_token_from_another_machine_is_not_found()
    {
        await using var factory = CreateFactory(simulateLocalhost: false);
        using var client = factory.CreateClient();
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");

        (await client.PostAsync(Url, content)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task There_is_no_event_stream_to_open_or_session_to_end()
    {
        (await _client!.GetAsync(Url)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await _client!.DeleteAsync(Url)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    // ── tools/list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_has_the_tools_every_session_gets_with_the_plugins_descriptions_and_inputs()
    {
        var tools = await ToolsAsync();

        tools.Select(tool => tool.GetProperty("name").GetString()).ShouldBe(
        [
            "fleet_canvas_list", "fleet_canvas_open", "fleet_canvas_read", "fleet_canvas_patch", "fleet_canvas_focus",
            "fleet_page_show", "fleet_app_start", "fleet_browser_open", "fleet_browser_read", "fleet_browser_act",
            "fleet_browser_screenshot", "fleet_session_read",
        ]);
        var page = tools.Single(tool => tool.GetProperty("name").GetString() == "fleet_page_show");
        page.GetProperty("description").GetString().ShouldBe(FleetToolCatalog.Find("fleet_page_show")!.Description);
        page.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(name => name.GetString()).ShouldBe(["path", "title"]);
    }

    [Fact]
    public async Task Each_switch_adds_its_tools_and_Settings_Browser_off_takes_the_browsers_away()
    {
        await SetPreferencesAsync(
            (AgentMemory.PreferenceKey, "true"),
            (SessionMessages.PreferenceKey, "true"),
            (AgentBrowserSettings.EnabledKey, "false"));

        var names = (await ToolsAsync()).Select(tool => tool.GetProperty("name").GetString()).ToList();

        names.ShouldContain("fleet_memory_save");
        names.ShouldContain("fleet_memory_forget");
        names.ShouldContain("fleet_message");
        names.ShouldNotContain("fleet_browser_read");
        names.ShouldNotContain("fleet_browser_act");
        // Not a workflow step's session.
        names.ShouldNotContain("fleet_step_done");
    }

    // ── tools/call ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_runs_the_tool_for_the_callers_session_and_keeps_its_card_for_the_harness()
    {
        var result = await CallAsync("fleet_canvas_open", new
        {
            kind = "diagram",
            title = "Flow",
            state = new { nodes = new[] { new { id = "a", label = "Agent" }, new { id = "f", label = "Fleet" } }, edges = new[] { new { id = "e", from = "a", to = "f" } } },
        }, callId: "toolu_open");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var text = result.GetProperty("content").EnumerateArray().ShouldHaveSingleItem();
        text.GetProperty("type").GetString().ShouldBe("text");
        text.GetProperty("text").GetString()!.ShouldStartWith("Opened \"Flow\" (cv_");
        text.TryGetProperty("data", out _).ShouldBeFalse();

        // What the tool card shows: the plugins' title and metadata, by the harness's id for the call.
        var record = _factory!.Services.GetRequiredService<FleetToolCallRecords>().Take("toolu_open").ShouldNotBeNull();
        record.Title.ShouldBe("Flow · +2 boxes, +1 edge · v1");
        record.Metadata.GetProperty("canvasId").GetString()!.ShouldStartWith("cv_");
        record.Metadata.GetProperty("version").GetInt32().ShouldBe(1);

        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await connection.ExecuteScalarAsync<string>("SELECT session_id FROM canvases WHERE title = 'Flow'")).ShouldBe(SessionId);
    }

    [Fact]
    public async Task A_screenshot_comes_back_as_an_image_the_model_sees_and_a_picture_the_conversation_shows()
    {
        var opened = await CallAsync("fleet_browser_open", new { url = "http://localhost:5173/", title = "Shop" }, callId: "toolu_browser");
        opened.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var canvasId = _factory!.Services.GetRequiredService<FleetToolCallRecords>().Take("toolu_browser")!.Metadata.GetProperty("canvasId").GetString();

        var result = await CallAsync("fleet_browser_screenshot", new { canvasId, path = "", viewport = "desktop" }, callId: "toolu_shot");

        var content = result.GetProperty("content").EnumerateArray().ToList();
        content.Select(item => item.GetProperty("type").GetString()).ShouldBe(["text", "image"]);
        content[1].GetProperty("mimeType").GetString().ShouldBe("image/png");
        Convert.FromBase64String(content[1].GetProperty("data").GetString()!).ShouldBe(FakeScreenshotter.Png);

        var screenshot = _factory.Services.GetRequiredService<FleetToolCallRecords>().Take("toolu_shot")!.Metadata.GetProperty("screenshot");
        screenshot.EnumerateObject().Select(property => property.Name).ShouldBe(["sessionId", "id", "width", "height"]);
        screenshot.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        screenshot.GetProperty("width").GetInt32().ShouldBe(1280);
    }

    [Fact]
    public async Task A_tools_refusal_is_its_answer_for_the_model_to_read()
    {
        var result = await CallAsync("fleet_canvas_read", new { canvasId = "" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!.ShouldContain("canvasId");
    }

    [Fact]
    public async Task A_tool_Fleet_doesnt_have_is_invalid_params()
    {
        var error = await ErrorAsync(5, "tools/call", new { name = "fleet_nothing", arguments = new { } });

        error.GetProperty("code").GetInt32().ShouldBe(-32602);
        error.GetProperty("message").GetString().ShouldBe("Unknown tool: fleet_nothing");
    }

    [Fact]
    public async Task A_tool_whose_switch_is_off_cant_be_called()
    {
        var error = await ErrorAsync(5, "tools/call", new { name = "fleet_memory_save", arguments = new { text = "x", list = "machine", kind = "learned", replaces = "" } });

        error.GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    [Fact]
    public async Task A_subagents_call_cant_finish_a_step()
    {
        await SetPreferencesAsync(("Workflows", "true"));
        using (var scope = _factory!.Services.CreateScope())
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await connection.ExecuteAsync("UPDATE sessions SET workflow_run_id = 'run-1' WHERE id = @SessionId", new { SessionId });

        var result = await CallAsync("fleet_step_done", new { outcome = "ready", summary = "Done." }, callId: "toolu_sub", subagent: true);

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe(WeaveFleet.Application.Workflows.WorkflowStepBridge.NotAStepMessage);
    }

    // -----------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client!.PostAsync(Url, content);
    }

    private async Task<JsonElement> SendAsync(int id, string method, object parameters)
    {
        var response = await PostAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetInt32().ShouldBe(id);
        return body;
    }

    private async Task<JsonElement> ResultAsync(int id, string method, object parameters)
        => (await SendAsync(id, method, parameters)).GetProperty("result");

    private async Task<JsonElement> ErrorAsync(int id, string method, object parameters)
        => (await SendAsync(id, method, parameters)).GetProperty("error");

    private async Task<List<JsonElement>> ToolsAsync()
        => [.. (await ResultAsync(2, "tools/list", new { })).GetProperty("tools").EnumerateArray()];

    /// <summary>A call as Claude Code makes it: the arguments, and its own id for the call in <c>_meta</c>.</summary>
    private Task<JsonElement> CallAsync(string name, object arguments, string callId = "toolu_1", bool subagent = false)
        => ResultAsync(3, "tools/call", new
        {
            name,
            arguments,
            _meta = new Dictionary<string, object> { ["test/callId"] = callId, ["test/subagent"] = subagent },
        });

    private async Task SetPreferencesAsync(params (string Key, string Value)[] preferences)
    {
        using var scope = _factory!.Services.CreateScope();
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
            foreach (var (key, value) in preferences)
                await repository.SetAsync(key, value);
        }
    }

    private static ApiWebApplicationFactory CreateFactory(bool simulateLocalhost)
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: simulateLocalhost,
            host: "127.0.0.1",
            requireToken: true,
            configureTestServices: services =>
            {
                services.AddSingleton<ILocalTokenAuthService>(new StubLocalTokenAuthService());
                services.AddSingleton<IHarnessBridgeTokens>(new FakeBridgeTokens());
                services.AddSingleton<IHarnessMcpCalls>(new FakeMcpCalls());
                services.AddSingleton<IHarnessCanvasCallerResolver>(new FakeCallers());
                services.AddSingleton<IScreenshotter>(new FakeScreenshotter());
            });

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
            VALUES ('sess-owner', 'ws-owner', 'inst-owner', 'cc-1', 'Owner', 'active', '/ws-owner',
                    'running', 'active', '2026-09-01T10:00:00+00:00', 'owner-user');
            """);
    }

    private sealed class FakeBridgeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == Token;
    }

    /// <summary>A harness whose client names the call in <c>_meta["test/callId"]</c>, and says when a subagent made it.</summary>
    private sealed class FakeMcpCalls : IHarnessMcpCalls
    {
        public Task<HarnessMcpCall?> PlaceAsync(string bridgeToken, JsonElement meta, CancellationToken ct = default)
        {
            if (bridgeToken != Token)
                return Task.FromResult<HarnessMcpCall?>(null);
            var callId = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("test/callId", out var id) ? id.GetString() : null;
            var subagent = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("test/subagent", out var flag) && flag.GetBoolean();
            return Task.FromResult<HarnessMcpCall?>(new HarnessMcpCall(subagent ? "subagent" : HarnessSessionId, callId));
        }
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken != Token
                ? null
                : harnessSessionId switch
                {
                    HarnessSessionId => new HarnessCanvasCaller(SessionId, Owner),
                    "subagent" => new HarnessCanvasCaller(SessionId, Owner, ViaParent: true),
                    _ => null,
                });
    }

    private sealed class FakeScreenshotter : IScreenshotter
    {
        public static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

        public Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default)
            => Task.FromResult(ScreenshotOutcome.Ok(Png, request.Width, request.Height));
    }

    private sealed class StubLocalTokenAuthService : ILocalTokenAuthService
    {
        public string Token { get; } = "1234567890abcdef";

        public bool ValidateToken(string candidate) => string.Equals(candidate, Token, StringComparison.Ordinal);
    }
}
