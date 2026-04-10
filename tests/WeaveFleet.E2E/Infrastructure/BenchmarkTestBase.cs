using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.TestHarness;
using Xunit.Abstractions;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Abstract base class for Playwright E2E benchmark tests.
/// Extends <see cref="E2ETestBase"/> with always-on trace capture, longer timeouts,
/// bulk session creation, synthetic background traffic helpers, and metric artifact output.
/// </summary>
[Trait("Category", "Benchmark")]
public abstract class BenchmarkTestBase : E2ETestBase
{
    private readonly FleetWebApplicationFactory _factory;
    private AsyncServiceScope? _orchestratorScope;
    private readonly string _benchmarkRunId = Guid.NewGuid().ToString("N")[..8];
    private string _benchmarkScenario = "unspecified";
    private string _benchmarkTestMethod = "unknown";

    protected BenchmarkTestBase(
        FleetWebApplicationFactory factory,
        PlaywrightFixture playwright,
        ITestOutputHelper output)
        : base(factory, playwright)
    {
        _factory = factory;
        Output = output;
    }

    /// <summary>The xUnit output helper for writing metrics reports to test output.</summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>The PerformanceMetrics collector for this test.</summary>
    protected PerformanceMetrics Metrics { get; } = new();

    /// <summary>Sets benchmark metadata used in artifact filenames and JSON output.</summary>
    protected void SetBenchmarkContext(string scenario, string testMethod)
    {
        _benchmarkScenario = scenario;
        _benchmarkTestMethod = testMethod;
    }

    // ── IAsyncLifetime overrides ─────────────────────────────────────────────

