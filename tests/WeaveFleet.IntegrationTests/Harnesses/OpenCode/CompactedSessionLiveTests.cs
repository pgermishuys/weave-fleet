extern alias FakeLlm;

using System.Collections.Concurrent;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// When a session's context fills up, OpenCode compacts it and marks the message it writes with <c>"summary": true</c>.
/// With a real <c>opencode</c> and a scripted model that reports more tokens than the model's context holds, the turn
/// ends in a compaction; opening the session afterwards must still build its snapshot. The pooled process runs with a
/// scratch HOME and scratch XDG dirs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompactedSessionLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "fleet-compacted-live";
    private const string Answer = "The login page lives in client/src/pages/login.vue.";
    private const string CompactionSummary = "Summary: the user asked where the login page is.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task A_session_OpenCode_compacted_still_opens()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-compacted-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var plugin = Path.Combine(root, "noop.ts");
        File.WriteAllText(plugin, "export const Noop = async () => ({})\n");
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, plugin, contextLimit: 10_000);
        ScriptModel(llm.Queue);

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            SeedSession(services, workspace);

            var events = new ConcurrentQueue<BroadcastEvent>();
            var collecting = CollectAsync(services.GetRequiredService<IEventBroadcaster>(), events, ct);

            var session = await services.GetRequiredService<OpenCodeHarnessRuntime>().SpawnAsync(
                new HarnessSpawnOptions
                {
                    SessionId = SessionId,
                    WorkingDirectory = workspace,
                    OwnerUserId = Owner,
                    LaunchArtifacts = new OpenCodeLaunchArtifacts(processEnvironment),
                },
                ct);
            await using var spawned = session;
            RegisterLikeTheOrchestrator(services, session);

            await session.SendPromptAsync("Where is the login page?", null, ct);

            // The answer reports a context past the model's limit, so OpenCode compacts before the turn ends.
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForAsync(() => llm.Queue.Requests.Any(IsCompactionRequest) && events.Any(e => e.Type == "session.idle"), wait.Token);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out waiting for the compaction. Fleet sent: {string.Join(", ", events.Select(e => e.Type))}\n" +
                    $"LLM requests ({llm.Queue.Requests.Count}):\n" +
                    string.Join("\n---\n", llm.Queue.Requests.Select(r => r.Length > 600 ? r[..600] : r)));
            }

            // Opening the session: the client subscribes and gets this snapshot.
            SessionSnapshot snapshot;
            using (var scope = services.CreateScope())
            using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            {
                snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(SessionId, ct: ct);
            }

            snapshot.IsPartial.ShouldBeFalse();
            var texts = snapshot.Messages.SelectMany(m => m.Parts).OfType<TextMessageEventPart>().Select(p => p.Text).ToList();
            texts.ShouldContain(Answer);
            texts.ShouldContain(CompactionSummary);

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void ScriptModel(ScriptedResponseStore queue)
    {
        // The title and the compaction's summary are both asked with tools off.
        queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Login page" };
        queue.ToolLessAnswer = body => IsCompactionRequest(body) ? new ScriptedLlmResponse { Text = CompactionSummary } : null;

        queue.Enqueue(new ScriptedLlmResponse { Text = Answer, InputTokens = 50_000 });

        // After compacting, OpenCode prompts the agent to carry on.
        queue.Fallback = _ => new ScriptedLlmResponse { Text = "Carrying on." };
    }

    /// <summary>OpenCode's compaction runs a summarization agent over the conversation so far.</summary>
    private static bool IsCompactionRequest(string body)
        => body.Contains("You are a context summarization agent", StringComparison.Ordinal);

    private static void SeedSession(IServiceProvider services, string workspace)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-compacted', '{workspace}', 'Live', '2026-09-25T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-seed', 0, NULL, '{workspace}', '', 'running', '2026-09-25T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, harness_type, created_at, user_id)
            VALUES ('{SessionId}', 'ws-compacted', 'inst-seed', 'pending', 'Live', 'active', '{workspace}',
                    'running', 'active', 'opencode', '2026-09-25T00:00:00+00:00', '{Owner}');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>What <c>SessionOrchestrator</c> does after a spawn, so the snapshot reads the live harness.</summary>
    private static void RegisterLikeTheOrchestrator(IServiceProvider services, IHarnessSession session)
    {
        using (var scope = services.CreateScope())
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        using (var command = connection.CreateCommand())
        {
            var token = session.ResumeToken is null ? "NULL" : $"'{session.ResumeToken}'";
            command.CommandText = $"""
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('{session.InstanceId}', 0, NULL, '', '', 'running', '2026-09-25T00:00:00+00:00', '{Owner}');
                UPDATE sessions SET instance_id = '{session.InstanceId}', harness_resume_token = {token} WHERE id = '{SessionId}';
                """;
            command.ExecuteNonQuery();
        }

        services.GetRequiredService<InstanceTracker>().Register(session.InstanceId, session);
    }

    private static async Task CollectAsync(IEventBroadcaster broadcaster, ConcurrentQueue<BroadcastEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var e in broadcaster.SubscribeAsync([$"session:{SessionId}"], Owner, ct))
                events.Enqueue(e);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> done, CancellationToken ct)
    {
        while (!done())
        {
            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }
    }
}
