using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>How full a session's context is (<c>GET /context</c>), and Compact now (<c>POST /compact</c>).</summary>
[Collection("NonParallelApiFactoryTests")]
public sealed class SessionContextEndpointTests : IAsyncLifetime, IDisposable
{
    private const string LocalUser = "local-user";
    private const string OtherUser = "other-user";
    private const string CreatedAt = "2026-10-06T10:00:00.0000000Z";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false);
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            await InsertSessionGraphAsync(connection, "session-with-context", LocalUser);
            await InsertSessionGraphAsync(connection, "session-without-context", LocalUser);
            await InsertSessionGraphAsync(connection, "their-session", OtherUser);
        }

        var repository = scope.ServiceProvider.GetRequiredService<ISessionContextRepository>();
        (await repository.UpsertAsync(Context("session-with-context", LocalUser), CancellationToken.None)).ShouldBeTrue();
        (await repository.UpsertAsync(Context("their-session", OtherUser), CancellationToken.None)).ShouldBeTrue();
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
    public async Task GetContext_ReturnsHowFullTheContextIs()
    {
        var response = await _client!.GetAsync("/api/sessions/session-with-context/context");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        context.GetRawText().ShouldBe(
            """{"sessionId":"session-with-context","used":76000,"limit":200000,"compactsAt":167000,"modelId":"claude-opus-5","providerId":"anthropic","lastCall":{"input":1000,"cacheRead":74500,"cacheWrite":0,"output":500,"reasoning":0,"used":76000},"lastCallAt":"2026-10-06T10:00:00+00:00","compacting":false,"compactedAt":null,"compactionError":null,"turns":[{"used":76000,"limit":200000,"at":"2026-10-06T10:00:00+00:00","afterCompaction":false}],"updatedAt":"2026-10-06T10:00:00+00:00"}""");
    }

    [Fact]
    public async Task GetContext_ReturnsNoContent_WhenTheHarnessHasntSaid()
    {
        var response = await _client!.GetAsync("/api/sessions/session-without-context/context");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("their-session")]
    [InlineData("no-such-session")]
    public async Task GetContext_ReturnsNotFound_ForSomeoneElsesOrAnUnknownSession(string sessionId)
    {
        var response = await _client!.GetAsync($"/api/sessions/{sessionId}/context");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Compact_ReturnsNotFound_ForSomeoneElsesSession()
    {
        var response = await _client!.PostAsync("/api/sessions/their-session/compact", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static SessionContext Context(string sessionId, string userId)
    {
        var at = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        return new SessionContext
        {
            SessionId = sessionId,
            UserId = userId,
            Used = 76_000,
            Limit = 200_000,
            CompactsAt = 167_000,
            ModelId = "claude-opus-5",
            ProviderId = "anthropic",
            LastCall = new ContextCall { Input = 1_000, CacheRead = 74_500, Output = 500 },
            LastCallAt = at,
            Turns = [new SessionContextTurn(76_000, 200_000, at, AfterCompaction: false)],
            UpdatedAt = at,
        };
    }

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
