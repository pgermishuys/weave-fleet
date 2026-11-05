using System.ComponentModel;
using WeaveFleet.Application.Machines;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>Reads <c>tailscale status --json</c> with the user's own <c>tailscale</c>. Null when it isn't installed or doesn't answer.</summary>
public sealed class TailscaleProcess(string executable = "tailscale") : ITailscale
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public async Task<string?> StatusJsonAsync(CancellationToken cancellationToken)
    {
        var start = SshProcess.Start(executable);
        start.ArgumentList.Add("status");
        start.ArgumentList.Add("--json");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)!;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using (process)
        {
            ProcessGroupHelper.AssignToProcessGroup(process);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
                return process.ExitCode == 0 ? await output : null;
            }
            catch (OperationCanceledException)
            {
                ProcessGroupHelper.KillProcessGroup(process);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
