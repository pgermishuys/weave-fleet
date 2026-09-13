using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Terminals;

/// <summary>
/// Saves every terminal's scrollback and ends its shell when Fleet stops, so the terminals come back as
/// stopped tabs with their output after a restart.
/// </summary>
internal sealed class TerminalShutdownService(TerminalManager terminals) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => terminals.ShutdownAsync();
}
