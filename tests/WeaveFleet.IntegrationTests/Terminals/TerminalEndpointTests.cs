using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using WeaveFleet.IntegrationTests.Sessions;

namespace WeaveFleet.IntegrationTests.Terminals;

/// <summary>
/// The terminal REST endpoints and socket on a real Kestrel server, with a real shell. Linux only, like the
/// other real-shell tests; macOS and Windows are on the plan's checklist.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TerminalEndpointTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private SignalRTestServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = new SignalRTestServer();
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    public void Dispose() => _http?.Dispose();

    [Fact]
    public async Task OpenAttachTypeResizeAndClose()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessionId = await CreateSessionAsync();

        var created = await _http.PostAsJsonAsync($"/api/sessions/{sessionId}/terminals", new { cols = 100, rows = 30 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var terminal = await created.Content.ReadFromJsonAsync<JsonElement>();
        var terminalId = terminal.GetProperty("id").GetString()!;
        terminal.GetProperty("status").GetString().ShouldBe("running");
        terminal.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();

        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/terminals");
        list.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ShouldBe([terminalId]);

        using var socket = await ConnectAsync(sessionId, terminalId, origin: _server.ServerUrl);
        await ReadUntilTextAsync(socket, """{"type":"ready"}""");

        await SendAsync(socket, "echo fleet-it-$((6*7))\r");
        await ReadUntilOutputAsync(socket, "fleet-it-42");

        await socket.SendAsync("""{"type":"resize","cols":90,"rows":20}"""u8.ToArray(), WebSocketMessageType.Text, true, default);
        await SendAsync(socket, "stty size\r");
        await ReadUntilOutputAsync(socket, "20 90");

        (await _http.DeleteAsync($"/api/sessions/{sessionId}/terminals/{terminalId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ReadUntilTextAsync(socket, """{"type":"exit","exitCode":null}""");
        var close = await ReadUntilClosedAsync(socket);
        close.ShouldBe(WebSocketCloseStatus.NormalClosure);
        var after = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/terminals");
        after.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ASecondConnection_GetsTheScrollback()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessionId = await CreateSessionAsync();
        var terminalId = await CreateTerminalAsync(sessionId);

        using (var first = await ConnectAsync(sessionId, terminalId, origin: null))
        {
            await ReadUntilTextAsync(first, """{"type":"ready"}""");
            await SendAsync(first, "echo remembered-$((1+1))\r");
            await ReadUntilOutputAsync(first, "remembered-2");
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        }

        using var second = await ConnectAsync(sessionId, terminalId, origin: null);
        var replay = await ReadUntilTextAsync(second, """{"type":"ready"}""");
        replay.ShouldContain("remembered-2");
    }

    [Fact]
    public async Task APageFromAnotherOrigin_CantAttach()
    {
        var sessionId = await CreateSessionAsync();
        var terminalId = await CreateTerminalAsync(sessionId);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://evil.localhost:4000");
        socket.Options.CollectHttpResponseDetails = true;

        await Should.ThrowAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri(sessionId, terminalId), default));
        socket.HttpStatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnUnknownSession_IsNotFound()
    {
        (await _http.GetAsync("/api/sessions/nope/terminals")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _http.PostAsJsonAsync("/api/sessions/nope/terminals", new { cols = 80, rows = 24 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        await Should.ThrowAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri("nope", "t_nope"), default));
        socket.HttpStatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ClientConfig_SaysTerminalsAreOn()
    {
        var config = await _http.GetFromJsonAsync<JsonElement>("/api/config/client");

        config.GetProperty("terminalEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task OpeningAndClosing_ArePushedToTheSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessionId = await CreateSessionAsync();
        var events = new List<JsonElement>();
        var received = new SemaphoreSlim(0);
        await using var hub = new HubConnectionBuilder().WithUrl($"{_server.ServerUrl}/hubs/session-events").Build();
        hub.On<string, long, JsonElement>("Event", (_, _, data) =>
        {
            var type = data.GetProperty("type").GetString();
            if (type is "terminal.opened" or "terminal.closed")
            {
                lock (events) events.Add(data.Clone());
                received.Release();
            }
        });
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var terminalId = await CreateTerminalAsync(sessionId);
        (await received.WaitAsync(Timeout)).ShouldBeTrue("no terminal.opened");
        await _http.DeleteAsync($"/api/sessions/{sessionId}/terminals/{terminalId}");
        (await received.WaitAsync(Timeout)).ShouldBeTrue("no terminal.closed");

        var opened = events[0];
        opened.GetProperty("type").GetString().ShouldBe("terminal.opened");
        opened.GetProperty("eventId").ValueKind.ShouldBe(JsonValueKind.Null);
        var properties = opened.GetProperty("properties");
        properties.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        properties.GetProperty("terminalId").GetString().ShouldBe(terminalId);
        properties.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        // Session events leave out null fields, so no exit code means the property isn't there.
        properties.TryGetProperty("exitCode", out _).ShouldBeFalse();
        events[1].GetProperty("type").GetString().ShouldBe("terminal.closed");
        events[1].GetProperty("properties").GetProperty("terminalId").GetString().ShouldBe(terminalId);
    }

    [Fact]
    public async Task TheSetupTerminal_StartsInTheHomeFolder_AndANewOneEndsTheLast()
    {
        if (!OperatingSystem.IsLinux()) return;
        var first = await CreateSetupTerminalAsync();
        var second = await CreateSetupTerminalAsync();

        (await _http.DeleteAsync($"/api/setup/terminals/{first}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", _server.ServerUrl);
        await socket.ConnectAsync(new Uri($"{_server.ServerUrl.Replace("http://", "ws://", StringComparison.Ordinal)}/api/setup/terminals/{second}/socket?cols=100&rows=30"), default);
        await ReadUntilTextAsync(socket, """{"type":"ready"}""");
        await SendAsync(socket, "echo \"cwd=$(pwd)\"\r");
        await ReadUntilOutputAsync(socket, $"cwd={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");

        (await _http.DeleteAsync($"/api/setup/terminals/{second}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ReadUntilTextAsync(socket, """{"type":"exit","exitCode":null}""");
    }

    private async Task<string> CreateSetupTerminalAsync()
    {
        var created = await _http.PostAsJsonAsync("/api/setup/terminals", new { cols = 100, rows = 30 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync()
    {
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var response = await _http.PostAsJsonAsync("/api/sessions", new
        {
            directory = tempDir,
            title = $"Terminal test {Guid.NewGuid():N}",
            harnessType = "opencode",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("session", out var session)
            ? session.GetProperty("id").GetString()!
            : body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateTerminalAsync(string sessionId)
    {
        var created = await _http.PostAsJsonAsync($"/api/sessions/{sessionId}/terminals", new { cols = 100, rows = 30 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private Uri SocketUri(string sessionId, string terminalId)
        => new($"{_server.ServerUrl.Replace("http://", "ws://", StringComparison.Ordinal)}/api/sessions/{sessionId}/terminals/{terminalId}/socket?cols=100&rows=30");

    private async Task<ClientWebSocket> ConnectAsync(string sessionId, string terminalId, string? origin)
    {
        var socket = new ClientWebSocket();
        if (origin is not null)
            socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(SocketUri(sessionId, terminalId), default);
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, string input)
        => socket.SendAsync(Encoding.UTF8.GetBytes(input), WebSocketMessageType.Binary, true, default);

    /// <summary>Reads until a text message equal to <paramref name="text"/>; returns the output seen before it.</summary>
    private static async Task<string> ReadUntilTextAsync(ClientWebSocket socket, string text)
    {
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            var (type, data) = await ReceiveAsync(socket, cts.Token);
            if (type == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Closed before '{text}'. Output: {output}");
            if (type == WebSocketMessageType.Text && data == text)
                return output.ToString();
            if (type == WebSocketMessageType.Binary)
                output.Append(data);
        }
    }

    private static async Task ReadUntilOutputAsync(ClientWebSocket socket, string text)
    {
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(Timeout);
        while (!output.ToString().Contains(text, StringComparison.Ordinal))
        {
            var (type, data) = await ReceiveAsync(socket, cts.Token);
            if (type == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Closed before '{text}'. Output: {output}");
            if (type == WebSocketMessageType.Binary)
                output.Append(data);
        }
    }

    private static async Task<WebSocketCloseStatus?> ReadUntilClosedAsync(ClientWebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            var (type, _) = await ReceiveAsync(socket, cts.Token);
            if (type == WebSocketMessageType.Close)
                return socket.CloseStatus;
        }
    }

    private static async Task<(WebSocketMessageType Type, string Data)> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
        return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()));
    }
}