    /// <summary>
    /// Override InitializeAsync to bump Playwright timeouts to accommodate load.
    /// </summary>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Raise timeouts for benchmark scenarios where load may cause slower renders
        Page.SetDefaultTimeout(15_000);
        Page.SetDefaultNavigationTimeout(15_000);
    }

    /// <summary>
    /// Override DisposeAsync to always save trace (not just on failure),
    /// write metrics report to test output, and save metrics JSON artifact.
    /// </summary>
    public override async Task DisposeAsync()
    {
        // Always mark as failed so E2ETestBase saves the trace
        MarkFailed();

        // Write metrics report to test output
        try
        {
            Output.WriteLine(Metrics.ToReport());
        }
        catch
        {
            // best effort — don't let reporting mask test failures
        }

        // Write metrics JSON artifact
        try
        {
            var artifactsDir = GetBenchmarkArtifactsDirectory();
            var testName = GetBenchmarkArtifactName();
            Directory.CreateDirectory(artifactsDir);
            var metricsPath = Path.Combine(artifactsDir, $"{testName}-metrics.json");
            await File.WriteAllTextAsync(metricsPath, Metrics.ToJson(new BenchmarkMetricsMetadata(
                TestClass: GetType().Name,
                TestMethod: _benchmarkTestMethod,
                Scenario: _benchmarkScenario,
                BenchmarkRunId: _benchmarkRunId,
                CapturedAtUtc: DateTimeOffset.UtcNow)));
        }
        catch
        {
            // best effort
        }

        // Dispose the orchestrator scope if created
        if (_orchestratorScope.HasValue)
        {
            try { await _orchestratorScope.Value.DisposeAsync(); }
            catch { /* best effort */ }
        }

        await base.DisposeAsync();
    }

    // ── DI convenience helpers ───────────────────────────────────────────────

    /// <summary>Returns the <see cref="InstanceTracker"/> singleton from the running app's DI container.</summary>
    protected InstanceTracker GetInstanceTracker()
        => _factory.KestrelServices.GetRequiredService<InstanceTracker>();

    // ── Session creation helpers ─────────────────────────────────────────────

    /// <summary>
    /// Create <paramref name="count"/> sessions via the orchestrator and return their (SessionId, InstanceId) pairs.
    /// Configures a no-op scenario before creation so all spawns succeed without prompt responses.
    /// </summary>
    protected async Task<IReadOnlyList<(string SessionId, string InstanceId)>> CreateSessionsAsync(
        int count, CancellationToken ct = default)
    {
        ConfigureScenario(_ => { }); // no-op scenario for all spawns

        var scope = _factory.KestrelServices.CreateAsyncScope();
        _orchestratorScope = scope;
        var orchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
        var results = new List<(string SessionId, string InstanceId)>(count);

        for (var i = 0; i < count; i++)
        {
            var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
            {
                Directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                Title = $"Bench-{_benchmarkRunId}-{i:D2}",
            }, ct);

            if (result.IsFailure)
                throw new InvalidOperationException(
                    $"Failed to create benchmark session {i}: {result.Error}");

            results.Add((result.Value.Session.Id, result.Value.InstanceId));
        }

        return results;
    }

    // ── Background traffic helpers ───────────────────────────────────────────

    /// <summary>
    /// Start pushing synthetic events to all given sessions at the specified rate.
    /// Returns a <see cref="CancellationTokenSource"/> that stops the traffic when cancelled.
    /// Events alternate between message.updated and message.part.updated.
    /// </summary>
    protected CancellationTokenSource StartBackgroundTraffic(
        IReadOnlyList<(string SessionId, string InstanceId)> sessions,
        TimeSpan interval)
    {
        var cts = new CancellationTokenSource();
        var tracker = GetInstanceTracker();

        foreach (var (sessionId, instanceId) in sessions)
        {
            var capturedSessionId = sessionId;
            var capturedInstanceId = instanceId;

            _ = Task.Run(async () =>
            {
                var partCounter = 0;
                var harnessInstance = (TestHarnessInstance)tracker.Get(capturedInstanceId)!;

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var evt = new HarnessEvent
                        {
                            Type = "message.part.updated",
                            SessionId = capturedInstanceId,
                            FleetSessionId = capturedSessionId,
                            Timestamp = DateTimeOffset.UtcNow,
                            Payload = JsonSerializer.SerializeToElement(new
                            {
                                part = new
                                {
                                    id = $"bench-part-{partCounter++}",
                                    sessionID = capturedInstanceId,
                                    messageID = $"bench-msg-{capturedSessionId}",
                                    type = "text",
                                    text = $"Background event {partCounter}"
                                }
                            })
                        };

                        await harnessInstance.PushEventAsync(evt, cts.Token);
                        await Task.Delay(interval, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // best effort — don't let background traffic crash the test
                        break;
                    }
                }
            }, cts.Token);
        }

        return cts;
    }

    /// <summary>
    /// Push a single burst of events to all sessions (for bursty load profile).
    /// </summary>
    protected async Task PushEventBurstAsync(
        IReadOnlyList<(string SessionId, string InstanceId)> sessions,
        int eventsPerSession)
    {
        var tracker = GetInstanceTracker();

        foreach (var (sessionId, instanceId) in sessions)
        {
            var harnessInstance = (TestHarnessInstance)tracker.Get(instanceId)!;

            for (var j = 0; j < eventsPerSession; j++)
            {
                var evt = new HarnessEvent
                {
                    Type = "message.part.updated",
                    SessionId = instanceId,
                    FleetSessionId = sessionId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        part = new
                        {
                            id = $"burst-part-{j}",
                            sessionID = instanceId,
                            messageID = $"burst-msg-{sessionId}",
                            type = "text",
                            text = $"Burst event {j}"
                        }
                    })
                };

                await harnessInstance.PushEventAsync(evt);
            }
        }
    }

    // ── Timing helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Measure the time from an action to a condition being met.
    /// Uses <see cref="Stopwatch"/> for high-resolution timing.
    /// Records the measurement into <see cref="Metrics"/>.
    /// </summary>
    protected async Task<double> MeasureAsync(
        string metricName,
        Func<Task> action,
        Func<Task> waitForCondition)
    {
        var sw = Stopwatch.StartNew();
        await action();
        await waitForCondition();
        sw.Stop();

        var elapsed = sw.Elapsed.TotalMilliseconds;
        Metrics.Record(metricName, elapsed);
        return elapsed;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string GetBenchmarkArtifactsDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        return Path.Combine(assemblyDir, "test-results");
    }

    private string GetBenchmarkArtifactName()
    {
        var className = GetType().Name;
        var scenario = SanitizeForFilename(_benchmarkScenario);
        var method = SanitizeForFilename(_benchmarkTestMethod);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        return $"{className}-{scenario}-{method}-{_benchmarkRunId}-{timestamp}";
    }

    private static string SanitizeForFilename(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalidChars.Contains(ch) ? '-' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
