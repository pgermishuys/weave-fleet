using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class LegacySessionImportStartupServiceTests
{
    [Fact]
    public async Task imports_legacy_sessions_when_destination_sessions_are_empty_and_legacy_db_exists()
    {
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        var importer = new CapturingLegacySessionImporter();
        await using var provider = CreateServiceProvider(destination.Factory, importer);
        var logger = new CapturingLogger<LegacySessionImportStartupService>();
        var service = new LegacySessionImportStartupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            _ => true,
            "/tmp/legacy/fleet.db");

        await service.StartAsync(CancellationToken.None);

        importer.CallCount.ShouldBe(1);
        importer.UsedDefaultPath.ShouldBeTrue();
    }

    [Fact]
    public async Task logs_notification_only_when_destination_sessions_exist_and_legacy_db_exists()
    {
        var destination = await TestDbHelper.CreateSharedDbAsync();
        using var keeper = destination.Keeper;
        await SeedSessionAsync(keeper);
        var importer = new CapturingLegacySessionImporter();
        await using var provider = CreateServiceProvider(destination.Factory, importer);
        var logger = new CapturingLogger<LegacySessionImportStartupService>();
        var service = new LegacySessionImportStartupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            _ => true,
            "/tmp/legacy/fleet.db");

        await service.StartAsync(CancellationToken.None);

        importer.CallCount.ShouldBe(0);
        logger.Messages.ShouldContain(
            "Legacy sessions detected at ~/.weave/fleet.db. Use `import-legacy-sessions` to import explicitly.");
    }

    private static ServiceProvider CreateServiceProvider(
        WeaveFleet.Application.Data.IDbConnectionFactory destinationFactory,
        ILegacySessionImporter importer)
    {
        var services = new ServiceCollection();
        services.AddSingleton(destinationFactory);
        services.AddScoped(_ => importer);
        return services.BuildServiceProvider();
    }

    private static async Task SeedSessionAsync(System.Data.IDbConnection connection)
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
                'stopped', '/tmp/existing', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', NULL, NULL,
                'stopped', 'active', NULL, 0, 0, 0,
                'opencode', NULL, 'test-user');
            """);
    }

    private sealed class CapturingLegacySessionImporter : ILegacySessionImporter
    {
        public int CallCount { get; private set; }
        public bool UsedDefaultPath { get; private set; }

        public Task<LegacySessionImportResult> ImportAsync()
        {
            CallCount++;
            UsedDefaultPath = true;
            return Task.FromResult(new LegacySessionImportResult(true, false, "/tmp/legacy/fleet.db", 2, "completed"));
        }

        public Task<LegacySessionImportResult> ImportAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            UsedDefaultPath = true;
            return Task.FromResult(new LegacySessionImportResult(true, false, "/tmp/legacy/fleet.db", 2, "completed"));
        }

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath)
        {
            CallCount++;
            UsedDefaultPath = false;
            return Task.FromResult(new LegacySessionImportResult(true, false, sourcePath, 2, "completed"));
        }

        public Task<LegacySessionImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
        {
            CallCount++;
            UsedDefaultPath = false;
            return Task.FromResult(new LegacySessionImportResult(true, false, sourcePath, 2, "completed"));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
