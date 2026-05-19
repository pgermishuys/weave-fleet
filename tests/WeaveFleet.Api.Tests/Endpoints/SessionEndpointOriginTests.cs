using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

[Collection("NonParallelApiFactoryTests")]
public sealed class SessionEndpointOriginTests : IAsyncLifetime, IDisposable
{
    private const string _userId = "local-user";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false);
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        var createdAt = "2026-01-01T00:00:00.0000000Z";

        await InsertWorkspaceAsync(connection, "workspace-with-origin", "/tmp/workspace-with-origin", createdAt);
        await InsertWorkspaceAsync(connection, "workspace-without-origin", "/tmp/workspace-without-origin", createdAt);

        await InsertInstanceAsync(connection, "instance-with-origin", "/tmp/workspace-with-origin", createdAt);
        await InsertInstanceAsync(connection, "instance-without-origin", "/tmp/workspace-without-origin", createdAt);

        await InsertSessionAsync(connection, "session-with-origin", "workspace-with-origin", "instance-with-origin", "/tmp/workspace-with-origin", createdAt);
        await InsertSessionAsync(connection, "session-without-origin", "workspace-without-origin", "instance-without-origin", "/tmp/workspace-without-origin", createdAt);

        await connection.ExecuteAsync(
            "INSERT INTO session_source_usages (id, session_id, workspace_id, provider_id, source_type, action_id, resource_id, resource_url, title, summary, created_at) VALUES (@Id, @SessionId, @WorkspaceId, @ProviderId, @SourceType, @ActionId, @ResourceId, @ResourceUrl, @Title, @Summary, @CreatedAt)",
            new
            {
                Id = "usage-start-session",
                SessionId = "session-with-origin",
                WorkspaceId = "workspace-with-origin",
                ProviderId = "github",
                SourceType = "repository",
                ActionId = "start-session",
                ResourceId = "repo-1",
                ResourceUrl = "https://github.com/acme/repo",
                Title = "acme/repo",
                Summary = "Primary origin",
                CreatedAt = createdAt
            });

        await connection.ExecuteAsync(
            "INSERT INTO session_source_usages (id, session_id, workspace_id, provider_id, source_type, action_id, resource_id, resource_url, title, summary, created_at) VALUES (@Id, @SessionId, @WorkspaceId, @ProviderId, @SourceType, @ActionId, @ResourceId, @ResourceUrl, @Title, @Summary, @CreatedAt)",
            new
            {
                Id = "usage-non-start-session",
                SessionId = "session-without-origin",
                WorkspaceId = "workspace-without-origin",
                ProviderId = "local",
                SourceType = "directory",
                ActionId = "add-to-session",
                ResourceId = "/tmp/workspace-without-origin",
                ResourceUrl = "file:///tmp/workspace-without-origin",
                Title = "workspace-without-origin",
                Summary = "Not a primary origin",
                CreatedAt = createdAt
            });
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
    public async Task ListSessions_WhenBatchContainsMixedOrigins_MapsOriginOnlyForSessionsWithStartSessionUsage()
    {
        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();

        var sessionsById = sessions.ToDictionary(
            session => session.GetProperty("session").GetProperty("id").GetString().ShouldNotBeNull(),
            session => session);

        var sessionWithOrigin = sessionsById["session-with-origin"];
        var origin = sessionWithOrigin.GetProperty("origin");
        origin.GetProperty("sourceType").GetString().ShouldBe("repository");
        origin.GetProperty("title").GetString().ShouldBe("acme/repo");
        origin.GetProperty("resourceUrl").GetString().ShouldBe("https://github.com/acme/repo");
        origin.GetProperty("resourceId").GetString().ShouldBe("repo-1");
        origin.GetProperty("providerId").GetString().ShouldBe("github");

        var sessionWithoutOrigin = sessionsById["session-without-origin"];
        sessionWithoutOrigin.GetProperty("origin").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetSession_WhenNoStartSessionUsageExists_ReturnsNullOrigin()
    {
        var response = await _client!.GetAsync("/api/sessions/session-without-origin");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);

        session.GetProperty("id").GetString().ShouldBe("session-without-origin");
        session.GetProperty("origin").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static Task<int> InsertWorkspaceAsync(IDbConnection connection, string id, string directory, string createdAt) =>
        connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = id,
                Directory = directory,
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = id,
                UserId = _userId
            });

    private static Task<int> InsertInstanceAsync(IDbConnection connection, string id, string directory, string createdAt) =>
        connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = id,
                Port = 0,
                Pid = (int?)null,
                Directory = directory,
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = _userId
            });

    private static Task<int> InsertSessionAsync(IDbConnection connection, string id, string workspaceId, string instanceId, string directory, string createdAt) =>
        connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = id,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                ProjectId = (string?)null,
                OpencodeSessionId = $"opencode-{id}",
                Title = id,
                Status = "active",
                Directory = directory,
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = (string?)null,
                ActivityStatus = "idle",
                LifecycleStatus = "running",
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = "opencode",
                HarnessResumeToken = (string?)null,
                IsHidden = false,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                UserId = _userId
            });
}
