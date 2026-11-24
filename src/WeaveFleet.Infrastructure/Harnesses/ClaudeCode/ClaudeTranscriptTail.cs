using System.Text.Json;
using WeaveFleet.Application.TerminalSessions;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Tells whether someone else added turns to a session's transcript while Fleet's <c>claude</c> process sat idle. A
/// <c>claude --resume</c> typed in a terminal appends to the same file; the live process keeps its own copy of the
/// conversation in memory, so it would answer from a history that lacks those turns and then write its entries beside them.
/// </summary>
internal static class ClaudeTranscriptTail
{
    /// <summary>The transcript's file and its size now, or null when there is no file to read.</summary>
    public static (string Path, long Length)? Mark(string claudeSessionId, string folder, string? configDirectory)
    {
        try
        {
            var path = ClaudeTranscriptLocator.Find(claudeSessionId, folder, configDirectory);
            return path is null ? null : (path, new FileInfo(path).Length);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Whether a conversation entry (a user or assistant line of the main chain) was written after <paramref name="mark"/>.</summary>
    public static bool HasTurnsAfter((string Path, long Length) mark)
    {
        try
        {
            using var stream = new FileStream(mark.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= mark.Length)
                return false;

            stream.Seek(mark.Length, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var entry = JsonDocument.Parse(line);
                    var root = entry.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("type", out var type) && type.GetString() is "user" or "assistant"
                        && root.TryGetProperty("uuid", out _)
                        && !(root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // A write cut off, or not a line of ours.
                }
            }
        }
        catch (IOException)
        {
        }

        return false;
    }
}
