using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Nats.EmbeddedNatsServer;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Launches the bundled nats-server subprocess on loopback when no external broker URL is
/// configured. No-op when <see cref="NatsOptions.ExternalUrl"/> is set. Exposes
/// <see cref="ResolvedUrl"/> so the NATS client registration can consume it after startup.
/// </summary>
public sealed class NatsServerHostedService : IHostedService, IAsyncDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogLaunched =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "NatsServerLaunched"),
            "Embedded nats-server launched at {Url}");
    private static readonly Action<ILogger, Exception?> LogSkipped =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, "NatsServerSkipped"),
            "Embedded nats-server skipped (ExternalUrl configured)");

    private readonly NatsOptions _options;
    private readonly ILogger<NatsServerHostedService> _logger;
    private Process? _process;

    public string ResolvedUrl { get; private set; } = "";
    public bool IsEmbeddedRunning => _process is { HasExited: false };

    public NatsServerHostedService(
        NatsOptions options,
        ILogger<NatsServerHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ExternalUrl))
        {
            ResolvedUrl = _options.ExternalUrl!;
            LogSkipped(_logger, null);
            return;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        var port = GetFreeLoopbackPort();
        var binary = NatsServerBinaryResolver.Resolve();
        var args = $"-a 127.0.0.1 -p {port} -js -sd \"{Path.GetFullPath(_options.DataDirectory)}\"";

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = binary,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        }) ?? throw new InvalidOperationException("Failed to start embedded nats-server.");

        ResolvedUrl = $"nats://127.0.0.1:{port}";
        await WaitForReadyAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
        LogLaunched(_logger, ResolvedUrl, null);
    }

    private bool _stopped;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        // NATS connection is disposed earlier (in ApplicationStopping) so that consuming
        // services exit their subscription loops immediately without waiting for internal
        // NATS client timeouts. No need to dispose it here again.

        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _process?.Dispose();
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForReadyAsync(string host, int port, CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return;
            }
            catch (SocketException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
        throw new TimeoutException($"Embedded nats-server did not become ready on {host}:{port}.");
    }
}
