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
        tables.ShouldContain("workspace_roots");
        tables.ShouldContain("_migrations");
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RecordsMigrationInTable()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        await runner.ApplyMigrationsAsync(conn);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");
        count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_SkipsAlreadyAppliedMigrations()
    {
        using var conn = CreateInMemoryConnection();
        var factory = new SingleConnectionFactory(conn);
        var runner = CreateRunner(factory);

        // Apply once
        await runner.ApplyMigrationsAsync(conn);
        var countAfterFirst = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");

        // Apply again — should be idempotent (no duplicates, no error)
        await runner.ApplyMigrationsAsync(conn);
        var countAfterSecond = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _migrations");

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
            "SELECT name FROM _migrations ORDER BY id")).ToList();

        // Verify 001 comes first if there are multiple
        if (migrations.Count > 1)
        {
            string.Compare(migrations[0], migrations[1], StringComparison.Ordinal).ShouldBeLessThan(0);
        }

        migrations.ShouldNotBeEmpty();
        migrations.ShouldContain(m => m.StartsWith("001_", StringComparison.Ordinal));
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
            "SELECT name FROM _migrations ORDER BY id")).ToList();

        // All recorded migrations should be from the "Migrations" folder,
        // not from "AnalyticsMigrations"
        mainMigrations.ShouldAllBe(name => !name.Contains("analytics", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adapter that always returns the same pre-opened in-memory connection.
    /// </summary>
    private sealed class SingleConnectionFactory(SqliteConnection connection) : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => connection;
    }
}
