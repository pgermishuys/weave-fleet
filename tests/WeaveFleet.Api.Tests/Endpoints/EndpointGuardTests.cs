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

    [Fact]
    public async Task resume_returns_conflict_when_automatic_session_resume_is_not_allowed()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        await InsertSessionAsync(
            factory,
            sessionId: "session-auto-resume-guard",
            instanceId: "instance-auto-resume-guard",
            lifecycleStatus: "stopped",
            status: "stopped",
            runtimeMode: "automatic");

        var response = await client.PostAsync("/api/sessions/session-auto-resume-guard/resume", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("error").GetString().ShouldBe("Automatic sessions resume on the next prompt.");
    }

    [Fact]
    public async Task stop_returns_conflict_when_session_is_not_running()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        await InsertSessionAsync(
            factory,
            sessionId: "session-stop-guard",
            instanceId: "instance-stop-guard",
            lifecycleStatus: "stopped",
            status: "stopped",
            runtimeMode: "manual");

        var response = await client.PostAsync("/api/sessions/session-stop-guard/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("error").GetString().ShouldBe("Session is not running.");
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("disconnected")]
    public async Task get_session_returns_manual_nucode_terminal_capabilities(string lifecycleStatus)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        var sessionId = $"session-nucode-{lifecycleStatus}";
        await InsertSessionAsync(
            factory,
            sessionId: sessionId,
            instanceId: $"instance-nucode-{lifecycleStatus}",
            lifecycleStatus: lifecycleStatus,
            status: lifecycleStatus,
            runtimeMode: "manual",
            harnessType: "nucode");

        var response = await client.GetAsync($"/api/sessions/{sessionId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        var capabilities = json.GetProperty("capabilities");
        capabilities.GetProperty("canResume").GetBoolean().ShouldBeTrue();
        capabilities.GetProperty("canPrompt").GetBoolean().ShouldBeFalse();
        capabilities.GetProperty("resumeDisabledReason").ValueKind.ShouldBe(JsonValueKind.Null);
        capabilities.GetProperty("promptDisabledReason").GetString().ShouldBe("Resume the session before prompting.");
    }

    private static async Task InsertSessionAsync(
        ApiWebApplicationFactory factory,
        string sessionId,
        string instanceId,
        string lifecycleStatus,
        string status,
        string runtimeMode)
    {
        await InsertSessionAsync(
            factory,
            sessionId,
            instanceId,
            lifecycleStatus,
            status,
            runtimeMode,
            harnessType: "opencode");
    }

    private static async Task InsertSessionAsync(
        ApiWebApplicationFactory factory,
        string sessionId,
        string instanceId,
        string lifecycleStatus,
        string status,
        string runtimeMode,
        string harnessType)
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
                ProjectId = (string?)null,
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
