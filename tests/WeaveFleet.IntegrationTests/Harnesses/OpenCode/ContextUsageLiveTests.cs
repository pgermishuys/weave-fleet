extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// How full the context is, from a real <c>opencode</c> and a scripted model: a turn's call reaches Fleet's record with
/// the model's window from OpenCode's provider list, and Compact now compacts. The pooled process runs with a scratch
/// HOME and scratch XDG dirs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContextUsageLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "fleet-context-live";
    private const string CompactionSummary = "Summary: the user asked where the login page is.";
    private const int ContextLimit = 200_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task A_turn_fills_the_context_and_compact_now_empties_it()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-context-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var plugin = Path.Combine(root, "noop.ts");
        File.WriteAllText(plugin, "export const Noop = async () => ({})\n");
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, plugin, contextLimit: ContextLimit);
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Login page" };
        llm.Queue.ToolLessAnswer = body => IsCompactionRequest(body) ? new ScriptedLlmResponse { Text = CompactionSummary } : null;
        llm.Queue.Enqueue(new ScriptedLlmResponse { Text = "It's in client/src/pages/login.vue.", InputTokens = 50_000, OutputTokens = 10 });
        llm.Queue.Fallback = _ => new ScriptedLlmResponse { Text = "Carrying on." };

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            SeedSession(services, workspace);

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

            // The call's tokens, each kind once, against the window the user's config gives the model.
            var filled = await WaitForContextAsync(services, c => c.Turns.Count == 1, "the turn's size", ct);
            filled.Used.ShouldBe(50_010);
            filled.Limit.ShouldBe(ContextLimit);
            filled.CompactsAt.ShouldBe(ContextLimit - 1_000);
            filled.ProviderId.ShouldBe("fake");
            filled.ModelId.ShouldBe("fake-model");
            filled.LastCall.ShouldNotBeNull().Input.ShouldBe(50_000);
            filled.Turns[0].Used.ShouldBe(50_010);

            // Compact now: OpenCode summarises with the model of the last call, and says when it's done.
            await session.CompactAsync(new CompactOptions(), ct);
            var compacted = await WaitForContextAsync(services, c => c.CompactedAt is not null, "the compaction to end", ct);
            compacted.Used.ShouldBeNull();
            compacted.Compacting.ShouldBeFalse();
            compacted.Limit.ShouldBe(ContextLimit);
            llm.Queue.Requests.ShouldContain(r => IsCompactionRequest(r));
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<SessionContext> WaitForContextAsync(IServiceProvider services, Func<SessionContext, bool> done, string what, CancellationToken ct)
    {
        SessionContext? context = null;
        while (true)
        {
            using (var scope = services.CreateScope())
                context = await scope.ServiceProvider.GetRequiredService<ISessionContextRepository>().GetForOwnerAsync(SessionId, Owner, ct);
            if (context is not null && done(context))
                return context;

            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for {what}. Fleet has: {context}");
            }
        }
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
            VALUES ('ws-context', '{workspace}', 'Live', '2026-09-25T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-seed', 0, NULL, '{workspace}', '', 'running', '2026-09-25T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, harness_type, created_at, user_id)
            VALUES ('{SessionId}', 'ws-context', 'inst-seed', 'pending', 'Live', 'active', '{workspace}',
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
}
