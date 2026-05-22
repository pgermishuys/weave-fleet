using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class AdminEndpointsTests
{
    [Fact]
    public async Task import_legacy_sessions_invokes_importer_and_returns_result_when_database_is_populated()
    {
        var importer = new SequencedLegacySessionImporter([
            new LegacySessionImportResult(true, false, "/tmp/legacy/fleet.db", 2, "completed")
        ]);

        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<ILegacySessionImporter>();
                services.AddSingleton<ILegacySessionImporter>(importer);
        });
        using var client = factory.CreateClient();
        await PopulateDestinationDatabaseAsync(factory);

        var response = await client.PostAsync("/api/admin/import-legacy-sessions", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        importer.CallCount.ShouldBe(1);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        body.GetProperty("imported").GetBoolean().ShouldBeTrue();
        body.GetProperty("skipped").GetBoolean().ShouldBeFalse();
        body.GetProperty("sourcePath").GetString().ShouldBe("/tmp/legacy/fleet.db");
        body.GetProperty("count").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("completed");
        body.GetProperty("errors").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task import_legacy_sessions_is_idempotent_when_called_twice()
    {
        var importer = new SequencedLegacySessionImporter([
            new LegacySessionImportResult(true, false, "/tmp/legacy/fleet.db", 2, "completed"),
            new LegacySessionImportResult(false, true, "/tmp/legacy/fleet.db", 2, "skipped")
        ]);

        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<ILegacySessionImporter>();
                services.AddSingleton<ILegacySessionImporter>(importer);
            });
        using var client = factory.CreateClient();

        var firstResponse = await client.PostAsync("/api/admin/import-legacy-sessions", content: null);
        var secondResponse = await client.PostAsync("/api/admin/import-legacy-sessions", content: null);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        importer.CallCount.ShouldBe(2);

        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        body.GetProperty("imported").GetBoolean().ShouldBeFalse();
        body.GetProperty("skipped").GetBoolean().ShouldBeTrue();
        body.GetProperty("count").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("skipped");
        body.GetProperty("errors").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task import_legacy_sessions_returns_clear_conflict_when_importer_reports_id_collision()
    {
        const string error = "Cannot import legacy session 'session-1' because a session with that id already exists.";
        var importer = new ThrowingLegacySessionImporter(new InvalidOperationException(error));

        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<ILegacySessionImporter>();
                services.AddSingleton<ILegacySessionImporter>(importer);
            });
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/admin/import-legacy-sessions", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        importer.CallCount.ShouldBe(1);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        body.GetProperty("imported").GetBoolean().ShouldBeFalse();
        body.GetProperty("skipped").GetBoolean().ShouldBeFalse();
        body.GetProperty("count").GetInt32().ShouldBe(0);
        body.GetProperty("status").GetString().ShouldBe("failed");
        body.GetProperty("errors")[0].GetString().ShouldBe(error);
    }

    private sealed class SequencedLegacySessionImporter(IReadOnlyList<LegacySessionImportResult> results) : ILegacySessionImporter
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<LegacySessionImportResult> ImportAsync()
            => ImportAsync(CancellationToken.None);

        public Task<LegacySessionImportResult> ImportAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(_callCount, results.Count - 1);
            _callCount++;
            return Task.FromResult(results[index]);
        }

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath)
            => ImportAsync(sourcePath, CancellationToken.None);

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
            => ImportAsync(cancellationToken);
    }

    private sealed class ThrowingLegacySessionImporter(Exception exception) : ILegacySessionImporter
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<LegacySessionImportResult> ImportAsync()
            => ImportAsync(CancellationToken.None);

        public Task<LegacySessionImportResult> ImportAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _callCount++;
            return Task.FromException<LegacySessionImportResult>(exception);
        }

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath)
            => ImportAsync(sourcePath, CancellationToken.None);

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
            => ImportAsync(cancellationToken);
    }

    private static async Task PopulateDestinationDatabaseAsync(ApiWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();
        var createdAt = DateTimeOffset.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, created_at, display_name, user_id) VALUES (@Id, @Directory, @CreatedAt, @DisplayName, @UserId)",
            new
            {
                Id = "existing-workspace",
                Directory = "/tmp/existing-workspace",
                CreatedAt = createdAt,
                DisplayName = "Existing Workspace",
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            "INSERT INTO instances (id, port, directory, url, status, created_at, user_id) VALUES (@Id, @Port, @Directory, @Url, @Status, @CreatedAt, @UserId)",
            new
            {
                Id = "existing-instance",
                Port = 0,
                Directory = "/tmp/existing-workspace",
                Url = string.Empty,
                Status = "running",
                CreatedAt = createdAt,
                UserId = "local-user"
            });

        await connection.ExecuteAsync(
            """
            INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title,
                status, directory, created_at, lifecycle_status, retention_status, is_hidden,
                total_tokens, total_cost, harness_type, user_id)
            VALUES (@Id, @WorkspaceId, @InstanceId, @OpencodeSessionId, @Title,
                @Status, @Directory, @CreatedAt, @LifecycleStatus, @RetentionStatus, @IsHidden,
                @TotalTokens, @TotalCost, @HarnessType, @UserId)
            """,
            new
            {
                Id = "existing-session",
                WorkspaceId = "existing-workspace",
                InstanceId = "existing-instance",
                OpencodeSessionId = "existing-opencode-session",
                Title = "Existing Session",
                Status = "completed",
                Directory = "/tmp/existing-workspace",
                CreatedAt = createdAt,
                LifecycleStatus = "completed",
                RetentionStatus = "active",
                IsHidden = 0,
                TotalTokens = 0,
                TotalCost = 0.0,
                HarnessType = "opencode",
                UserId = "local-user"
            });
    }
}
