using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// <c>onComplete</c> on <c>POST /api/sessions</c>, through the real relay: when the session finishes its turn, the
/// session that asked is prompted, even when the instance it was registered with is gone (issue #364).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionCallbackTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new SignalRTestServer();
        await _server.StartAsync();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    // The server is disposed in DisposeAsync.
    public void Dispose()
    {
    }

    [Fact]
    public async Task The_coordinator_hears_when_its_session_finishes_even_after_its_instance_went_away()
    {
        _server.TestHarnessRuntime.Configure(scenario => scenario.WithSimpleTextResponse("_placeholder_", "msg-worker", "Tests written."));

        var (coordinatorId, liveInstanceId) = await CreateSessionAsync(new { title = "Coordinator" });

        // A Fleet restart: the instance the coordinator ran on is gone, under an id Fleet won't use again, and the
        // session can be resumed. (The test harness names an instance after its session, so the old one is renamed.)
        var coordinatorInstanceId = $"inst-before-restart-{Guid.NewGuid():N}";
        using (var scope = _server.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<InstanceTracker>().Remove(liveInstanceId);
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                PRAGMA foreign_keys = OFF;
                UPDATE instances SET id = '{coordinatorInstanceId}', status = 'stopped' WHERE id = '{liveInstanceId}';
                UPDATE sessions SET instance_id = '{coordinatorInstanceId}' WHERE id = '{coordinatorId}';
                PRAGMA foreign_keys = ON;
                """;
            cmd.ExecuteNonQuery();
            await scope.ServiceProvider.GetRequiredService<ISessionRepository>().UpdateResumeTokenAsync(coordinatorId, "resume-coordinator");
        }

        var (workerId, _) = await CreateSessionAsync(new
        {
            title = "Worker",
            initialPrompt = "Write the tests",
            onComplete = new { notifySessionId = coordinatorId, notifyInstanceId = coordinatorInstanceId },
        });

        var expected = $"Session 'Worker' ({workerId}) completed.";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string[] prompts = [];
        while (DateTime.UtcNow < deadline)
        {
            prompts = await CoordinatorPromptsAsync(coordinatorId);
            if (prompts.Contains(expected))
                break;
            await Task.Delay(100);
        }

        prompts.ShouldContain(expected);

        // Delivered to the session's new instance, and only then marked fired. The mark is written after the prompt
        // returns, so the coordinator can show the prompt a moment before the callback stops being "started".
        using var check = _server.Services.CreateScope();
        var coordinator = await check.ServiceProvider.GetRequiredService<ISessionRepository>().GetByIdAsync(coordinatorId);
        coordinator.ShouldNotBeNull().InstanceId.ShouldNotBe(coordinatorInstanceId);
        var callbacks = check.ServiceProvider.GetRequiredService<ISessionCallbackRepository>();
        var started = await callbacks.GetStartedAsync();
        while (started.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            started = await callbacks.GetStartedAsync();
        }

        started.ShouldBeEmpty();
    }

    /// <summary>What the coordinator's harness was prompted with, read from its live instance once it has one.</summary>
    private async Task<string[]> CoordinatorPromptsAsync(string coordinatorId)
    {
        using var scope = _server.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>().GetByIdAsync(coordinatorId);
        var instance = session is null ? null : scope.ServiceProvider.GetRequiredService<InstanceTracker>().Get(session.InstanceId);
        if (instance is null)
            return [];

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);
        return [.. page.Messages.Where(m => m.Role == "user").Select(m => m.TextContent)];
    }

    private async Task<(string SessionId, string InstanceId)> CreateSessionAsync(object fields)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["directory"] = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        body["harnessType"] = "opencode";

        var response = await http.PostAsync("/api/sessions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue(text);

        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("session").GetProperty("id").GetString()!, doc.RootElement.GetProperty("instanceId").GetString()!);
    }
}
