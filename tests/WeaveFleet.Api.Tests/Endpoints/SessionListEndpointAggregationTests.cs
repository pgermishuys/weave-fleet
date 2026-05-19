using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

[CollectionDefinition("NonParallelApiFactoryTests", DisableParallelization = true)]
public sealed class NonParallelApiFactoryTestGroup;

[Collection("NonParallelApiFactoryTests")]
public sealed class SessionListEndpointAggregationTests : IAsyncLifetime, IDisposable
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

        var createdAt = DateTime.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = "workspace-parent",
                Directory = "/tmp/workspace-parent",
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = "Parent Workspace",
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = "instance-parent",
                Port = 0,
                Pid = (int?)null,
                Directory = "/tmp/workspace-parent",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = "workspace-standalone",
                Directory = "/tmp/workspace-standalone",
                SourceDirectory = (string?)null,
                IsolationStrategy = "existing",
                Branch = (string?)null,
                CreatedAt = createdAt,
                CleanedUpAt = (string?)null,
                DisplayName = "Standalone Workspace",
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = "instance-standalone",
                Port = 0,
                Pid = (int?)null,
                Directory = "/tmp/workspace-standalone",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id) VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)",
            new
            {
                Id = "instance-child",
                Port = 0,
                Pid = (int?)null,
                Directory = "/tmp/workspace-parent",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = "session-parent",
                WorkspaceId = "workspace-parent",
                InstanceId = "instance-parent",
                ProjectId = (string?)null,
                OpencodeSessionId = "opencode-parent",
                Title = "Parent Session",
                Status = "active",
                Directory = "/tmp/workspace-parent",
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

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = "session-child",
                WorkspaceId = "workspace-parent",
                InstanceId = "instance-child",
                ProjectId = (string?)null,
                OpencodeSessionId = "opencode-child",
                Title = "Child Session",
                Status = "active",
                Directory = "/tmp/workspace-parent",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = "session-parent",
                ActivityStatus = "busy",
                LifecycleStatus = "running",
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = "opencode",
                HarnessResumeToken = (string?)null,
                IsHidden = true,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                UserId = _userId
            });

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = "session-standalone",
                WorkspaceId = "workspace-standalone",
                InstanceId = "instance-standalone",
                ProjectId = (string?)null,
                OpencodeSessionId = "opencode-standalone",
                Title = "Standalone Session",
                Status = "active",
                Directory = "/tmp/workspace-standalone",
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
    public async Task ListSessions_WhenParentHasBusyChild_ReturnsActiveSessionStatus()
    {
        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        sessions.Length.ShouldBe(2);

        var sessionsById = sessions.ToDictionary(
            session => session.GetProperty("session").GetProperty("id").GetString().ShouldNotBeNull(),
            session => session);

        sessionsById.Count.ShouldBe(2);

        var parentSession = sessionsById["session-parent"];
        parentSession.GetProperty("session").GetProperty("id").GetString().ShouldBe("session-parent");
        parentSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        parentSession.GetProperty("sessionStatus").GetString().ShouldBe("active");

        var standaloneSession = sessionsById["session-standalone"];
        standaloneSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        standaloneSession.GetProperty("sessionStatus").GetString().ShouldBe("idle");
    }
}
