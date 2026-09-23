extern alias FakeLlm;

using System.Diagnostics;
using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.IntegrationTests.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// An OpenCode (v1) session and an OpenCode 2 session in one Fleet, as a user with both installed has them: V2 in its
/// separate install next to OpenCode 1, as CI installs them. Both run real servers with scratch HOMEs against one
/// scripted model. Each test starts a Fleet of its own, since the first one stops it.
/// <para>
/// OpenCode 1's pooled process is started the way its own live tests start it (<see cref="SessionMessagesLiveTests"/>):
/// through the runtime with the scratch HOME in its environment, then registered as the orchestrator registers a
/// session. Created through the orchestrator it would run with the test runner's HOME. From there both sessions are
/// driven through the orchestrator, as the client drives them.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed class OpenCodeSideBySideLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [BothOpenCodesFact]
    public async Task An_OpenCode_1_session_and_an_OpenCode_2_session_run_side_by_side_and_stop_with_Fleet()
    {
        const string v1Hello = "Say hello, OpenCode 1. (side by side)";
        const string v2Hello = "Say hello, OpenCode 2. (side by side)";
        const string v1Reply = "Hello from OpenCode 1.";
        const string v2Reply = "Hello from OpenCode 2.";
        const string v1Slow = "Run OpenCode 1's slow check. (side by side)";
        const string v2Quick = "Quick one for OpenCode 2. (side by side)";
        const string v2Slow = "Run OpenCode 2's slow check. (side by side)";
        const string v1Quick = "Quick one for OpenCode 1. (side by side)";

        await using var both = await BothFleet.StartAsync();
        var fleet = both.Fleet;
        fleet.Answer(request =>
            LlmRequest.Starts(request, v1Hello) ? Text(v1Reply)
            : LlmRequest.Starts(request, v2Hello) ? Text(v2Reply)
            : LlmRequest.Starts(request, v1Slow) ? ToolCall("call_v1_slow", "bash", new { command = "sleep 10; echo v1-check-done", description = "OpenCode 1's check" })
            : LlmRequest.Continues(request, v1Slow) ? Text("OpenCode 1's check passed.")
            : LlmRequest.Starts(request, v2Quick) ? Text("OpenCode 2 answered while OpenCode 1 worked.")
            : LlmRequest.Starts(request, v2Slow) ? ToolCall("call_v2_slow", "shell", new { command = "sleep 10; echo v2-check-done", description = "OpenCode 2's check" })
            : LlmRequest.Continues(request, v2Slow) ? Text("OpenCode 2's check passed.")
            : LlmRequest.Starts(request, v1Quick) ? Text("OpenCode 1 answered while OpenCode 2 worked.")
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var v1 = await both.StartOpenCode1SessionAsync("side-by-side-v1", "OpenCode 1 side", cts.Token);
        var v2 = await fleet.CreateSessionAsync(fleet.NewFolder("side-by-side-v2"), "OpenCode 2 side", cts.Token);
        var events = fleet.Watch(cts.Token, v1.Id, v2);
        int Idles(string id) => events.For(id).Count(e => e.Type == "session.idle");

        // Both answer, prompted together.
        await Task.WhenAll(PromptAsync(fleet, v1.Id, v1Hello, cts.Token), PromptAsync(fleet, v2, v2Hello, cts.Token));
        await WaitForAsync(fleet, events, () => Idles(v1.Id) >= 1 && Idles(v2) >= 1, cts.Token);
        LatestTexts(events, v1.Id).ShouldContain(v1Reply);
        LatestTexts(events, v2).ShouldContain(v2Reply);

        // A turn on each while the other is working: the quick one ends first, and the slow one isn't ended by it.
        await PromptAsync(fleet, v1.Id, v1Slow, cts.Token);
        await WaitForAsync(fleet, events, () => IsRunning(events, v1.Id, "call_v1_slow"), cts.Token);
        var (v1Idles, v2Idles) = (Idles(v1.Id), Idles(v2));
        await PromptAsync(fleet, v2, v2Quick, cts.Token);
        await WaitForAsync(fleet, events, () => Idles(v2) > v2Idles, cts.Token);
        Idles(v1.Id).ShouldBe(v1Idles, "OpenCode 1's turn ended when OpenCode 2's did.");
        await WaitForAsync(fleet, events, () => Idles(v1.Id) > v1Idles, cts.Token);
        LatestTexts(events, v1.Id).ShouldContain("OpenCode 1's check passed.");
        LatestTexts(events, v2).ShouldContain("OpenCode 2 answered while OpenCode 1 worked.");

        await PromptAsync(fleet, v2, v2Slow, cts.Token);
        await WaitForAsync(fleet, events, () => IsRunning(events, v2, "call_v2_slow"), cts.Token);
        (v1Idles, v2Idles) = (Idles(v1.Id), Idles(v2));
        await PromptAsync(fleet, v1.Id, v1Quick, cts.Token);
        await WaitForAsync(fleet, events, () => Idles(v1.Id) > v1Idles, cts.Token);
        Idles(v2).ShouldBe(v2Idles, "OpenCode 2's turn ended when OpenCode 1's did.");
        await WaitForAsync(fleet, events, () => Idles(v2) > v2Idles, cts.Token);
        LatestTexts(events, v2).ShouldContain("OpenCode 2's check passed.");
        LatestTexts(events, v1.Id).ShouldContain("OpenCode 1 answered while OpenCode 2 worked.");

        // No cross-talk: each session's topic carried only its own conversation. Both harnesses' events go through
        // the same relay and broadcaster, so a session id mapped to the wrong session would show here.
        string[] v1Texts = [v1Hello, v1Reply, v1Slow, "v1-check-done", "OpenCode 1's check passed.", v1Quick, "OpenCode 1 answered while OpenCode 2 worked."];
        string[] v2Texts = [v2Hello, v2Reply, v2Quick, "OpenCode 2 answered while OpenCode 1 worked.", v2Slow, "v2-check-done", "OpenCode 2's check passed."];
        var v1Sent = string.Join("\n", events.For(v1.Id).SelectMany(e => Strings(e.Payload)));
        var v2Sent = string.Join("\n", events.For(v2).SelectMany(e => Strings(e.Payload)));
        v1Sent.ShouldContain(v1Reply);
        v2Sent.ShouldContain(v2Reply);
        foreach (var text in v2Texts)
            v1Sent.ShouldNotContain(text, customMessage: "OpenCode 2's conversation reached OpenCode 1's session.");
        foreach (var text in v1Texts)
            v2Sent.ShouldNotContain(text, customMessage: "OpenCode 1's conversation reached OpenCode 2's session.");
        v1Sent.ShouldNotContain(v2);
        v2Sent.ShouldNotContain(v1.Id);
        events.For(v1.Id).ShouldNotContain(e => e.Type == "session.error");
        events.For(v2).ShouldNotContain(e => e.Type == "session.error");

        // Reopened, each shows its own history and nothing of the other's.
        var v1History = await HistoryAsync(fleet, v1.Id, cts.Token);
        var v2History = await HistoryAsync(fleet, v2, cts.Token);
        v1History.ShouldBe(
            [v1Hello, v1Reply, v1Slow, "OpenCode 1's check passed.", v1Quick, "OpenCode 1 answered while OpenCode 2 worked."],
            $"OpenCode 1's history: {string.Join(" | ", v1History)}");
        v2History.ShouldBe(
            [v2Hello, v2Reply, v2Quick, "OpenCode 2 answered while OpenCode 1 worked.", v2Slow, "OpenCode 2's check passed."],
            $"OpenCode 2's history: {string.Join(" | ", v2History)}");

        // Both servers are Fleet's harness processes, and stopping Fleet stops both, with what they started.
        var v1Server = v1.Harness.ProcessId.ShouldNotBeNull();
        var v2Server = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(v2, cts.Token)).ProcessId.ShouldNotBeNull();
        ProcessGroupHelper.RunningProcesses.Select(p => p.Pid).ShouldContain(v1Server);
        ProcessGroupHelper.RunningProcesses.Select(p => p.Pid).ShouldContain(v2Server);
        var v1Processes = ProcessesWith("HOME", both.OpenCode1Home);
        var v2Processes = ProcessesWith("HOME", fleet.Runtime.ServerEnvironment["HOME"]);
        v1Processes.ShouldContain(v1Server);
        v2Processes.ShouldContain(v2Server);

        await cts.CancelAsync();
        await both.DisposeAsync();

        foreach (var pid in v1Processes)
            (await HasExitedAsync(pid, TimeSpan.FromSeconds(10))).ShouldBeTrue($"OpenCode 1 process {pid} outlived Fleet.");
        foreach (var pid in v2Processes)
            (await HasExitedAsync(pid, TimeSpan.FromSeconds(10))).ShouldBeTrue($"OpenCode 2 process {pid} outlived Fleet.");
        ProcessGroupHelper.RunningProcesses.ShouldNotContain(p => p.Pid == v1Server || p.Pid == v2Server);
    }

    [BothOpenCodesFact]
    public async Task An_OpenCode_2_session_messages_an_OpenCode_2_and_an_OpenCode_1_session_and_OpenCode_1_messages_back()
    {
        const string plain = "Tell the V2 docs session. (v2 to v2)";
        const string ask = "Ask the V1 tests session and hear back. (v2 to v1)";
        const string askBack = "Ask the V2 docs session and hear back. (v1 to v2)";
        const string toV2 = "The API changed; update the docs.";
        const string toV1 = "The API changed; update the tests.";
        const string backToV2 = "The tests changed; note it in the docs.";
        const string v2Docs = "Docs updated for the API change.";
        const string v1Tests = "Tests updated for the API change.";
        const string v2Noted = "Noted the test change in the docs.";
        const string update = "<fleet-session-update";

        await using var both = await BothFleet.StartAsync();
        var fleet = both.Fleet;
        using var cts = new CancellationTokenSource(Timeout);

        // Settings' Experimental switch, before any server starts, so both harnesses start with the tool.
        using (BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner))
        using (var scope = fleet.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, "true");

        var sender = await fleet.CreateSessionAsync(fleet.NewFolder("messages-sender"), "API change", cts.Token);
        var docs = await fleet.CreateSessionAsync(fleet.NewFolder("messages-docs"), "Update documentation", cts.Token);
        var tests = await both.StartOpenCode1SessionAsync("messages-tests", "Update tests", cts.Token);
        // notifyWhenDone is always given: OpenCode 2 checks a call against the tool's schema, where every Fleet tool
        // argument is required, and refuses one without it. OpenCode 1 doesn't check.
        fleet.Answer(request =>
            LlmRequest.Starts(request, plain) ? ToolCall("call_plain", "fleet_message", new { sessionId = docs, text = toV2, notifyWhenDone = false })
            : LlmRequest.Starts(request, ask) ? ToolCall("call_ask", "fleet_message", new { sessionId = tests.Id, text = toV1, notifyWhenDone = true })
            : LlmRequest.Starts(request, askBack) ? ToolCall("call_ask_back", "fleet_message", new { sessionId = docs, text = backToV2, notifyWhenDone = true })
            : LlmRequest.Continues(request, plain) || LlmRequest.Continues(request, ask) || LlmRequest.Continues(request, askBack) ? Text("Sent.")
            : LlmRequest.Starts(request, update) ? Text("Thanks.")
            : LlmRequest.Starts(request, toV2) ? Text(v2Docs)
            : LlmRequest.Starts(request, toV1) ? Text(v1Tests)
            : LlmRequest.Starts(request, backToV2) ? Text(v2Noted)
            : null);
        var events = fleet.Watch(cts.Token, sender, docs, tests.Id);
        List<string> Turns(string text) => [.. fleet.Llm.Queue.Requests.Where(r => LlmRequest.Starts(r, text))];
        string ToolResult(string prompt) => LlmRequest.LastToolText(fleet.Llm.Queue.Requests.First(r => LlmRequest.Continues(r, prompt)))!;

        // V2 to V2, without asking to hear back: the other session gets it from the sender, and that's all.
        await PromptAsync(fleet, sender, plain, cts.Token);
        await WaitForAsync(fleet, events, () => Turns(toV2).Count == 1 && LatestTexts(events, docs).Contains(v2Docs), cts.Token);
        LlmRequest.OfferedToolNames(Turns(plain).Single()).ShouldContain("fleet_message");
        ToolResult(plain).ShouldContain($"Delivered to Update documentation ({docs}).");
        LlmRequest.LastUserText(Turns(toV2).Single()).ShouldBe(SessionMessages.Wrap(sender, "API change", toV2));
        Messaged(events, docs).ShouldHaveSingleItem().ShouldBe(sender);

        // V2 to OpenCode 1, asking to hear back: the V1 session gets it from the V2 one, and its reply comes back.
        await PromptAsync(fleet, sender, ask, cts.Token);
        await WaitForAsync(fleet, events, () => Turns(update).Count == 1, cts.Token);
        ToolResult(ask).ShouldContain($"Delivered to Update tests ({tests.Id}).");
        LlmRequest.LastUserText(Turns(toV1).ShouldHaveSingleItem()).ShouldBe(SessionMessages.Wrap(sender, "API change", toV1));
        Messaged(events, tests.Id).ShouldHaveSingleItem().ShouldBe(sender);
        LlmRequest.LastUserText(Turns(update).Single()).ShouldBe(SessionMessages.WrapUpdate(tests.Id, "Update tests", "finished", v1Tests));
        Reported(events, sender).ShouldHaveSingleItem().ShouldBe(tests.Id);

        // And back: OpenCode 1 to V2, asking to hear back.
        await PromptAsync(fleet, tests.Id, askBack, cts.Token);
        await WaitForAsync(fleet, events, () => Turns(update).Count == 2, cts.Token);
        LlmRequest.OfferedToolNames(Turns(askBack).Single()).ShouldContain("fleet_message");
        ToolResult(askBack).ShouldContain($"Delivered to Update documentation ({docs}).");
        LlmRequest.LastUserText(Turns(backToV2).ShouldHaveSingleItem()).ShouldBe(SessionMessages.Wrap(tests.Id, "Update tests", backToV2));
        Messaged(events, docs).ShouldBe([sender, tests.Id]);
        LlmRequest.LastUserText(Turns(update)[1]).ShouldBe(SessionMessages.WrapUpdate(docs, "Update documentation", "finished", v2Noted));
        Reported(events, tests.Id).ShouldHaveSingleItem().ShouldBe(docs);

        // Nothing more: one message per call, one update per ask.
        await Task.Delay(3000, cts.Token);
        Turns(update).Count.ShouldBe(2);
        Reported(events, sender).Count.ShouldBe(1);
        Reported(events, docs).ShouldBeEmpty();
    }

    /// <summary>The senders of the messages <paramref name="sessionId"/> got, in order.</summary>
    private static List<string> Messaged(LiveEvents events, string sessionId)
        => [.. events.For(sessionId).Where(e => e.Type == "session.messaged").Select(e => e.Payload.GetProperty("fromSessionId").GetString()!)];

    /// <summary>The sessions that reported back to <paramref name="sessionId"/>, in order.</summary>
    private static List<string> Reported(LiveEvents events, string sessionId)
        => [.. events.For(sessionId).Where(e => e.Type == "session.reported").Select(e => e.Payload.GetProperty("fromSessionId").GetString()!)];

    /// <summary>
    /// A Fleet with OpenCode 2 set up as in <see cref="OpenCode2LiveFleet"/>, and a scratch HOME for OpenCode 1
    /// pointed at the same scripted model.
    /// </summary>
    private sealed class BothFleet : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-side-by-side-{Guid.NewGuid():N}");
        private bool _disposed;

        public OpenCode2LiveFleet Fleet { get; } = new();

        public string OpenCode1Home => Path.Combine(_root, "home");

        public static async Task<BothFleet> StartAsync()
        {
            var both = new BothFleet();
            await both.Fleet.InitializeAsync();
            return both;
        }

        /// <summary>
        /// An OpenCode 1 session in a folder of its own, started on the owner's pooled process with the scratch HOME
        /// and whatever the owner's settings add to its environment, and registered as the orchestrator registers one.
        /// </summary>
        public async Task<(string Id, IHarnessSession Harness)> StartOpenCode1SessionAsync(string id, string title, CancellationToken ct)
        {
            var folder = Fleet.NewFolder(id);
            var services = Fleet.Services;
            var plugin = Path.Combine(_root, "no-op.ts");
            Directory.CreateDirectory(_root);
            File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");
            var environment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(_root, Fleet.Llm.BaseUrl, plugin);

            var runtime = services.GetRequiredService<OpenCodeHarnessRuntime>();
            var prepared = await runtime.PrepareRuntimeAsync(
                new RuntimePreparationContext { UserId = OpenCode2LiveFleet.Owner, UserCredentials = [], WorkingDirectory = folder },
                ct);
            foreach (var (name, value) in ((OpenCodeLaunchArtifacts)prepared.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).EnvironmentVariables)
                environment[name] = value;

            Seed(services, id, title, folder);
            var harness = await runtime.SpawnAsync(
                new HarnessSpawnOptions
                {
                    SessionId = id,
                    WorkingDirectory = folder,
                    OwnerUserId = OpenCode2LiveFleet.Owner,
                    LaunchArtifacts = new OpenCodeLaunchArtifacts(environment),
                },
                ct);
            Register(services, id, harness);
            await harness.WaitForEventSubscriptionAsync(ct);
            return (id, harness);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            await Fleet.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private static void Seed(IServiceProvider services, string id, string title, string folder)
        {
            using var scope = services.CreateScope();
            using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
                VALUES ('ws-{id}', '{folder}', '{title}', '2026-09-23T00:00:00+00:00', '{OpenCode2LiveFleet.Owner}');
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('inst-{id}', 0, NULL, '{folder}', '', 'running', '2026-09-23T00:00:00+00:00', '{OpenCode2LiveFleet.Owner}');
                INSERT INTO sessions (
                    id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                    lifecycle_status, retention_status, created_at, user_id)
                VALUES ('{id}', 'ws-{id}', 'inst-{id}', 'pending', '{title}', 'active', '{folder}',
                        'running', 'active', '2026-09-23T00:00:00+00:00', '{OpenCode2LiveFleet.Owner}');
                """;
            command.ExecuteNonQuery();
        }

        /// <summary>Points the seeded session at its spawned instance and registers it, as the orchestrator does.</summary>
        private static void Register(IServiceProvider services, string id, IHarnessSession session)
        {
            using (var scope = services.CreateScope())
            using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            using (var command = connection.CreateCommand())
            {
                var token = session.ResumeToken is null ? "NULL" : $"'{session.ResumeToken}'";
                command.CommandText = $"""
                    INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                    VALUES ('{session.InstanceId}', 0, NULL, '', '', 'running', '2026-09-23T00:00:00+00:00', '{OpenCode2LiveFleet.Owner}');
                    UPDATE sessions SET instance_id = '{session.InstanceId}', harness_resume_token = {token} WHERE id = '{id}';
                    """;
                command.ExecuteNonQuery();
            }

            services.GetRequiredService<InstanceTracker>().Register(session.InstanceId, session);
        }
    }

    private static async Task PromptAsync(OpenCode2LiveFleet fleet, string sessionId, string text, CancellationToken ct)
    {
        var prompted = await fleet.WithOrchestratorAsync(o => o.PromptSessionAsync(sessionId, text, null, ct));
        prompted.IsSuccess.ShouldBeTrue(prompted.IsFailure ? prompted.Error.Description : null);
    }

    /// <summary>The texts of the prompts and replies the client gets when it opens the session again, in order.</summary>
    private static async Task<List<string>> HistoryAsync(OpenCode2LiveFleet fleet, string sessionId, CancellationToken ct)
    {
        using var scope = fleet.Services.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(OpenCode2LiveFleet.Owner);
        var snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(sessionId, ct: ct);
        snapshot.IsPartial.ShouldBeFalse();
        return [.. snapshot.Messages.SelectMany(m => m.Parts).OfType<TextMessageEventPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t))];
    }

    // Read from what the client gets, not from domain events: OpenCode 1's relay sends its parts on as OpenCode wrote
    // them, with no domain event, and OpenCode 2's are serialized from one. Both have the same shape on the wire.
    private static IEnumerable<JsonElement> Parts(LiveEvents events, string sessionId)
        => events.For(sessionId)
            .Where(e => e.Type == "message.part.updated" && e.Payload.TryGetProperty("part", out _))
            .Select(e => e.Payload.GetProperty("part"));

    /// <summary>Whether the tool call's card has shown it running.</summary>
    private static bool IsRunning(LiveEvents events, string sessionId, string callId)
        => Parts(events, sessionId).Any(p => p.GetProperty("type").GetString() == "tool"
            && p.GetProperty("callID").GetString() == callId
            && p.GetProperty("state").GetProperty("status").GetString() == "running");

    /// <summary>The session's text parts as the live stream last showed them.</summary>
    private static List<string> LatestTexts(LiveEvents events, string sessionId)
        => [.. Parts(events, sessionId).Where(p => p.GetProperty("type").GetString() == "text")
            .GroupBy(p => p.GetProperty("id").GetString()).Select(g => g.Last().GetProperty("text").GetString()!)];

    /// <summary>Every string in <paramref name="json"/>, however deep.</summary>
    private static IEnumerable<string> Strings(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => [json.GetString()!],
        JsonValueKind.Object => json.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(Strings),
        _ => [],
    };

    private static ScriptedLlmResponse Text(string text) => new() { Text = text };

    private static ScriptedLlmResponse ToolCall(string callId, string tool, object input) => new()
    {
        StopReason = "tool_calls",
        ToolCalls = [new ScriptedToolCall(callId, tool, JsonSerializer.Serialize(input))],
    };

    /// <summary>Polls until <paramref name="done"/>; on a timeout, says what Fleet sent and what the model was asked.</summary>
    private static async Task WaitForAsync(OpenCode2LiveFleet fleet, LiveEvents events, Func<bool> done, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (!done())
        {
            try
            {
                await Task.Delay(200, wait.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out. Fleet sent: {events.Describe()}\n" +
                    "The model was asked:\n" + string.Join("\n", fleet.Llm.Queue.Requests.Select(r =>
                        $"  tools={LlmRequest.OffersTools(r)} last={LlmRequest.LastRole(r)} user={Trim(LlmRequest.LastUserText(r))} tool={Trim(LlmRequest.LastToolText(r))}")));
            }
        }
    }

    private static string Trim(string? value) => value is null ? "-" : value[..Math.Min(value.Length, 120)];

    private static List<int> ProcessesWith(string name, string value)
    {
        var wanted = $"{name}={value}";
        var found = new List<int>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out var pid))
                continue;
            try
            {
                if (File.ReadAllText(Path.Combine(folder, "environ")).Split('\0').Contains(wanted))
                    found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or gone.
            }
        }

        return found;
    }

    private static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}

/// <summary>A test that needs both a real OpenCode 1 (<c>opencode</c> on PATH) and a real OpenCode 2; skipped or failed as each one's own attribute is.</summary>
internal sealed class BothOpenCodesFactAttribute : FactAttribute
{
    public BothOpenCodesFactAttribute()
    {
        Skip = new OpenCodeFactAttribute().Skip ?? new OpenCode2FactAttribute().Skip;
    }
}
