using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Performs the one-time startup auto-import of legacy local sessions into a fresh database.
/// </summary>
public sealed partial class LegacySessionImportStartupService : IHostedService
{
    private const string LegacyDatabaseDisplayPath = "~/.weave/fleet.db";
    private const string StartupImportUserId = "local-user";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacySessionImportStartupService> _logger;
    private readonly Func<string, bool> _fileExists;
    private readonly string _legacyDatabasePath;

    public LegacySessionImportStartupService(
        IServiceScopeFactory scopeFactory,
        ILogger<LegacySessionImportStartupService> logger)
        : this(scopeFactory, logger, File.Exists, GetDefaultLegacyDatabasePath())
    {
    }

    internal LegacySessionImportStartupService(
        IServiceScopeFactory scopeFactory,
        ILogger<LegacySessionImportStartupService> logger,
        Func<string, bool> fileExists,
        string legacyDatabasePath)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _fileExists = fileExists;
        _legacyDatabasePath = legacyDatabasePath;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var sessionCount = await GetSessionCountAsync(connectionFactory, cancellationToken).ConfigureAwait(false);

        if (!_fileExists(_legacyDatabasePath))
            return;

        if (sessionCount > 0)
        {
            LogLegacySessionsDetected(_logger);
            return;
        }

        using var userScope = BackgroundUserContext.BeginScope(StartupImportUserId);
        var importer = scope.ServiceProvider.GetRequiredService<ILegacySessionImporter>();
        var result = await importer.ImportAsync(cancellationToken).ConfigureAwait(false);
        LogLegacySessionsImported(_logger, result.SessionCount, result.Status);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<int> GetSessionCountAsync(
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        await using var command = ((DbConnection)connection).CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetDefaultLegacyDatabasePath()
        => Path.Combine(GetUserProfileDirectory(), ".weave", "fleet.db");

    private static string GetUserProfileDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Legacy sessions detected at ~/.weave/fleet.db. Use `import-legacy-sessions` to import explicitly.")]
    private static partial void LogLegacySessionsDetected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Legacy session startup import completed with status {Status}; imported {SessionCount} session(s).")]
    private static partial void LogLegacySessionsImported(ILogger logger, int sessionCount, string status);
}
