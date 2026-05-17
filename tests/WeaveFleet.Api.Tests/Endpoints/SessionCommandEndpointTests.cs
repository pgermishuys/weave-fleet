using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Tests.Endpoints;

[Collection("NonParallelApiFactoryTests")]
public sealed class SessionCommandEndpointTests : IAsyncLifetime, IDisposable
{
    private const string UserId = "local-user";
    private const string SessionId = "session-cmd-1";
    private const string InstanceId = "instance-cmd-1";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;
    private SlowFakeHarnessSession? _slowSession;

    public async Task InitializeAsync()
    {
        _slowSession = new SlowFakeHarnessSession(InstanceId);

        _factory = new ApiWebApplicationFactory(authEnabled: false);
        _client = _factory.CreateClient();

        // Register the slow session in the InstanceTracker
        var tracker = _factory.Services.GetRequiredService<InstanceTracker>();
        tracker.Register(InstanceId, _slowSession);

        // Seed DB with instance and session
        using var scope = _factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        var createdAt = "2026-01-01T00:00:00.0000000Z";
        await InsertWorkspaceAsync(connection, $"ws-{SessionId}", "/tmp/cmd-test", createdAt);
        await InsertInstanceAsync(connection, InstanceId, "/tmp/cmd-test", createdAt);
        await InsertSessionAsync(connection, SessionId, InstanceId, "/tmp/cmd-test", createdAt);
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
    public async Task Command_returns_202_immediately_without_awaiting_orchestrator()
    {
        var sw = Stopwatch.StartNew();

        var response = await _client!.PostAsJsonAsync(
            $"/api/sessions/{SessionId}/command",
            new { command = "start-work" });

        sw.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The slow session delays 5 seconds — response should arrive well before that
        sw.ElapsedMilliseconds.ShouldBeLessThan(2000);

        // The command should still have been dispatched (eventually)
        // Give a small window for the fire-and-forget task to start
        await Task.Delay(200);
        _slowSession!.SendCommandCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Command_passes_cancellation_token_none_to_orchestrator()
    {
        var response = await _client!.PostAsJsonAsync(
            $"/api/sessions/{SessionId}/command",
            new { command = "start-work" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Wait for the fire-and-forget task to execute
        await Task.Delay(200);

        _slowSession!.SendCommandCalls.Count.ShouldBe(1);
        // The cancellation token passed should not be cancelled even after the HTTP response completes
        _slowSession.LastCancellationToken.IsCancellationRequested.ShouldBeFalse();
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
                UserId
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
                UserId
            });

    private static Task<int> InsertSessionAsync(IDbConnection connection, string id, string instanceId, string directory, string createdAt) =>
        connection.ExecuteAsync(
            "INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title, status, directory, created_at, stopped_at, parent_session_id, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, harness_resume_token, is_hidden, retention_status, archived_at, user_id) VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title, @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus, @LifecycleStatus, @TotalTokens, @TotalCost, @HarnessType, @HarnessResumeToken, @IsHidden, @RetentionStatus, @ArchivedAt, @UserId)",
            new
            {
                Id = id,
                WorkspaceId = $"ws-{id}",
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
                UserId
            });

    /// <summary>
    /// A fake harness session that delays in SendCommandAsync to simulate a long-running LLM turn.
    /// </summary>
    private sealed class SlowFakeHarnessSession(string instanceId) : IHarnessSession
    {
        public string InstanceId { get; } = instanceId;
        public int? ProcessId => null;
        public string? ResumeToken => null;
        public string HarnessType => "opencode";
        public HarnessSessionStatus Status => HarnessSessionStatus.Running;

        public List<CommandOptions> SendCommandCalls { get; } = [];
        public CancellationToken LastCancellationToken { get; private set; }

        public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
        {
            SendCommandCalls.Add(options);
            LastCancellationToken = ct;
            // Simulate a long-running command — should NOT block the endpoint
            return Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct) => Task.FromResult(new MessagePage([], false));
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct) => Task.FromResult(new HealthCheckResult(true, null));
        public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CommandInfo>>([]);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

        public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
