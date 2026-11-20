using Microsoft.Extensions.Logging;
using WeaveFleet.Application.TerminalSessions;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>Reads terminal sessions' transcripts from where Claude Code keeps them (<see cref="ClaudeTranscriptReader"/>).</summary>
public sealed partial class ClaudeCodeTranscripts(ILogger<ClaudeCodeTranscripts> logger) : ITerminalTranscripts
{
    /// <inheritdoc />
    public async Task<TerminalTranscript?> ReadAsync(string sessionId, string folder, string? path, CancellationToken ct)
    {
        var file = ClaudeTranscriptLocator.IsTranscriptOf(path, sessionId) && File.Exists(path)
            ? path
            : ClaudeTranscriptLocator.Find(sessionId, folder);
        if (file is null)
            return null;

        try
        {
            // Claude Code is still writing to it while a session runs: share the file, and a half-written last line is skipped.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                lines.Add(line);
            return ClaudeTranscriptReader.Read(lines);
        }
        catch (IOException ex)
        {
            LogUnreadable(ex, sessionId);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the transcript of Claude Code session {SessionId}")]
    private partial void LogUnreadable(Exception ex, string sessionId);
}
