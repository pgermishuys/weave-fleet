using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Reads newline-delimited JSON from a <c>claude</c> process's stdout stream and
/// deserializes each line into a <see cref="ClaudeCodeStreamMessage"/>.
/// Stateless — purely functional.
/// </summary>
internal static class ClaudeCodeStdioClient
{
    private static readonly Action<ILogger, string, Exception?> LogMalformedLine =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "MalformedLine"),
            "claude stdout: skipping malformed JSON line: {Line}");

    /// <summary>
    /// Reads lines from <paramref name="stdout"/> and yields deserialized messages.
    /// Skips blank lines and malformed JSON (logs warnings).
    /// Completes when the stream ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    internal static async IAsyncEnumerable<ClaudeCodeStreamMessage> ReadMessagesAsync(
        StreamReader stdout,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (line is null) yield break; // EOF

            if (string.IsNullOrWhiteSpace(line)) continue;

            ClaudeCodeStreamMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(
                    line, ClaudeCodeJsonOptions.Default);
            }
            catch (JsonException)
            {
                LogMalformedLine(logger, line.Length > 200 ? line[..200] : line, null);
                continue;
            }

            if (msg is not null) yield return msg;
        }
    }
}
