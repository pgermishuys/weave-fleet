using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// What Fleet runs as OpenCode 2. The V2 installer writes <c>opencode</c> and an <c>opencode2</c> next to it; other
/// installs (Homebrew, npm, <c>opencode upgrade</c>) only have <c>opencode</c>. The major version it reports decides:
/// 2.x is OpenCode 2, whatever it's called, and a plain <c>opencode</c> may well be OpenCode 1. Where it looks is
/// <see cref="OpenCode2Install"/>.
/// </summary>
internal static class OpenCode2Executable
{
    public const string Command = "opencode2";

    /// <summary>The name OpenCode 2 shares with OpenCode 1, tried after <see cref="Command"/>.</summary>
    public const string PlainCommand = "opencode";

    /// <summary>OpenCode 2's major version. Anything else isn't the API this harness speaks.</summary>
    private const int MajorVersion = 2;

    public static bool IsOpenCode2(string version)
        => !HarnessVersion.IsOlder(version, $"{MajorVersion}.0.0") && HarnessVersion.IsOlder(version, $"{MajorVersion + 1}.0.0");

    /// <summary>A ready install that isn't 2.x is not working for this harness; anything else is returned as it is.</summary>
    public static HarnessAvailability RequireOpenCode2(HarnessAvailability availability) => availability switch
    {
        { Available: true, Version: null } => HarnessAvailability.NotWorking(
            $"{Name(availability.ExecutablePath)} --version didn't print a version, so Fleet can't tell it's OpenCode 2.",
            executablePath: availability.ExecutablePath),
        { Available: true, Version: { } version } when !IsOpenCode2(version) => HarnessAvailability.NotWorking(
            $"{Name(availability.ExecutablePath)} is OpenCode {version}. The OpenCode 2 harness needs OpenCode 2.x.",
            version,
            availability.ExecutablePath),
        _ => availability,
    };

    /// <summary>The executable's name without its folder or extension, as the user would type it.</summary>
    public static string Name(string? executablePath)
        => executablePath is null ? Command : Path.GetFileNameWithoutExtension(executablePath);
}
