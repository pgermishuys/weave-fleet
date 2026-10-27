using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionListEndpointAggregationTests : IClassFixture<SessionListEndpointAggregationTests.Server>, IAsyncLifetime, IDisposable
{
    private const string _userId = "local-user";

    // The server is shared, so each test seeds sessions of its own and looks only at those.
    private readonly string _sfx = Guid.NewGuid().ToString("N");
    private readonly ApiWebApplicationFactory _factory;
    private readonly string _parentId;
    private readonly string _childId;
    private readonly string _standaloneId;
    private HttpClient? _client;

    public SessionListEndpointAggregationTests(Server server)
    {
        _factory = server.Factory;
        _parentId = $"session-parent-{_sfx}";
        _childId = $"session-child-{_sfx}";
        _standaloneId = $"session-standalone-{_sfx}";
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        var createdAt = DateTime.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name, user_id) VALUES (@Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName, @UserId)",
            new
            {
                Id = $"workspace-parent-{_sfx}",
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
                Id = $"instance-parent-{_sfx}",
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
                Id = $"workspace-standalone-{_sfx}",
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
                Id = $"instance-standalone-{_sfx}",
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
                Id = $"instance-child-{_sfx}",
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
                Id = _parentId,
                WorkspaceId = $"workspace-parent-{_sfx}",
                InstanceId = $"instance-parent-{_sfx}",
                ProjectId = (string?)null,
                OpencodeSessionId = $"opencode-parent-{_sfx}",
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
                Id = _childId,
                WorkspaceId = $"workspace-parent-{_sfx}",
                InstanceId = $"instance-child-{_sfx}",
                ProjectId = (string?)null,
                OpencodeSessionId = $"opencode-child-{_sfx}",
                Title = "Child Session",
                Status = "active",
                Directory = "/tmp/workspace-parent",
                CreatedAt = createdAt,
                StoppedAt = (string?)null,
                ParentSessionId = _parentId,
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
                Id = _standaloneId,
                WorkspaceId = $"workspace-standalone-{_sfx}",
                InstanceId = $"instance-standalone-{_sfx}",
                ProjectId = (string?)null,
                OpencodeSessionId = $"opencode-standalone-{_sfx}",
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
    }

    private JsonElement[] OwnSessions(JsonElement[] sessions) => sessions
        .Where(s => s.GetProperty("session").GetProperty("id").GetString() is { } id && (id == _parentId || id == _childId || id == _standaloneId))
        .ToArray();

    [Fact]
    public async Task ListSessions_WhenParentHasBusyChild_ReturnsActiveSessionStatus()
    {
        // The child session has ActivityStatus = "busy" in the DB, but the activity tracker
        // is not aware of it (no ephemeral state). Therefore, the parent should NOT show as "active".
        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        sessions = OwnSessions(sessions);
        sessions.Length.ShouldBe(2);

        var sessionsById = sessions.ToDictionary(
            session => session.GetProperty("session").GetProperty("id").GetString().ShouldNotBeNull(),
            session => session);

        sessionsById.Count.ShouldBe(2);

        var parentSession = sessionsById[_parentId];
        parentSession.GetProperty("session").GetProperty("id").GetString().ShouldBe(_parentId);
        parentSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        // Fixed: parent should be idle when child is not busy in the activity tracker
        parentSession.GetProperty("sessionStatus").GetString().ShouldBe("idle");

        var standaloneSession = sessionsById[_standaloneId];
        standaloneSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        standaloneSession.GetProperty("sessionStatus").GetString().ShouldBe("idle");
    }

    [Fact]
    public async Task A_retrying_session_says_which_attempt_why_and_when_in_the_list_and_on_its_own()
    {
        // Claude Code's api_retry, as the relay records it: a page opened mid-retry reads it from here.
        using var scope = _factory.Services.CreateScope();
        var activityTracker = scope.ServiceProvider.GetRequiredService<Application.Sessions.SessionActivityTracker>();
        var next = DateTimeOffset.UtcNow.AddSeconds(12);
        activityTracker.Update(_standaloneId, "retry", _userId, retryAttempt: 3, retryMessage: "API overloaded (529)", retryNext: next, retryMax: 10);

        var sessions = await _client!.GetFromJsonAsync<JsonElement[]>("/api/sessions", JsonSerializerOptions.Web);
        var single = await _client!.GetFromJsonAsync<JsonElement>($"/api/sessions/{_standaloneId}", JsonSerializerOptions.Web);

        var listed = sessions.ShouldNotBeNull().Single(s => s.GetProperty("session").GetProperty("id").GetString() == _standaloneId);
        foreach (var session in new[] { listed, single })
        {
            session.GetProperty("activityStatus").GetString().ShouldBe("retry");
            session.GetProperty("retryAttempt").GetInt32().ShouldBe(3);
            session.GetProperty("retryMaxAttempts").GetInt32().ShouldBe(10);
            session.GetProperty("retryMessage").GetString().ShouldBe("API overloaded (529)");
            session.GetProperty("retryNext").GetDateTimeOffset().ShouldBe(next);
        }

        // A session that isn't retrying says nothing about it.
        var parent = sessions.Single(s => s.GetProperty("session").GetProperty("id").GetString() == _parentId);
        var saysAttempt = parent.TryGetProperty("retryAttempt", out var attempt) && attempt.ValueKind != JsonValueKind.Null;
        saysAttempt.ShouldBeFalse();
    }

    [Fact]
    public async Task ListSessions_WhenParentHasTrackerBusyChild_ReturnsActiveSessionStatus()
    {
        // Register the child as busy in the activity tracker
        using var scope = _factory.Services.CreateScope();
        var activityTracker = scope.ServiceProvider.GetRequiredService<Application.Sessions.SessionActivityTracker>();
        activityTracker.Update(_childId, "busy", _userId);

        var response = await _client!.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        sessions = OwnSessions(sessions);
        sessions.Length.ShouldBe(2);

        var sessionsById = sessions.ToDictionary(
            session => session.GetProperty("session").GetProperty("id").GetString().ShouldNotBeNull(),
            session => session);

        sessionsById.Count.ShouldBe(2);

        var parentSession = sessionsById[_parentId];
        parentSession.GetProperty("session").GetProperty("id").GetString().ShouldBe(_parentId);
        parentSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        // Parent should show as active when child is busy in the activity tracker
        parentSession.GetProperty("sessionStatus").GetString().ShouldBe("active");

        var standaloneSession = sessionsById[_standaloneId];
        standaloneSession.GetProperty("activityStatus").GetString().ShouldBe("idle");
        standaloneSession.GetProperty("sessionStatus").GetString().ShouldBe("idle");
    }

    [Theory]
    [InlineData("running", "active")]
    [InlineData("completed", "idle")]
    [InlineData("error", "idle")]
    [InlineData("cancelled", "idle")]
    public async Task ListSessions_WhenTrackerBusyChildHasDelegation_FollowsDelegationStatus(string delegationStatus, string expectedParentStatus)
    {
        // A child can stay "busy" in the tracker after its sub agent finished, when its idle
        // event never reached it. Once the delegation is over, that must not pin the parent.
        using var scope = _factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using (var connection = connectionFactory.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO delegations (id, parent_session_id, child_session_id, parent_tool_call_id, title, status) VALUES (@Id, @ParentSessionId, @ChildSessionId, @ParentToolCallId, @Title, @Status)",
                new
                {
                    Id = $"delegation-1-{_sfx}",
                    ParentSessionId = _parentId,
                    ChildSessionId = _childId,
                    ParentToolCallId = "tool-call-1",
                    Title = "spindle",
                    Status = delegationStatus
                });
        }

        var activityTracker = scope.ServiceProvider.GetRequiredService<Application.Sessions.SessionActivityTracker>();
        activityTracker.Update(_childId, "busy", _userId);

        var sessions = await _client!.GetFromJsonAsync<JsonElement[]>("/api/sessions", JsonSerializerOptions.Web);

        sessions.ShouldNotBeNull();
        var parentSession = sessions.Single(s => s.GetProperty("session").GetProperty("id").GetString() == _parentId);
        parentSession.GetProperty("sessionStatus").GetString().ShouldBe(expectedParentStatus);
    }

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false);
        public void Dispose() => Factory.Dispose();
    }
}
