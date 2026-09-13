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
public sealed class EndpointGuardTests
{
    private const string _userId = "local-user";

    [Theory]
    [InlineData("stopped")]
    [InlineData("disconnected")]
    public async Task get_session_returns_manual_pi_terminal_capabilities(string lifecycleStatus)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = $"session-pi-{lifecycleStatus}";
        await InsertSessionAsync(
            factory,
            sessionId: sessionId,
            instanceId: $"instance-pi-{lifecycleStatus}",
            lifecycleStatus: lifecycleStatus,
            status: lifecycleStatus,
            runtimeMode: "manual",
            harnessType: "pi");

        var response = await client.GetAsync($"/api/sessions/{sessionId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        var capabilities = json.GetProperty("capabilities");
        capabilities.GetProperty("canPrompt").GetBoolean().ShouldBeTrue();
        capabilities.GetProperty("promptDisabledReason").ValueKind.ShouldBe(JsonValueKind.Null);
        capabilities.TryGetProperty("canResume", out _).ShouldBeFalse();
        capabilities.TryGetProperty("canStop", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task get_session_returns_its_project_name()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        const string sessionId = "session-in-project";
        using (var scope = factory.Services.CreateScope())
        {
            using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await connection.ExecuteAsync(
                "INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id) VALUES ('project-scratch', 'Scratch', NULL, 'scratch', 0, @Now, @Now, @UserId)",
                new { Now = DateTime.UtcNow.ToString("O"), UserId = _userId });
        }
        await InsertSessionAsync(
            factory,
            sessionId: sessionId,
            instanceId: "instance-in-project",
            lifecycleStatus: "stopped",
            status: "stopped",
            runtimeMode: "manual",
            harnessType: "pi",
            projectId: "project-scratch");

        var response = await client.GetAsync($"/api/sessions/{sessionId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("projectId").GetString().ShouldBe("project-scratch");
        json.GetProperty("projectName").GetString().ShouldBe("Scratch");
    }

    private static async Task InsertSessionAsync(
        ApiWebApplicationFactory factory,
        string sessionId,
        string instanceId,
        string lifecycleStatus,
        string status,
        string runtimeMode,
        string harnessType,
        string? projectId = null)
    {
        using var scope = factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();
        var createdAt = DateTime.UtcNow.ToString("O");
        var workspaceId = $"workspace-{sessionId}";
        var directory = $"/tmp/{sessionId}";

        await InsertWorkspaceAsync(connection, workspaceId, directory, createdAt);
        await InsertInstanceAsync(connection, instanceId, directory, createdAt);

        await connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, runtime_mode, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @RuntimeMode, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = sessionId,
                WorkspaceId = workspaceId,
                InstanceId = instanceId,
                ProjectId = projectId,
                OpencodeSessionId = $"opencode-{sessionId}",
                Title = sessionId,
                Status = status,
                Directory = directory,
                CreatedAt = createdAt,
                StoppedAt = createdAt,
                ParentSessionId = (string?)null,
                ActivityStatus = "idle",
                LifecycleStatus = lifecycleStatus,
                TotalTokens = 0,
                TotalCost = 0d,
                HarnessType = harnessType,
                RuntimeMode = runtimeMode,
                HarnessResumeToken = $"resume-{sessionId}",
                IsHidden = false,
                RetentionStatus = "active",
                ArchivedAt = (string?)null,
                UserId = _userId
            });
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
                Status = "stopped",
                CreatedAt = createdAt,
                StoppedAt = createdAt,
                UserId = _userId
            });
}
