using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// A server that was started but never answers (a catalogue read that times out, a cancelled request) must not stay
/// behind: the runtime disposes the process manager when the start fails, and that ends the server and its children.
/// </summary>
public sealed class OpenCode2StartupCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"oc2-startup-{Guid.NewGuid():N}");

    public OpenCode2StartupCleanupTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task A_server_that_never_listens_is_gone_with_its_children_once_the_start_fails(string how)
    {
        if (OperatingSystem.IsWindows())
            return;

        var childPid = Path.Combine(_directory, "child.pid");
        var executable = Path.Combine(_directory, "opencode2");
        TestExecutable.Write(executable, $"#!/bin/sh\nsleep 300 &\necho $! > '{childPid}'\nsleep 300\n");

        var manager = new OpenCode2ProcessManager(NullLogger<OpenCode2ProcessManager>.Instance);
        using var cancel = new CancellationTokenSource();
        var starting = manager.StartAsync(new OpenCode2ProcessOptions
        {
            ExecutablePath = executable,
            WorkingDirectory = _directory,
            Password = "pw",
            EnvironmentVariables = new Dictionary<string, string>(),
            StartupTimeout = how == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1),
        }, cancel.Token);

        // The server is running (with a child of its own) while it hasn't listened.
        await WaitForAsync(() => File.Exists(childPid) && File.ReadAllText(childPid).Trim().Length > 0);
        var serverPid = manager.ProcessId!.Value;
        var child = int.Parse(File.ReadAllText(childPid).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Alive(serverPid).ShouldBeTrue();
        Alive(child).ShouldBeTrue();

        if (how == "cancel")
            cancel.Cancel();

        try
        {
            await starting;
            throw new InvalidOperationException("The start should have failed.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // What the runtime does when a start fails.
            await manager.DisposeAsync();
        }

        await WaitForAsync(() => !Alive(serverPid) && !Alive(child));
    }

    private static bool Alive(int pid)
        => File.Exists($"/proc/{pid}/stat") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition not met in 15 s.");
            await Task.Delay(50);
        }
    }
}
