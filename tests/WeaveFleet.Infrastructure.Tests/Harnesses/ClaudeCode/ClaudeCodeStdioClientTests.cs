using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeStdioClientTests
{
    private static StreamReader MakeStream(params string[] lines)
    {
        var content = string.Join("\n", lines) + "\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        return new StreamReader(new MemoryStream(bytes));
    }

    [Fact]
    public async Task ReadMessagesAsync_ValidNdjsonStream_YieldsAllMessages()
    {
        var reader = MakeStream(
            """{"type":"system","subtype":"init","session_id":"sess-1"}""",
            """{"type":"assistant","message":{"id":"msg-1","content":[]}}""",
            """{"type":"result","subtype":"success","session_id":"sess-1"}"""
        );

        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(reader, NullLogger.Instance, CancellationToken.None)
            .ToListAsync();

        messages.Count.ShouldBe(3);
        messages[0].ShouldBeOfType<ClaudeCodeSystemMessage>();
        messages[1].ShouldBeOfType<ClaudeCodeAssistantMessage>();
        messages[2].ShouldBeOfType<ClaudeCodeResultMessage>();
    }

    [Fact]
    public async Task ReadMessagesAsync_BlankLines_AreSkipped()
    {
        var reader = MakeStream(
            """{"type":"system","subtype":"init","session_id":"sess-1"}""",
            "",
            "   ",
            """{"type":"result","subtype":"success","session_id":"sess-1"}"""
        );

        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(reader, NullLogger.Instance, CancellationToken.None)
            .ToListAsync();

        messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReadMessagesAsync_MalformedJson_IsSkippedGracefully()
    {
        var reader = MakeStream(
            """{"type":"system","subtype":"init","session_id":"sess-1"}""",
            "this is not json {{{",
            """{"type":"result","subtype":"success","session_id":"sess-1"}"""
        );

        // Should NOT throw — malformed line is skipped and logged
        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(reader, NullLogger.Instance, CancellationToken.None)
            .ToListAsync();

        messages.Count.ShouldBe(2);
        messages[0].ShouldBeOfType<ClaudeCodeSystemMessage>();
        messages[1].ShouldBeOfType<ClaudeCodeResultMessage>();
    }

    [Fact]
    public async Task ReadMessagesAsync_EmptyStream_YieldsNothing()
    {
        var reader = MakeStream(); // No lines (will just be a newline)
        // Overwrite with a truly empty stream
        var emptyReader = new StreamReader(new MemoryStream([]));

        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(emptyReader, NullLogger.Instance, CancellationToken.None)
            .ToListAsync();

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadMessagesAsync_CancellationToken_StopsReading()
    {
        using var cts = new CancellationTokenSource();

        // Build a stream with many messages
        var lines = Enumerable.Range(0, 100)
            .Select(i => $$$"""{"type":"system","subtype":"init","session_id":"sess-{{{i}}}"}""")
            .ToArray();
        var reader = MakeStream(lines);

        // Cancel almost immediately
        await cts.CancelAsync();

        // Should return without reading all 100 messages
        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(reader, NullLogger.Instance, cts.Token)
            .ToListAsync(CancellationToken.None);

        // With pre-cancelled token, should yield 0 or few messages (not all 100)
        messages.Count.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task ReadMessagesAsync_UnknownMessageType_DeserializesAsBaseType()
    {
        var reader = MakeStream(
            """{"type":"heartbeat","interval_ms":1000}"""
        );

        var messages = await ClaudeCodeStdioClient
            .ReadMessagesAsync(reader, NullLogger.Instance, CancellationToken.None)
            .ToListAsync();

        // Unknown type falls back to base ClaudeCodeStreamMessage (not skipped, not thrown)
        messages.Count.ShouldBe(1);
        messages[0].ShouldBeOfType<ClaudeCodeStreamMessage>();
    }
}
