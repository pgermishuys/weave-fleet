using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

public sealed partial class SseEventScopingTests
{
    private const string RequiredOpenCodeVersion = "1.15.10";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NegativeAssertionWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Codifies verified OpenCode 1.15.10 behavior for pooled harness design:
    /// pooled mode must keep one <c>GET /event?directory=...</c> SSE subscription per
    /// active directory per pooled process, then demux by <c>properties.sessionID</c>.
    /// The global <c>GET /event</c> stream is subscribed here only as a guardrail; it
    /// must never be used as the sole event source for pooled sessions.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task directory_scoped_event_streams_emit_session_events_only_for_matching_directory()
    {
        if (!IsRequiredOpenCodeVersionAvailable())
        {
            return;
        }

        var rootDirectory = CreateTemporaryDirectory("opencode-sse-scope-root");
        var dirA = Path.Combine(rootDirectory, "dir-a");
        var dirB = Path.Combine(rootDirectory, "dir-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        await using var server = await OpenCodeServer.StartAsync(rootDirectory, CancellationToken.None);
        using var httpClient = server.CreateClient();
        using var collectorA = await SseCollector.StartAsync(httpClient, BuildEventPath(dirA), CancellationToken.None);
        using var collectorB = await SseCollector.StartAsync(httpClient, BuildEventPath(dirB), CancellationToken.None);
        using var globalCollector = await SseCollector.StartAsync(httpClient, "/event", CancellationToken.None);

        try
        {
            var sessionA = await CreateSessionAsync(httpClient, dirA, CancellationToken.None);
            var sessionB = await CreateSessionAsync(httpClient, dirB, CancellationToken.None);

            var eventA = await collectorA.WaitForSessionEventAsync(sessionA.Id, EventTimeout, CancellationToken.None);
            var eventB = await collectorB.WaitForSessionEventAsync(sessionB.Id, EventTimeout, CancellationToken.None);

            PathsShouldMatch(eventA.Directory, dirA);
            PathsShouldMatch(eventB.Directory, dirB);

            await Task.Delay(NegativeAssertionWindow);

            collectorA.ContainsSessionEvent(sessionB.Id).ShouldBeFalse(
                "GET /event?directory=dirA must not receive session events for dirB sessions.");
            collectorB.ContainsSessionEvent(sessionA.Id).ShouldBeFalse(
                "GET /event?directory=dirB must not receive session events for dirA sessions.");
            globalCollector.ContainsSessionEvent(sessionA.Id).ShouldBeFalse(
                "pooled mode must not rely on global GET /event as the sole source for dirA session events.");
            globalCollector.ContainsSessionEvent(sessionB.Id).ShouldBeFalse(
                "pooled mode must not rely on global GET /event as the sole source for dirB session events.");
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task<OpenCodeSession> CreateSessionAsync(
        HttpClient httpClient,
        string directory,
        CancellationToken ct)
    {
        var path = $"/session?directory={Uri.EscapeDataString(directory)}";
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(path, content, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return new OpenCodeSession(
            document.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("OpenCode session response did not include an id."));
    }

    private static string BuildEventPath(string directory) => $"/event?directory={Uri.EscapeDataString(directory)}";

    private static void PathsShouldMatch(string? actual, string expected)
    {
        actual.ShouldNotBeNull();
        NormalizeMacOsTemporaryPath(Path.GetFullPath(actual)).ShouldBe(
            NormalizeMacOsTemporaryPath(Path.GetFullPath(expected)));
    }

    private static string NormalizeMacOsTemporaryPath(string path) =>
        path.StartsWith("/private/var/", StringComparison.Ordinal)
            ? path["/private".Length..]
            : path;

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsRequiredOpenCodeVersionAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "opencode",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null || !process.WaitForExit(3000) || process.ExitCode != 0)
            {
                return false;
            }

            var version = process.StandardOutput.ReadToEnd().Trim();
            return string.Equals(version, RequiredOpenCodeVersion, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record OpenCodeSession(string Id);

    private sealed record OpenCodeEvent(string Type, string? SessionId, string? Directory);

    private sealed partial class OpenCodeServer : IAsyncDisposable
    {
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
        private static readonly Regex ReadyPattern = OpenCodeReadyPattern();
        private readonly Process _process;
        private readonly string _username;
        private readonly string _password;

        private OpenCodeServer(Process process, Uri baseUrl, string username, string password)
        {
            _process = process;
            BaseUrl = baseUrl;
            _username = username;
            _password = password;
        }

        private Uri BaseUrl { get; }

        public static async Task<OpenCodeServer> StartAsync(string workingDirectory, CancellationToken ct)
        {
            var username = "opencode";
            var password = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderr = new StringBuilder();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "opencode",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { "serve", "--hostname", "127.0.0.1", "--port", "0" },
                },
                EnableRaisingEvents = true,
            };

            process.StartInfo.Environment["OPENCODE_SERVER_USERNAME"] = username;
            process.StartInfo.Environment["OPENCODE_SERVER_PASSWORD"] = password;
            process.StartInfo.Environment["OPENAI_API_KEY"] = "fake-key";
            process.StartInfo.Environment["ANTHROPIC_API_KEY"] = "fake-key";
            process.StartInfo.Environment["OPENCODE_CONFIG_CONTENT"] = "{}";

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                var match = ReadyPattern.Match(args.Data);
                if (match.Success)
                {
                    ready.TrySetResult(new Uri($"http://{match.Groups["host"].Value}:{match.Groups["port"].Value}"));
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    stderr.AppendLine(args.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                if (!ready.Task.IsCompleted)
                {
                    ready.TrySetException(new InvalidOperationException(
                        $"opencode exited before serving. ExitCode={process.ExitCode}; stderr={stderr}"));
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var baseUrl = await ready.Task.WaitAsync(StartupTimeout, ct);
                return new OpenCodeServer(process, baseUrl, username, password);
            }
            catch
            {
                await StopProcessAsync(process);
                process.Dispose();
                throw;
            }
        }

        public HttpClient CreateClient()
        {
            var client = new HttpClient { BaseAddress = BaseUrl };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}")));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await StopProcessAsync(_process);
            _process.Dispose();
        }

        private static async Task StopProcessAsync(Process process)
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ShutdownTimeout);
        }

        [GeneratedRegex("opencode server listening on http://(?<host>[^:]+):(?<port>\\d+)", RegexOptions.CultureInvariant)]
        private static partial Regex OpenCodeReadyPattern();
    }

    private sealed class SseCollector : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _readTask;
        private readonly ConcurrentQueue<OpenCodeEvent> _events = new();

        private SseCollector(CancellationTokenSource cts, Task readTask, ConcurrentQueue<OpenCodeEvent> events)
        {
            _cts = cts;
            _readTask = readTask;
            _events = events;
        }

        public static async Task<SseCollector> StartAsync(HttpClient httpClient, string path, CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var events = new ConcurrentQueue<OpenCodeEvent>();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var readTask = Task.Run(async () =>
            {
                using (response)
                await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
                using (var reader = new StreamReader(stream))
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(cts.Token);
                        if (line is null)
                        {
                            break;
                        }

                        if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var parsed = TryParseEvent(line["data: ".Length..]);
                        if (parsed is not null)
                        {
                            events.Enqueue(parsed);
                        }
                    }
                }
            }, cts.Token);

