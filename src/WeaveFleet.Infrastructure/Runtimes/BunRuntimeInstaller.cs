using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>Finds and installs the Bun the mod host runs on.</summary>
internal sealed partial class BunRuntimeInstaller(
    FleetOptions options,
    IHostEnvironment environment,
    IHttpClientFactory httpClientFactory,
    ILogger<BunRuntimeInstaller> logger) : IBunRuntime
{
    private readonly object[] _dependencies = [options, environment, httpClientFactory, logger];

    internal string Home { get; init; } = Environment.CurrentDirectory;

    internal BunRelease Release { get; init; } = BunRelease.Pinned;

    internal string Rid { get; init; } = BunRelease.CurrentRid();

    internal Uri DownloadBase { get; init; } = BunRelease.GitHubDownloads;

    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    internal Func<string?> FindOnPath { get; init; } = () => null;

    public string Version => Release.Version;

    public BunInstallJob? Job => null;

    public BunLocation? Find() => throw new NotImplementedException();

    public Task<Result<BunLocation>> EnsureAsync(IProgress<BunInstallJob>? progress, CancellationToken ct) =>
        throw new NotImplementedException();
}
