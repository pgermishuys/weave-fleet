using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>Builds the commands that update a harness, depending on how it was installed.</summary>
internal static class UpdateCommands
{
    /// <summary>True for an executable winget installed (its links and packages live under <c>…\Microsoft\WinGet\</c>).</summary>
    public static bool IsWingetInstall(string executablePath) =>
        executablePath.Replace('/', '\\').Contains(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>winget upgrade</c> for one package, without prompts: Fleet runs it with no terminal to answer them.</summary>
    public static HarnessCommand Winget(string packageId) => new(
        ExecutableResolver.Resolve("winget"),
        ["upgrade", "--id", packageId, "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements"],
        $"winget upgrade --id {packageId} --exact");

    /// <summary>The harness's own updater, e.g. <c>opencode upgrade 1.18.31</c> or <c>claude update</c>.</summary>
    public static HarnessCommand Native(string executablePath, IReadOnlyList<string> arguments) => new(
        executablePath,
        arguments,
        string.Join(' ', [ShellCommand.Executable(executablePath, OperatingSystem.IsWindows()), .. arguments]));
}
