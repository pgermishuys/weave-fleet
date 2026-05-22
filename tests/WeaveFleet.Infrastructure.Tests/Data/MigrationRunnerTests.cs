using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Data;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Tests.Data;

public sealed class MigrationRunnerTests
{
    private static SqliteConnection CreateInMemoryConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static MigrationRunner CreateRunner(IDbConnectionFactory factory)
        => new(factory, NullLogger<MigrationRunner>.Instance);

    [Fact]
    public async Task ApplyMigrationsAsync_CreatesAllTables()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        tables.ShouldContain("projects");
        tables.ShouldContain("workspaces");
        tables.ShouldContain("instances");
        tables.ShouldContain("sessions");
        tables.ShouldContain("session_callbacks");
        tables.ShouldContain("session_source_usages");
        tables.ShouldContain("workspace_roots");
        tables.ShouldContain("legacy_imports");
        tables.ShouldContain(MigrationRunner.MainJournalTable);
        tables.ShouldNotContain("_migrations");
    }

    [Fact]
    public async Task ApplyMigrationsAsync_CreatesLegacyImportsTable()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var columns = (await conn.QueryAsync<(string Name, string Type, int NotNull, int PrimaryKey)>(
            """
            SELECT name AS Name, type AS Type, "notnull" AS "NotNull", pk AS PrimaryKey
            FROM pragma_table_info('legacy_imports')
            ORDER BY cid
            """)).ToList();

        columns.Select(column => column.Name).ShouldBe([
            "id",
            "source_path",
            "imported_at",
            "session_count",
            "status"
        ]);

        columns.Single(column => column.Name == "id").ShouldSatisfyAllConditions(
            column => column.Type.ShouldBe("TEXT"),
            column => column.PrimaryKey.ShouldBe(1));
        columns.Single(column => column.Name == "source_path").Type.ShouldBe("TEXT");
        columns.Single(column => column.Name == "imported_at").Type.ShouldBe("TEXT");
        columns.Single(column => column.Name == "session_count").Type.ShouldBe("INTEGER");
        columns.Single(column => column.Name == "status").Type.ShouldBe("TEXT");
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsSessionRetentionColumnsWithDefaults()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var columns = new Dictionary<string, (string Type, int NotNull, string? DefaultValue)>(StringComparer.OrdinalIgnoreCase);
        using var command = conn.CreateCommand();
        command.CommandText = "PRAGMA table_info(sessions);";
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns[reader.GetString(reader.GetOrdinal("name"))] =
                (reader.GetString(reader.GetOrdinal("type")), reader.GetInt32(reader.GetOrdinal("notnull")), reader.IsDBNull(reader.GetOrdinal("dflt_value")) ? null : reader.GetString(reader.GetOrdinal("dflt_value")));
        }

        columns.ShouldContainKey("retention_status");
        columns["retention_status"].Type.ShouldBe("TEXT");
        columns["retention_status"].NotNull.ShouldBe(1);
        columns["retention_status"].DefaultValue.ShouldBe("'active'");

        columns.ShouldContainKey("archived_at");
        columns["archived_at"].Type.ShouldBe("TEXT");
    }

    [Fact]
    public async Task apply_migrations_async_adds_nullable_session_git_baseline_columns()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var columns = new Dictionary<string, (string Type, int NotNull, string? DefaultValue)>(StringComparer.OrdinalIgnoreCase);
        using var command = conn.CreateCommand();
        command.CommandText = "PRAGMA table_info(sessions);";
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns[reader.GetString(reader.GetOrdinal("name"))] =
                (reader.GetString(reader.GetOrdinal("type")), reader.GetInt32(reader.GetOrdinal("notnull")), reader.IsDBNull(reader.GetOrdinal("dflt_value")) ? null : reader.GetString(reader.GetOrdinal("dflt_value")));
        }

        columns.ShouldContainKey("git_baseline_ref");
        columns["git_baseline_ref"].Type.ShouldBe("TEXT");
        columns["git_baseline_ref"].NotNull.ShouldBe(0);
        columns["git_baseline_ref"].DefaultValue.ShouldBeNull();

        columns.ShouldContainKey("git_repo_root");
        columns["git_repo_root"].Type.ShouldBe("TEXT");
        columns["git_repo_root"].NotNull.ShouldBe(0);
        columns["git_repo_root"].DefaultValue.ShouldBeNull();
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsWorkspaceSourceMetadataAndUsageTable()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var workspaceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = conn.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(workspaces);";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                workspaceColumns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        workspaceColumns.ShouldContain("source_provider_id");
        workspaceColumns.ShouldContain("source_type");
        workspaceColumns.ShouldContain("source_resource_id");
        workspaceColumns.ShouldContain("source_resource_url");
        workspaceColumns.ShouldContain("source_title");
        workspaceColumns.ShouldContain("source_summary");
        workspaceColumns.ShouldContain("source_resolved_at");

        var usageColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = conn.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(session_source_usages);";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                usageColumns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        usageColumns.ShouldContain("session_id");
        usageColumns.ShouldContain("workspace_id");
        usageColumns.ShouldContain("provider_id");
        usageColumns.ShouldContain("source_type");
        usageColumns.ShouldContain("action_id");
        usageColumns.ShouldContain("summary");
    }

    [Fact]
    public async Task ApplyMigrationsAsync_BackfillsExistingSessionsToActiveRetentionWithoutInferringArchiveState()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);

        await ApplyLegacyMigrationsThroughAsync(conn, "007_add_sessions_hidden_flag.sql");

        await conn.ExecuteAsync(
            "INSERT INTO projects (id, name, type, position, created_at, updated_at) VALUES ('proj-1', 'Project', 'user', 0, '2026-01-01', '2026-01-01')");
        await conn.ExecuteAsync(
            "INSERT INTO workspaces (id, directory, isolation_strategy, created_at) VALUES ('ws-1', '/tmp/proj', 'existing', '2026-01-01')");
        await conn.ExecuteAsync(
            "INSERT INTO instances (id, port, directory, url, status, created_at) VALUES ('inst-1', 0, '/tmp/proj', 'http://127.0.0.1:0', 'running', '2026-01-01')");
        await conn.ExecuteAsync(
            """
            INSERT INTO sessions (
                id, workspace_id, instance_id, project_id, opencode_session_id, title, status,
                directory, created_at, stopped_at, activity_status, lifecycle_status,
                total_tokens, total_cost, harness_type, harness_resume_token, is_hidden)
            VALUES (
                'sess-1', 'ws-1', 'inst-1', 'proj-1', 'oc-1', 'Legacy stopped session', 'completed',
                '/tmp/proj', '2026-01-01', '2026-01-02', 'idle', 'completed',
                0, 0, 'opencode', NULL, 0)
            """);

        var runner = CreateRunner(factory);
        await runner.ApplyMigrationsAsync(conn);

        var row = await conn.QuerySingleAsync<(string RetentionStatus, string? ArchivedAt, string LifecycleStatus)>(
            "SELECT retention_status AS RetentionStatus, archived_at AS ArchivedAt, lifecycle_status AS LifecycleStatus FROM sessions WHERE id = 'sess-1'");

        row.RetentionStatus.ShouldBe("active");
        row.ArchivedAt.ShouldBeNull();
        row.LifecycleStatus.ShouldBe("completed");

        var journalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");
        journalCount.ShouldBe(MigrationRunner.LoadScripts("Migrations").Count);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RecordsMigrationInDbUpJournal()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var count = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");
        count.ShouldBe(MigrationRunner.LoadScripts("Migrations").Count);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_SkipsAlreadyAppliedMigrations()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        // Apply once
        await runner.ApplyMigrationsAsync(conn);
        var countAfterFirst = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");

        // Apply again - should be idempotent (no duplicates, no error)
        await runner.ApplyMigrationsAsync(conn);
        var countAfterSecond = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");

        countAfterSecond.ShouldBe(countAfterFirst);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AppliesMigrationsInOrder()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var migrations = (await conn.QueryAsync<string>(
            $"SELECT ScriptName FROM {MigrationRunner.MainJournalTable} ORDER BY SchemaVersionId")).ToList();

        // Verify 001 comes first if there are multiple
        if (migrations.Count > 1)
        {
            string.Compare(migrations[0], migrations[1], StringComparison.Ordinal).ShouldBeLessThan(0);
        }

        migrations.ShouldNotBeEmpty();
        migrations.ShouldContain(m => m.EndsWith(".Migrations.001_initial_schema.sql", StringComparison.Ordinal));
    }

    // ── Segment filter tests ──────────────────────────────────────────────────

    [Fact]
    public async Task DefaultMigrationsSegment_DoesNotApplyAnalyticsMigrations()
    {
        // The main runner with default "Migrations" segment should only apply
        // resources whose dotted name contains "Migrations" as an exact segment.
        // "AnalyticsMigrations" is a DIFFERENT segment — not matched.
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory); // default "Migrations"

        await runner.ApplyMigrationsAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        // Analytics tables should NOT be created by the main runner
        tables.ShouldNotContain("token_events");
        tables.ShouldNotContain("session_snapshots");
        tables.ShouldNotContain("daily_rollups");
    }

    [Fact]
    public async Task AnalyticsMigrationsSegment_AppliesAnalyticsSchema()
    {
        // The analytics runner with "AnalyticsMigrations" segment should create
        // the three analytics tables and NOT the main app tables.
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance, "AnalyticsMigrations");

        await runner.ApplyMigrationsAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        tables.ShouldContain("token_events");
        tables.ShouldContain("session_snapshots");
        tables.ShouldContain("daily_rollups");

        // Main app tables should NOT be created by the analytics runner
        tables.ShouldNotContain("sessions");
        tables.ShouldNotContain("projects");
    }

    [Fact]
    public async Task SegmentMatchIsExact_NotSubstring()
    {
        // "Migrations" should not match "AnalyticsMigrations" — they are different dotted segments.
        // This test validates the exact-segment semantics of the filter.
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);

        // Main runner (segment = "Migrations")
        var mainRunner = CreateRunner(factory);
        await mainRunner.ApplyMigrationsAsync(conn);

        var mainMigrations = (await conn.QueryAsync<string>(
            $"SELECT ScriptName FROM {MigrationRunner.MainJournalTable} ORDER BY SchemaVersionId")).ToList();

        // All recorded migrations should be from the "Migrations" folder,
        // not from "AnalyticsMigrations"
        mainMigrations.ShouldAllBe(name => !name.Contains("analytics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyBridge_FullyMigratedLegacyJournal_SeedsDbUpJournalWithoutRerunningScripts()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await ApplyLegacyMigrationsThroughAsync(conn, null);
        var legacyCountBefore = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");

        await runner.ApplyMigrationsAsync(conn);

        var journalScripts = (await conn.QueryAsync<string>(
            $"SELECT ScriptName FROM {MigrationRunner.MainJournalTable} ORDER BY SchemaVersionId")).ToList();
        journalScripts.Count.ShouldBe(legacyCountBefore);
        journalScripts.ShouldAllBe(name => name.Contains(".Migrations.", StringComparison.Ordinal));

        var legacyCountAfter = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");
        legacyCountAfter.ShouldBe(legacyCountBefore);
    }

    [Fact]
    public async Task LegacyBridge_PoisonedLegacyJournalWithMissingProjects_ClearsLegacyJournalAndRebuilds()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await EnsureLegacyJournalAsync(conn);
        await conn.ExecuteAsync("INSERT INTO _migrations (name) VALUES ('001_initial_schema.sql')");

        await runner.ApplyMigrationsAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();
        tables.ShouldContain("projects");
        tables.ShouldContain("sessions");

        var legacyCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");
        legacyCount.ShouldBe(0);

        var journalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");
        journalCount.ShouldBe(MigrationRunner.LoadScripts("Migrations").Count);
    }

    [Fact]
    public async Task LegacyBridge_EmptyLegacyJournalWithLegacyWorkspaces_DropsLegacyTablesAndRebuilds()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await EnsureLegacyJournalAsync(conn);
        await conn.ExecuteAsync("CREATE TABLE workspaces (legacy_id TEXT PRIMARY KEY)");

        await runner.ApplyMigrationsAsync(conn);

        var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('workspaces')")).ToList();
        columns.ShouldContain("id");
        columns.ShouldContain("directory");
        columns.ShouldNotContain("legacy_id");

        var journalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {MigrationRunner.MainJournalTable}");
        journalCount.ShouldBe(MigrationRunner.LoadScripts("Migrations").Count);
    }

    [Fact]
    public async Task LegacyBridge_UnknownLegacyMigrationName_FailsClearly()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await EnsureLegacyJournalAsync(conn);
        await conn.ExecuteAsync("CREATE TABLE projects (id TEXT PRIMARY KEY)");
        await conn.ExecuteAsync("INSERT INTO _migrations (name) VALUES ('999_unknown.sql')");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => runner.ApplyMigrationsAsync(conn));
        ex.Message.ShouldContain("cannot be mapped to an embedded DbUp script");
    }

    [Fact]
    public async Task AnalyticsMigrationsSegment_UsesSeparateDbUpJournalAndLegacyBridge()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = new MigrationRunner(
            factory,
            NullLogger<MigrationRunner>.Instance,
            "AnalyticsMigrations",
            AnalyticsMigrationRunner.AnalyticsJournalTable);

        var firstAnalyticsScript = MigrationRunner.LoadScripts("AnalyticsMigrations")[0];
        await EnsureLegacyJournalAsync(conn);
        await conn.ExecuteAsync(firstAnalyticsScript.Contents);
        await conn.ExecuteAsync(
            "INSERT INTO _migrations (name) VALUES (@Name)",
            new { Name = MigrationRunner.ExtractMigrationName(firstAnalyticsScript.Name) });

        await runner.ApplyMigrationsAsync(conn);

        var journalScripts = (await conn.QueryAsync<string>(
            $"SELECT ScriptName FROM {AnalyticsMigrationRunner.AnalyticsJournalTable} ORDER BY SchemaVersionId")).ToList();
        journalScripts.Count.ShouldBe(MigrationRunner.LoadScripts("AnalyticsMigrations").Count);
        journalScripts.ShouldAllBe(name => name.Contains(".AnalyticsMigrations.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Adapter that always returns the same pre-opened in-memory connection.
    /// </summary>
    private sealed class SingleConnectionFactory(SqliteConnection connection) : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => connection;
    }

    private static async Task ApplyLegacyMigrationsThroughAsync(SqliteConnection conn, string? lastMigrationName)
    {
        await EnsureLegacyJournalAsync(conn);

        var scripts = MigrationRunner.LoadScripts("Migrations");
        foreach (var script in scripts)
        {
            await conn.ExecuteAsync(script.Contents);
            await conn.ExecuteAsync(
                "INSERT INTO _migrations (name) VALUES (@Name)",
                new { Name = MigrationRunner.ExtractMigrationName(script.Name) });

            if (lastMigrationName is not null
                && string.Equals(MigrationRunner.ExtractMigrationName(script.Name), lastMigrationName, StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    private static Task<int> EnsureLegacyJournalAsync(SqliteConnection conn)
        => conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS _migrations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);
}
