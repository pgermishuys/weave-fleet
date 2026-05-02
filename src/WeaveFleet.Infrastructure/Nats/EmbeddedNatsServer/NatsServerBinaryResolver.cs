using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Nats.EmbeddedNatsServer;

internal static class NatsServerBinaryResolver
{
    public static string Resolve()
    {
        var rid = ResolveRid();
        var exe = OperatingSystem.IsWindows() ? "nats-server.exe" : "nats-server";
        var candidate = Path.Combine(AppContext.BaseDirectory, "Nats", "EmbeddedNatsServer", "Binaries", rid, exe);
        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                $"Bundled nats-server not found for RID '{rid}' at {candidate}. Ensure the runtime is supported.");
        return candidate;
    }

    private static string ResolveRid()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (OperatingSystem.IsLinux()) return "linux-x64";
        throw new PlatformNotSupportedException($"No bundled nats-server for OS {RuntimeInformation.OSDescription}.");
    }
}
