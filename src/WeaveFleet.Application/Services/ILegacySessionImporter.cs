namespace WeaveFleet.Application.Services;

public interface ILegacySessionImporter
{
    Task<LegacySessionImportResult> ImportAsync();
    Task<LegacySessionImportResult> ImportAsync(CancellationToken cancellationToken);
    Task<LegacySessionImportResult> ImportAsync(string sourcePath);
    Task<LegacySessionImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken);
}

public sealed record LegacySessionImportResult(
    bool Imported,
    bool Skipped,
    string SourcePath,
    int SessionCount,
    string Status);
