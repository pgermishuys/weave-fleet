using System.Net.Http.Headers;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.TerminalSessions;

namespace WeaveFleet.Api;

/// <summary>
/// <c>fleet mcp</c>: a stdio MCP server that Claude Code starts with each session. It has no tools. It exists so Fleet
/// learns of the session: it tells the Fleet on this machine the session's id and folder when it starts and again when it
/// ends (Claude Code closes its input), and Fleet reads the turns from the session's transcript. An MCP server never sees
/// prompts or replies.
/// </summary>
/// <remarks>
/// It always answers the MCP handshake, whether or not a Fleet is running or answers, so the terminal session carries on
/// whatever happens to Fleet. It writes nothing to stdout but MCP messages. The report is signed with the machine token that
/// sits next to the database (or <c>WEAVE_FLEET_AUTH_TOKEN</c>), over loopback, as <c>fleet node pair</c> is; the token is
/// never on a command line.
/// </remarks>
public static class TerminalMcpCommand
{
    public const string Flag = "--terminal-mcp";

    private const string DefaultProtocolVersion = "2025-06-18";
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Runs the server on the process's standard input and output until Claude Code closes its input.</summary>
    public static async Task<int> RunAsync(
        string host,
        int port,
        string databasePath,
        string? tokenFromEnvironment,
        string version,
        HttpClient http,
        TextReader input,
        TextWriter output,
        Func<string, string?> environment,
        string currentDirectory,
        TimeSpan? heartbeat = null,
        CancellationToken cancellationToken = default)
    {
        var token = !string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? tokenFromEnvironment.Trim()
            : File.Exists(MachineIdentityStore.GetFilePath(databasePath)) ? new MachineIdentityStore(databasePath).Get().AccessToken : null;

        // A wildcard bind answers on loopback; a specific address answers only there.
        var target = host is "0.0.0.0" or "::" or "[::]" or "" ? "127.0.0.1" : host.Trim('[', ']');
        var url = $"http://{(target.Contains(':') ? $"[{target}]" : target)}:{port}/api/machine/terminal-sessions";

        async Task ReportAsync(TerminalSessionReport report)
        {
            if (token is null)
                return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReportTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(Body(report), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // No Fleet answered: the terminal session doesn't depend on it.
            }
        }

        return await new Server(version, ReportAsync, environment, currentDirectory, heartbeat ?? TerminalSessions.HeartbeatEvery)
            .RunAsync(input, output, cancellationToken).ConfigureAwait(false);
    }

    private static string Body(TerminalSessionReport report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event", report.Event);
            writer.WriteString("sessionId", report.SessionId);
            writer.WriteString("folder", report.Folder);
            if (report.TranscriptPath is not null)
                writer.WriteString("transcriptPath", report.TranscriptPath);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The MCP side, apart from how a report reaches Fleet, so a test can drive it.</summary>
    internal sealed class Server(
        string version,
        Func<TerminalSessionReport, Task> report,
        Func<string, string?> environment,
        string currentDirectory,
        TimeSpan heartbeat)
    {
        public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var signals = StopOnSignals(stop);

            // Told at once, not waited for: the handshake comes first.
            var started = report(Report(TerminalSessions.Started));
            // Claude Code usually kills this process when the session ends, without closing its input first, so "ended"
            // can't be counted on. Fleet takes the silence after the last heartbeat as the end.
            var beats = BeatAsync(stop.Token);

            try
            {
                while (await input.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    if (Answer(line) is { } answer)
                    {
                        await output.WriteLineAsync(answer).ConfigureAwait(false);
                        await output.FlushAsync(stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Told to stop.
            }

            await stop.CancelAsync().ConfigureAwait(false);
            await beats.ConfigureAwait(false);
            await started.ConfigureAwait(false);
            await report(Report(TerminalSessions.Ended)).ConfigureAwait(false);
            return 0;
        }

        private async Task BeatAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(heartbeat);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    await report(Report(TerminalSessions.Heartbeat)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The session ended.
            }
        }

        /// <summary>A polite stop (Claude Code asking, or the terminal closing) is the end of the session too: tell Fleet.</summary>
        private static Registrations StopOnSignals(CancellationTokenSource stop)
        {
            var registrations = new List<IDisposable>();
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
            {
                try
                {
                    registrations.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        stop.Cancel();
                    }));
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
                {
                    // Not a signal this platform has.
                }
            }

            return new Registrations(registrations);
        }

        private sealed class Registrations(List<IDisposable> items) : IDisposable
        {
            public void Dispose()
            {
                foreach (var item in items)
                    item.Dispose();
            }
        }

        internal TerminalSessionReport Report(string kind)
        {
            // The folder the session runs in; the project dir only when the process somehow has no working directory.
            var folder = string.IsNullOrEmpty(currentDirectory) ? environment("CLAUDE_PROJECT_DIR") : currentDirectory;
            var sessionId = environment("CLAUDE_CODE_SESSION_ID");
            string? transcript = null;
            if (folder is not null)
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    // Not told which: the newest transcript in the folder is the best there is.
                    transcript = ClaudeTranscriptLocator.Newest(folder);
                    sessionId = transcript is null ? null : Path.GetFileNameWithoutExtension(transcript);
                }
                else
                {
                    transcript = ClaudeTranscriptLocator.Find(sessionId, folder);
                }
            }

            return new TerminalSessionReport(kind, sessionId, folder, transcript);
        }

        /// <summary>The answer to one line of input; null for a notification, which has none, and for what isn't JSON-RPC.</summary>
        internal string? Answer(string line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id)
                    || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    switch (method.GetString())
                    {
                        case "initialize":
                            writer.WritePropertyName("result");
                            writer.WriteStartObject();
                            writer.WriteString("protocolVersion", ProtocolVersion(root));
                            writer.WriteStartObject("capabilities");
                            writer.WriteStartObject("tools");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                            writer.WriteStartObject("serverInfo");
                            writer.WriteString("name", "fleet");
                            writer.WriteString("version", version);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                            break;
                        case "tools/list":
                            writer.WritePropertyName("result");
                            writer.WriteStartObject();
                            writer.WriteStartArray("tools");
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                            break;
                        case "ping":
                            writer.WritePropertyName("result");
                            writer.WriteStartObject();
                            writer.WriteEndObject();
                            break;
                        default:
                            writer.WritePropertyName("error");
                            writer.WriteStartObject();
                            writer.WriteNumber("code", -32601);
                            writer.WriteString("message", "Method not found");
                            writer.WriteEndObject();
                            break;
                    }

                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static string ProtocolVersion(JsonElement request)
            => request.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
               && parameters.TryGetProperty("protocolVersion", out var asked) && asked.ValueKind == JsonValueKind.String
                ? asked.GetString()!
                : DefaultProtocolVersion;
    }
}
