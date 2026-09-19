using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Where Fleet finds OpenCode 2. The V2 installer writes <c>~/.opencode/bin/opencode</c> and an <c>opencode2</c>
/// next to it; Fleet runs <c>opencode2</c>, since a plain <c>opencode</c> may well be OpenCode 1.
/// </summary>
internal static class OpenCode2Executable
{
    public const string Command = "opencode2";

    /// <summary>OpenCode 2's major version. Anything else isn't the API this harness speaks.</summary>
    private const int MajorVersion = 2;

    /// <summary>Where the V2 install script (<c>curl -fsSL https://opencode.ai/v2/install | bash</c>) puts it.</summary>
    public static IEnumerable<string> InstallDirectories()
    {
        var home = ExecutableResolver.HomeDirectory();
        if (home is not null)
            yield return Path.Combine(home, ".opencode", "bin");
    }

    /// <summary>The executable's path, or <see langword="null"/> when it isn't installed.</summary>
    public static string? TryResolve()
        => ExecutableResolver.TryResolve(Command, InstallDirectories(), out var path) ? path : null;

    public static bool IsOpenCode2(string version)
        => !HarnessVersion.IsOlder(version, $"{MajorVersion}.0.0") && HarnessVersion.IsOlder(version, $"{MajorVersion + 1}.0.0");

    /// <summary>A ready install that isn't 2.x is not working for this harness; anything else is returned as it is.</summary>
    public static HarnessAvailability RequireOpenCode2(HarnessAvailability availability) => availability switch
    {
        { Available: true, Version: null } => HarnessAvailability.NotWorking(
            $"{Command} --version didn't print a version, so Fleet can't tell it's OpenCode 2.",
            executablePath: availability.ExecutablePath),
        { Available: true, Version: { } version } when !IsOpenCode2(version) => HarnessAvailability.NotWorking(
            $"{Command} is OpenCode {version}. The OpenCode 2 harness needs OpenCode 2.x.",
            version,
            availability.ExecutablePath),
        _ => availability,
    };
}
