using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.TestHarness;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Integration tests that verify the subscribe-to-session flow works end-to-end:
/// start a test session, connect via SignalR, subscribe, verify the snapshot contains
/// messages from opencode's API (not from Fleet's database).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionProxySnapshotTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;
    private HubConnection _hub = null!;

    public void Dispose()
    {
        // No resources to dispose
    }

    public async Task InitializeAsync()
    {
        _server = new SignalRTestServer();
        await _server.StartAsync();

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_server.ServerUrl}/hubs/session-events")
            .Build();

        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeToSession_returns_snapshot_with_messages_from_test_harness()
    {
        // Arrange: configure test harness to return messages
        var messageId = $"msg-{Guid.NewGuid():N}";
        
        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario.WithAssistantMessage(messageId, "Hello from test harness!");
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to the session
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot structure
        snapshot.ValueKind.ShouldBe(JsonValueKind.Object);
        
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");
        messages.ValueKind.ShouldBe(JsonValueKind.Array);

        // Assert: snapshot contains the message from the test harness
        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(1, 
            $"Expected 1 message from test harness but got {messageArray.Count}. Messages: {messages.GetRawText()}");

        var message = messageArray[0];
        message.TryGetProperty("info", out var info).ShouldBeTrue(
            $"Message missing 'info'. Actual: {message.GetRawText()}");
        
        info.TryGetProperty("id", out var idProp).ShouldBeTrue(
            $"Message info missing 'id'. Actual: {info.GetRawText()}");
        idProp.GetString().ShouldBe(messageId);

        info.TryGetProperty("role", out var roleProp).ShouldBeTrue(
            $"Message info missing 'role'. Actual: {info.GetRawText()}");
        roleProp.GetString().ShouldBe("assistant");

        // Assert: message has parts
        message.TryGetProperty("parts", out var parts).ShouldBeTrue(
            $"Message missing 'parts'. Actual: {message.GetRawText()}");
        parts.ValueKind.ShouldBe(JsonValueKind.Array);

        var partsArray = parts.EnumerateArray().ToList();
        partsArray.Count.ShouldBe(1, 
            $"Expected 1 part but got {partsArray.Count}. Parts: {parts.GetRawText()}");

        var part = partsArray[0];
        part.TryGetProperty("text", out var textProp).ShouldBeTrue(
            $"Part missing 'text'. Actual: {part.GetRawText()}");
        textProp.GetString().ShouldBe("Hello from test harness!");
    }

    [Fact]
    public async Task SubscribeToSession_returns_snapshot_with_multiple_messages()
    {
        // Arrange: configure test harness with multiple messages
        var message1Id = $"msg-{Guid.NewGuid():N}";
        var message2Id = $"msg-{Guid.NewGuid():N}";
        
        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario
                .WithUserMessage(message1Id, "User question", DateTimeOffset.UtcNow.AddMinutes(-1))
                .WithAssistantMessage(message2Id, "Assistant response", DateTimeOffset.UtcNow);
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to the session
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot contains both messages
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue();
        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(2, 
            $"Expected 2 messages from test harness but got {messageArray.Count}");

        // Verify first message
        var msg1 = messageArray[0];
        msg1.GetProperty("info").GetProperty("id").GetString().ShouldBe(message1Id);
        msg1.GetProperty("info").GetProperty("role").GetString().ShouldBe("user");
        msg1.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("User question");

        // Verify second message
        var msg2 = messageArray[1];
        msg2.GetProperty("info").GetProperty("id").GetString().ShouldBe(message2Id);
        msg2.GetProperty("info").GetProperty("role").GetString().ShouldBe("assistant");
        msg2.GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("Assistant response");
    }

    [Fact]
    public async Task SubscribeToSession_returns_snapshot_with_tool_parts()
    {
        // Arrange: configure test harness with a message containing a tool part
        var messageId = $"msg-{Guid.NewGuid():N}";
        var toolCallId = $"call-{Guid.NewGuid():N}";
        
        var parts = new MessagePart[]
        {
            new TextPart("Let me run a command..."),
            new ToolUsePart(
                ToolCallId: toolCallId,
                ToolName: "bash",
                Arguments: JsonSerializer.SerializeToElement(new { command = "echo test" }),
                State: ToolUseState.Completed),
            new ToolResultPart(
                ToolCallId: toolCallId,
                Content: "test",
                IsError: false)
        };

        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario.WithAssistantMessageParts(messageId, parts);
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to the session
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot contains the message with tool parts
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue();
        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(1);

        var message = messageArray[0];
        message.TryGetProperty("parts", out var messageParts).ShouldBeTrue();
        var partsArray = messageParts.EnumerateArray().ToList();
        
        // Note: ToolResultPart is merged into ToolUsePart's state, so we expect 2 parts (text + tool)
        partsArray.Count.ShouldBe(2, 
            $"Expected 2 parts (text + tool) but got {partsArray.Count}. Parts: {messageParts.GetRawText()}");

        // Verify text part
        var textPart = partsArray[0];
        textPart.TryGetProperty("text", out var textProp).ShouldBeTrue();
        textProp.GetString().ShouldBe("Let me run a command...");

        // Verify tool use part
        var toolPart = partsArray[1];
        toolPart.TryGetProperty("tool", out var toolProp).ShouldBeTrue(
            $"Tool part missing 'tool' field. Actual: {toolPart.GetRawText()}");
        toolProp.GetString().ShouldBe("bash");
        
        toolPart.TryGetProperty("callID", out var callIdProp).ShouldBeTrue(
            $"Tool part missing 'callID' field. Actual: {toolPart.GetRawText()}");
        callIdProp.GetString().ShouldBe(toolCallId);

        toolPart.TryGetProperty("state", out var stateProp).ShouldBeTrue(
            $"Tool part missing 'state' field. Actual: {toolPart.GetRawText()}");
        stateProp.TryGetProperty("status", out var statusProp).ShouldBeTrue();
        statusProp.GetString().ShouldBe("completed");
    }

    [Fact]
    public async Task SubscribeToSession_returns_empty_messages_for_new_session()
    {
        // Arrange: configure test harness with no messages
        _server.TestHarnessRuntime.Configure(scenario =>
        {
            // Empty scenario - no messages
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to the session
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot has empty messages array
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");
        messages.ValueKind.ShouldBe(JsonValueKind.Array);

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(0, 
            $"Expected empty messages array for new session but got {messageArray.Count} messages");
    }

    private async Task<string> CreateSessionAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        // Register workspace root
        var rootPayload = JsonSerializer.Serialize(new { path = tempDir });
        await http.PostAsync("/api/workspace-roots",
            new StringContent(rootPayload, System.Text.Encoding.UTF8, "application/json"));

        // Create session
        var createPayload = JsonSerializer.Serialize(new
        {
            directory = tempDir,
            title = $"Proxy Snapshot Test {Guid.NewGuid():N}",
            harnessType = "opencode"
        });
        var response = await http.PostAsync("/api/sessions",
            new StringContent(createPayload, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Try common response shapes
        if (doc.RootElement.TryGetProperty("session", out var sessionObj)
            && sessionObj.TryGetProperty("id", out var idProp))
        {
            return idProp.GetString()!;
        }

        if (doc.RootElement.TryGetProperty("id", out var directId))
        {
            return directId.GetString()!;
        }

        throw new InvalidOperationException($"Could not extract session ID from response: {body}");
    }
}
