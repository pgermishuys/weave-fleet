using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

[Collection("NonParallelApiFactoryTests")]
public sealed class SessionProgressEndpointTests : IAsyncLifetime, IDisposable
{
    private const string LocalUser = "local-user";
    private const string OtherUser = "other-user";
    private const string CreatedAt = "2026-09-13T10:00:00.0000000Z";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false);
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            await InsertSessionGraphAsync(connection, "session-with-progress", LocalUser);
            await InsertSessionGraphAsync(connection, "session-without-progress", LocalUser);
            await InsertSessionGraphAsync(connection, "their-session", OtherUser);
        }

        var repository = scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>();
        (await repository.UpsertAsync(Progress("session-with-progress", LocalUser), CancellationToken.None)).ShouldBeTrue();
        (await repository.UpsertAsync(Progress("their-session", OtherUser), CancellationToken.None)).ShouldBeTrue();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task ListSessions_IncludesProgress_ForSessionsThatHaveIt()
    {
        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = (await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web)).ShouldNotBeNull();
        var byId = sessions.ToDictionary(session => session.GetProperty("session").GetProperty("id").GetString()!);

        byId.Keys.ShouldNotContain("their-session");
        byId["session-with-progress"].GetProperty("progress").GetRawText().ShouldBe(
            """{"sessionId":"session-with-progress","kind":"todos","done":1,"total":2,"current":"Drop the indexes"}""");
        byId["session-without-progress"].GetProperty("progress").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetProgress_ReturnsTheSessionsTodos()
    {
        var response = await _client!.GetAsync("/api/sessions/session-with-progress/progress");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var progress = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        progress.GetRawText().ShouldBe(
            """{"sessionId":"session-with-progress","kind":"todos","done":1,"total":2,"current":"Drop the indexes","todos":[{"content":"Write the migration","status":"completed","priority":"high"},{"content":"Drop the indexes","status":"in_progress","priority":null}],"updatedAt":"2026-09-13T12:24:00.0000000Z"}""");
    }

    [Fact]
    public async Task GetProgress_ReturnsNoContent_WhenThereIsNone()
    {
        var response = await _client!.GetAsync("/api/sessions/session-without-progress/progress");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetProgress_ReturnsNotFound_ForSomeoneElsesSession()
    {
        var response = await _client!.GetAsync("/api/sessions/their-session/progress");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProgress_ReturnsNotFound_ForAnUnknownSession()
    {
        var response = await _client!.GetAsync("/api/sessions/no-such-session/progress");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static SessionProgress Progress(string sessionId, string userId) => new()
    {
        SessionId = sessionId,
        UserId = userId,
        Kind = SessionProgressKinds.Todos,
        Done = 1,
        Total = 2,
        Current = "Drop the indexes",
        Todos =
        [
            new TodoEntry { Content = "Write the migration", Status = TodoStatuses.Completed, Priority = "high" },
            new TodoEntry { Content = "Drop the indexes", Status = TodoStatuses.InProgress },
        ],
        UpdatedAt = new DateTimeOffset(2026, 9, 13, 12, 24, 0, TimeSpan.Zero),
    };

    private static async Task InsertSessionGraphAsync(IDbConnection connection, string sessionId, string userId)
    {
        var directory = $"/tmp/{sessionId}";
        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, isolation_strategy, created_at, display_name, user_id) VALUES (@Id, @Directory, 'existing', @CreatedAt, @Id, @UserId)",
            new { Id = $"workspace-{sessionId}", Directory = directory, CreatedAt, UserId = userId });
        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, directory, url, status, created_at, user_id) VALUES (@Id, 0, @Directory, '', 'running', @CreatedAt, @UserId)",
            new { Id = $"instance-{sessionId}", Directory = directory, CreatedAt, UserId = userId });
        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title, status, directory, created_at, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, is_hidden, retention_status, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @OpencodeSessionId, @Id, 'active', @Directory, @CreatedAt, 'idle', 'running', 0, 0, 'opencode', 0, 'active', @UserId)",
            new
            {
                Id = sessionId,
                WorkspaceId = $"workspace-{sessionId}",
                InstanceId = $"instance-{sessionId}",
                OpencodeSessionId = $"opencode-{sessionId}",
                Directory = directory,
                CreatedAt,
                UserId = userId,
            });
    }
}
