using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Tests.Nats;

/// <summary>
/// xUnit fixture that launches a bundled <c>nats-server</c> on a random loopback port with
/// JetStream enabled in a temporary storage directory. Shared across a test class so setup cost
/// is paid once.
/// </summary>
public sealed class EmbeddedNatsTestFixture : IAsyncLifetime
{
    public string Url { get; private set; } = "";
    private Process? _process;
    private string? _storageDir;

    public async Task InitializeAsync()
    {
        var port = GetFreePort();
        _storageDir = Path.Combine(Path.GetTempPath(), $"nats-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageDir);
        var binary = FindBinary();
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"-a 127.0.0.1 -p {port} -js -sd \"{_storageDir}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start nats-server");

        Url = $"nats://127.0.0.1:{port}";

        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"nats-server on port {port} did not become ready.");
    }

    public Task DisposeAsync()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        try { _process?.WaitForExit(5000); } catch { }
        _process?.Dispose();
        if (_storageDir is not null && Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string FindBinary()
    {
        string rid;
        string exe;
        if (OperatingSystem.IsWindows()) { rid = "win-x64"; exe = "nats-server.exe"; }
        else if (OperatingSystem.IsMacOS())
        {
            rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            exe = "nats-server";
        }
        else { rid = "linux-x64"; exe = "nats-server"; }
        var candidate = Path.Combine(AppContext.BaseDirectory, "Nats", "EmbeddedNatsServer", "Binaries", rid, exe);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Bundled nats-server binary not found for RID '{rid}' at {candidate}.");
        return candidate;
    }
}