            return new SseCollector(cts, readTask, events);
        }

        public async Task<OpenCodeEvent> WaitForSessionEventAsync(
            string sessionId,
            TimeSpan timeout,
            CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            while (!cts.IsCancellationRequested)
            {
                var match = _events.FirstOrDefault(evt => string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
            }

            throw new TimeoutException($"Timed out waiting for session event {sessionId}.");
        }

        public bool ContainsSessionEvent(string sessionId) =>
            _events.Any(evt => string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal));

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _readTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(IsCancellationException))
            {
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private static bool IsCancellationException(Exception ex) =>
            ex is OperationCanceledException;

        private static OpenCodeEvent? TryParseEvent(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return null;
                }

                var type = typeElement.GetString();
                if (type is null || !root.TryGetProperty("properties", out var properties))
                {
                    return null;
                }

                return new OpenCodeEvent(
                    type,
                    ReadString(properties, "sessionID", "sessionId", "session_id"),
                    ReadDirectory(properties));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadDirectory(JsonElement properties)
        {
            if (properties.TryGetProperty("info", out var info)
                && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("directory", out var directoryElement))
            {
                return directoryElement.GetString();
            }

            return ReadString(properties, "directory");
        }

        private static string? ReadString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }
            }

            return null;
        }
    }
}
