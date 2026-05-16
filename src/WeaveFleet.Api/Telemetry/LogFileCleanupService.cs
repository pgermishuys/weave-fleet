using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Api.Telemetry;

/// <summary>
/// Background service that periodically deletes log files older than the configured retention period.
/// Runs once at startup and then every 24 hours.
/// </summary>
internal sealed partial class LogFileCleanupService : BackgroundService
{
    private readonly string _logDirectory;
    private readonly string _filePrefix;
    private readonly int _retentionDays;
    private readonly ILogger<LogFileCleanupService> _logger;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    public LogFileCleanupService(
        string logDirectory,
        string filePrefix,
        int retentionDays,
        ILogger<LogFileCleanupService> logger)
    {
        _logDirectory = logDirectory;
        _filePrefix = filePrefix;
        _retentionDays = retentionDays;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run cleanup immediately on startup, then repeat every 24 hours.
        while (!stoppingToken.IsCancellationRequested)
        {
            CleanupOldLogs();

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CleanupOldLogs()
    {
        if (!Directory.Exists(_logDirectory))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        var pattern = $"{_filePrefix}-*.log";

        try
        {
            var files = Directory.GetFiles(_logDirectory, pattern);
            var deleted = 0;

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    // Use LastWriteTimeUtc as a proxy for the log date — more reliable than
                    // CreationTimeUtc on network/container filesystems where creation time may
                    // be reset on copy. For daily rolling files, LastWriteTime equals the log date.
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    LogCleanupFileFailed(_logger, file, ex);
                }
            }

            if (deleted > 0)
                LogCleanupComplete(_logger, deleted, _retentionDays);
        }
        catch (Exception ex)
        {
            LogCleanupFailed(_logger, _logDirectory, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Diagnostic log cleanup: deleted {Count} file(s) older than {RetentionDays} days.")]
    private static partial void LogCleanupComplete(ILogger logger, int count, int retentionDays);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Diagnostic log cleanup: failed to delete file '{FilePath}'.")]
    private static partial void LogCleanupFileFailed(ILogger logger, string filePath, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Diagnostic log cleanup: error scanning log directory '{Directory}'.")]
    private static partial void LogCleanupFailed(ILogger logger, string directory, Exception ex);
}
