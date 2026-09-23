using Microsoft.Extensions.Hosting;

namespace WeaveFleet.Application.Workflows;

/// <summary>Picks up unfinished workflow runs once Fleet has started (<see cref="WorkflowRunner.RecoverAsync"/>).</summary>
public sealed class WorkflowRunRecovery(WorkflowRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RecoverAsync(stoppingToken);
}
