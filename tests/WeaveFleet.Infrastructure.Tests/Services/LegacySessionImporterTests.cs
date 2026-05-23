using Dapper;
using Microsoft.Data.Sqlite;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class LegacySessionImporterTests
{
    private const string SourcePath = "/tmp/legacy/fleet.db";

    [Fact]
    public async Task imports_sessions_and_records_marker()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        var result = await importer.ImportAsync(SourcePath);

        result.Imported.ShouldBeTrue();
        result.Skipped.ShouldBeFalse();
        result.SessionCount.ShouldBe(2);
        result.Status.ShouldBe("completed");

        var sessions = (await destination.Keeper.QueryAsync<(string Id, string Title, string Status, string ProjectName, string WorkspaceId, string InstanceId)>(
            """
            SELECT s.id AS Id, s.title AS Title, s.status AS Status, p.name AS ProjectName,
                   s.workspace_id AS WorkspaceId, s.instance_id AS InstanceId
            FROM sessions s
            INNER JOIN projects p ON p.id = s.project_id
            ORDER BY s.id
            """)).ToList();
        sessions.Count.ShouldBe(2);
        sessions[0].ShouldBe(("legacy-session-1", "First legacy session", "stopped", "Imported Legacy Sessions", "legacy-import-workspace", "legacy-import-instance"));
        sessions[1].ShouldBe(("legacy-session-2", "Second legacy session", "completed", "Imported Legacy Sessions", "legacy-import-workspace", "legacy-import-instance"));

        var marker = await destination.Keeper.QuerySingleAsync<(string SourcePath, int SessionCount, string Status)>(
            "SELECT source_path AS SourcePath, session_count AS SessionCount, status AS Status FROM legacy_imports");
        marker.SourcePath.ShouldBe(Path.GetFullPath(SourcePath));
        marker.SessionCount.ShouldBe(2);
        marker.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task maps_each_legacy_status_to_imported_status_axes()
    {
        using var source = await CreateLegacySourceWithStatusesAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        await importer.ImportAsync(SourcePath);

        var importedAt = await destination.Keeper.QuerySingleAsync<string>("SELECT imported_at FROM legacy_imports");
        var sessions = (await destination.Keeper.QueryAsync<ImportedSessionStatus>(
            """
            SELECT id AS Id, status AS Status, lifecycle_status AS LifecycleStatus,
                   activity_status AS ActivityStatus, stopped_at AS StoppedAt
            FROM sessions
            WHERE id LIKE 'legacy-status-%'
            ORDER BY id
            """)).ToDictionary(session => session.Id, StringComparer.Ordinal);

        sessions.Count.ShouldBe(7);
        sessions["legacy-status-active"].ShouldBe(new ImportedSessionStatus("legacy-status-active", "stopped", "completed", null, importedAt));
        sessions["legacy-status-idle"].ShouldBe(new ImportedSessionStatus("legacy-status-idle", "stopped", "completed", null, importedAt));
        sessions["legacy-status-waiting-input"].ShouldBe(new ImportedSessionStatus("legacy-status-waiting-input", "stopped", "completed", null, null));
        sessions["legacy-status-stopped"].ShouldBe(new ImportedSessionStatus("legacy-status-stopped", "stopped", "stopped", null, "2026-01-03T00:00:00Z"));
        sessions["legacy-status-completed"].ShouldBe(new ImportedSessionStatus("legacy-status-completed", "completed", "completed", null, "2026-01-04T00:00:00Z"));
        sessions["legacy-status-error"].ShouldBe(new ImportedSessionStatus("legacy-status-error", "error", "error", null, "2026-01-05T00:00:00Z"));
        sessions["legacy-status-disconnected"].ShouldBe(new ImportedSessionStatus("legacy-status-disconnected", "disconnected", "disconnected", null, "2026-01-06T00:00:00Z"));
    }

    [Fact]
    public async Task imports_sessions_into_populated_destination_when_explicitly_requested()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        await SeedPopulatedDestinationAsync(destination.Keeper);
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        var result = await importer.ImportAsync(SourcePath);

        result.Imported.ShouldBeTrue();
        result.Skipped.ShouldBeFalse();
        result.SessionCount.ShouldBe(2);
        result.Status.ShouldBe("completed");

        var sessions = (await destination.Keeper.QueryAsync<(string Id, string ProjectName)>(
            """
            SELECT s.id AS Id, p.name AS ProjectName
            FROM sessions s
            INNER JOIN projects p ON p.id = s.project_id
            WHERE s.id IN ('existing-session', 'legacy-session-1', 'legacy-session-2')
            ORDER BY s.id
            """)).ToList();
        sessions.Count.ShouldBe(3);
        sessions[0].ShouldBe(("existing-session", "Existing"));
        sessions[1].ShouldBe(("legacy-session-1", "Imported Legacy Sessions"));
        sessions[2].ShouldBe(("legacy-session-2", "Imported Legacy Sessions"));

        var markerCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM legacy_imports");
        markerCount.ShouldBe(1);
    }

    [Fact]
    public async Task skips_when_source_path_already_imported()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        await importer.ImportAsync(SourcePath);
        var secondResult = await importer.ImportAsync(SourcePath);

        secondResult.Imported.ShouldBeFalse();
        secondResult.Skipped.ShouldBeTrue();
        secondResult.SessionCount.ShouldBe(2);
        secondResult.Status.ShouldBe("skipped");

        var sessionCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sessions");
        sessionCount.ShouldBe(2);
        var markerCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM legacy_imports");
        markerCount.ShouldBe(1);
    }

    [Fact]
    public async Task rolls_back_when_session_id_collides()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        await SeedDestinationCollisionAsync(destination.Keeper);
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => importer.ImportAsync(SourcePath));

        exception.Message.ShouldBe("Cannot import legacy session 'legacy-session-1' because a session with that id already exists.");

        var legacySessionCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE id IN ('legacy-session-1', 'legacy-session-2')");
        legacySessionCount.ShouldBe(1);
        var markerCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM legacy_imports");
        markerCount.ShouldBe(0);
        var importedProjectCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM projects WHERE name = 'Imported Legacy Sessions'");
        importedProjectCount.ShouldBe(0);
    }

    [Fact]
    public async Task rolls_back_partial_import_when_sentinel_workspace_id_collides()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        await SeedReservedWorkspaceIdCollisionAsync(destination.Keeper);
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => importer.ImportAsync(SourcePath));

        exception.Message.ShouldBe("Cannot import legacy sessions because id 'legacy-import-workspace' already exists in 'workspaces'.");

        var importedSessionCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE id IN ('legacy-session-1', 'legacy-session-2')");
        importedSessionCount.ShouldBe(0);
        var markerCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM legacy_imports");
        markerCount.ShouldBe(0);
        var importedProjectCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM projects WHERE id = 'legacy-imported-sessions-project'");
        importedProjectCount.ShouldBe(0);
        var sentinelInstanceCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM instances WHERE id = 'legacy-import-instance'");
        sentinelInstanceCount.ShouldBe(0);
    }

    [Fact]
    public async Task aborts_when_import_reserved_project_id_collides()
    {
        using var source = await CreateLegacySourceAsync();
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        await SeedReservedProjectIdCollisionAsync(destination.Keeper);
        var importer = CreateImporter(destination.Factory, source.ConnectionString);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => importer.ImportAsync(SourcePath));

        exception.Message.ShouldBe("Cannot import legacy sessions because id 'legacy-imported-sessions-project' already exists in 'projects'.");

        var importedSessionCount = await destination.Keeper.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sessions WHERE id IN ('legacy-session-1', 'legacy-session-2')");
        importedSessionCount.ShouldBe(0);
        var markerCount = await destination.Keeper.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM legacy_imports");
        markerCount.ShouldBe(0);
    }

    private static LegacySessionImporter CreateImporter(
        WeaveFleet.Application.Data.IDbConnectionFactory destinationFactory,
        string sourceConnectionString)
        => new(destinationFactory, new TestUserContext(), new InMemoryLegacyDatabaseConnectionFactory(sourceConnectionString));

    private static async Task<LegacySource> CreateLegacySourceAsync()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            CREATE TABLE workspaces (id TEXT PRIMARY KEY, directory TEXT, created_at TEXT);
            CREATE TABLE instances (id TEXT PRIMARY KEY, directory TEXT, status TEXT, created_at TEXT);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                workspace_id TEXT,
                instance_id TEXT,
                opencode_session_id TEXT,
                title TEXT,
                status TEXT,
                directory TEXT,
                created_at TEXT,
                stopped_at TEXT,
                parent_session_id TEXT,
                activity_status TEXT,
                lifecycle_status TEXT,
                total_tokens INTEGER,
                total_cost REAL,
                harness_type TEXT,
                harness_resume_token TEXT,
                is_hidden INTEGER
            );
            INSERT INTO workspaces (id, directory, created_at) VALUES ('legacy-workspace-1', '/src/legacy-one', '2026-01-01T00:00:00Z');
            INSERT INTO instances (id, directory, status, created_at) VALUES ('legacy-instance-1', '/src/legacy-one', 'stopped', '2026-01-01T00:00:00Z');
            INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title, status, directory, created_at, total_tokens, total_cost, harness_type, is_hidden)
            VALUES
                ('legacy-session-1', 'legacy-workspace-1', 'legacy-instance-1', 'oc-1', 'First legacy session', 'active', '/src/legacy-one', '2026-01-01T00:00:00Z', 100, 0.25, 'opencode', 0),
                ('legacy-session-2', 'legacy-workspace-1', 'legacy-instance-1', 'oc-2', 'Second legacy session', 'completed', NULL, '2026-01-02T00:00:00Z', 200, 0.50, 'opencode', 1);
            """);

        return new LegacySource(connection, connectionString);
    }

    private static async Task<LegacySource> CreateLegacySourceWithStatusesAsync()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            CREATE TABLE workspaces (id TEXT PRIMARY KEY, directory TEXT, created_at TEXT);
            CREATE TABLE instances (id TEXT PRIMARY KEY, directory TEXT, status TEXT, created_at TEXT);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                workspace_id TEXT,
                instance_id TEXT,
                opencode_session_id TEXT,
                title TEXT,
                status TEXT,
                directory TEXT,
                created_at TEXT,
                stopped_at TEXT,
                parent_session_id TEXT,
                activity_status TEXT,
                lifecycle_status TEXT,
                total_tokens INTEGER,
                total_cost REAL,
                harness_type TEXT,
                harness_resume_token TEXT,
                is_hidden INTEGER
            );
            INSERT INTO workspaces (id, directory, created_at) VALUES ('legacy-workspace-1', '/src/legacy-one', '2026-01-01T00:00:00Z');
            INSERT INTO instances (id, directory, status, created_at) VALUES ('legacy-instance-1', '/src/legacy-one', 'stopped', '2026-01-01T00:00:00Z');
            INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title, status, directory, created_at, stopped_at, activity_status, lifecycle_status, total_tokens, total_cost, harness_type, is_hidden)
            VALUES
                ('legacy-status-active', 'legacy-workspace-1', 'legacy-instance-1', 'oc-active', 'Active legacy session', 'active', '/src/legacy-one', '2026-01-01T00:00:00Z', NULL, 'idle', 'running', 0, 0, 'opencode', 0),
                ('legacy-status-idle', 'legacy-workspace-1', 'legacy-instance-1', 'oc-idle', 'Idle legacy session', 'idle', '/src/legacy-one', '2026-01-01T00:00:00Z', NULL, 'idle', 'running', 0, 0, 'opencode', 0),
                ('legacy-status-waiting-input', 'legacy-workspace-1', 'legacy-instance-1', 'oc-waiting-input', 'Waiting legacy session', 'waiting_input', '/src/legacy-one', '2026-01-01T00:00:00Z', NULL, 'waiting_input', 'running', 0, 0, 'opencode', 0),
                ('legacy-status-stopped', 'legacy-workspace-1', 'legacy-instance-1', 'oc-stopped', 'Stopped legacy session', 'stopped', '/src/legacy-one', '2026-01-01T00:00:00Z', '2026-01-03T00:00:00Z', 'idle', 'stopped', 0, 0, 'opencode', 0),
                ('legacy-status-completed', 'legacy-workspace-1', 'legacy-instance-1', 'oc-completed', 'Completed legacy session', 'completed', '/src/legacy-one', '2026-01-01T00:00:00Z', '2026-01-04T00:00:00Z', 'idle', 'completed', 0, 0, 'opencode', 0),
                ('legacy-status-error', 'legacy-workspace-1', 'legacy-instance-1', 'oc-error', 'Error legacy session', 'error', '/src/legacy-one', '2026-01-01T00:00:00Z', '2026-01-05T00:00:00Z', 'idle', 'error', 0, 0, 'opencode', 0),
                ('legacy-status-disconnected', 'legacy-workspace-1', 'legacy-instance-1', 'oc-disconnected', 'Disconnected legacy session', 'disconnected', '/src/legacy-one', '2026-01-01T00:00:00Z', '2026-01-06T00:00:00Z', 'idle', 'disconnected', 0, 0, 'opencode', 0);
            """);

        return new LegacySource(connection, connectionString);
    }

    private static async Task SeedDestinationCollisionAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id)
            VALUES ('existing-project', 'Existing', NULL, 'user', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'test-user');
            INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary, source_resolved_at, user_id)
            VALUES ('existing-workspace', '/tmp/existing', NULL, 'existing', NULL, '2026-01-01T00:00:00Z', NULL, NULL,
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'test-user');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id)
            VALUES ('existing-instance', 0, NULL, '/tmp/existing', '', 'stopped', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'test-user');
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, retention_status, archived_at, is_hidden, total_tokens, total_cost,
                harness_type, harness_resume_token, user_id)
            VALUES ('legacy-session-1', 'existing-workspace', 'existing-instance', 'existing-project', 'existing-oc', 'Existing',
                'active', '/tmp/existing', '2026-01-01T00:00:00Z', NULL, NULL, 'idle',
                'active', 'active', NULL, 0, 0, 0,
                'opencode', NULL, 'test-user');
            """);
    }

    private static async Task SeedPopulatedDestinationAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id)
            VALUES ('existing-project', 'Existing', NULL, 'user', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'test-user');
            INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary, source_resolved_at, user_id)
            VALUES ('existing-workspace', '/tmp/existing', NULL, 'existing', NULL, '2026-01-01T00:00:00Z', NULL, NULL,
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'test-user');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id)
            VALUES ('existing-instance', 0, NULL, '/tmp/existing', '', 'stopped', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'test-user');
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, retention_status, archived_at, is_hidden, total_tokens, total_cost,
                harness_type, harness_resume_token, user_id)
            VALUES ('existing-session', 'existing-workspace', 'existing-instance', 'existing-project', 'existing-oc', 'Existing',
                'active', '/tmp/existing', '2026-01-01T00:00:00Z', NULL, NULL, 'idle',
                'active', 'active', NULL, 0, 0, 0,
                'opencode', NULL, 'test-user');
            """);
    }

    private static async Task SeedReservedProjectIdCollisionAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id)
            VALUES ('legacy-imported-sessions-project', 'Conflicting Project', NULL, 'user', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'other-user');
            """);
    }

    private static async Task SeedReservedWorkspaceIdCollisionAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO workspaces (id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary, source_resolved_at, user_id)
            VALUES ('legacy-import-workspace', '/tmp/conflicting', NULL, 'existing', NULL, '2026-01-01T00:00:00Z', NULL, NULL,
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'other-user');
            """);
    }

    private sealed record LegacySource(SqliteConnection Connection, string ConnectionString) : IDisposable
    {
        public void Dispose() => Connection.Dispose();
    }

    private sealed record ImportedSessionStatus(
        string Id,
        string Status,
        string LifecycleStatus,
        string? ActivityStatus,
        string? StoppedAt);

    private sealed class InMemoryLegacyDatabaseConnectionFactory(string connectionString) : ILegacyDatabaseConnectionFactory
    {
        public bool Exists(string sourcePath) => true;

        public async Task<System.Data.Common.DbConnection> OpenReadOnlyAsync(string sourcePath, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
