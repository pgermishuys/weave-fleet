using System.Text.RegularExpressions;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>How OpenCode 2 is installed on this machine.</summary>
internal enum OpenCode2InstallMode
{
    /// <summary>Where OpenCode's V2 installer puts it (<c>~/.opencode/bin</c>), with OpenCode's usual config folder and database.</summary>
    Default,

    /// <summary>
    /// Next to OpenCode 1: the same installer with <c>HOME</c> set to <c>~/.weave/harnesses/opencode2</c>, and Fleet
    /// starts it with a config folder and database of its own there.
    /// </summary>
    Separate,
}

/// <summary>What <see cref="OpenCode2Install.CheckAsync"/> found: the install's mode, whether it's remembered, and the executable's state.</summary>
internal sealed record OpenCode2InstallCheck(OpenCode2InstallMode Mode, bool Remembered, HarnessAvailability Availability);

/// <summary>
/// Where OpenCode 2 lives on this machine, and how Fleet sets it up. Both install modes use OpenCode's own V2 installer;
/// Fleet never downloads it itself. Separate mode is for machines with OpenCode 1: both versions install as
/// <c>~/.opencode/bin/opencode</c>, and they'd share one database, which V2 migrates in place.
/// <para>
/// The mode is decided when Fleet first finds a working V2 and written to <c>~/.weave/harnesses/opencode2/install-mode</c>.
/// After that it doesn't change: an OpenCode 1 installed later mustn't move V2's sessions to another database.
/// Until then, a machine with an OpenCode 1 gets separate mode.
/// </para>
/// </summary>
internal sealed partial class OpenCode2Install
{
    public const string InstallerUrl = "https://opencode.ai/v2/install";
    public const string DocsUrl = "https://opencode.ai/v2/docs";

    private const string OpenCode1Command = "opencode";

    private readonly string _home;
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyList<string> _userBinDirectories;
    private readonly Func<string, CancellationToken, Task<HarnessAvailability>> _probe;
    private readonly bool _windows;

    /// <param name="home">The user's home folder.</param>
    /// <param name="environment">Reads an environment variable (<c>PATH</c>, <c>XDG_CONFIG_HOME</c>, <c>XDG_DATA_HOME</c>).</param>
    /// <param name="userBinDirectories">Other folders to look for executables in, after <c>PATH</c> (<see cref="ExecutableResolver.UserBinDirectories"/>).</param>
    /// <param name="probe">Runs an executable's <c>--version</c> (<see cref="HarnessProbe"/>).</param>
    /// <param name="windows">Whether this is Windows, where V2's installer doesn't run.</param>
    public OpenCode2Install(
        string home,
        Func<string, string?> environment,
        IEnumerable<string> userBinDirectories,
        Func<string, CancellationToken, Task<HarnessAvailability>> probe,
        bool windows)
    {
        _home = home;
        _environment = environment;
        _userBinDirectories = [.. userBinDirectories];
        _probe = probe;
        _windows = windows;
    }

    /// <summary><c>~/.weave/harnesses/opencode2</c>: the separate install's HOME, config folder and database, and the remembered mode.</summary>
    public static string RootFor(string home) => Path.Combine(home, ".weave", "harnesses", "opencode2");

    /// <summary>The config folder a separate install reads instead of <c>~/.config/opencode</c>.</summary>
    public static string ConfigDirectoryFor(string home) => Path.Combine(RootFor(home), "config");

    public string Root => RootFor(_home);

    /// <summary>Where the installer puts V2 when its HOME is <see cref="Root"/>.</summary>
    public string SeparateBin => Path.Combine(Root, ".opencode", "bin");

    /// <summary>Where the installer puts V2 by default, and OpenCode 1's installer puts OpenCode 1.</summary>
    public string DefaultBin => Path.Combine(_home, ".opencode", "bin");

    public string SeparateConfigDirectory => ConfigDirectoryFor(_home);

    public string SeparateDatabase => Path.Combine(Root, "data", "opencode.db");

    private string ModeFile => Path.Combine(Root, "install-mode");

    private string ConfigHome => XdgFolder("XDG_CONFIG_HOME", ".config");

    private string DataHome => XdgFolder("XDG_DATA_HOME", Path.Combine(".local", "share"));

    /// <summary>The mode written when Fleet first found a working V2, if it has.</summary>
    public OpenCode2InstallMode? RememberedMode()
    {
        try
        {
            return File.Exists(ModeFile) ? ParseMode(File.ReadAllText(ModeFile).Trim()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the mode down. A write that fails is tried again on the next check.</summary>
    public void Remember(OpenCode2InstallMode mode)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ModeFile, ModeName(mode));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered yet; the install itself still says where it is.
        }
    }

    /// <summary>
    /// The executable of the install in <paramref name="mode"/>. Separate mode only uses its own folder; default mode
    /// looks on <c>PATH</c>, in <c>~/.opencode/bin</c>, then in the usual user folders (a V2 the user installed another
    /// way is used as it is).
    /// </summary>
    public string? Find(OpenCode2InstallMode mode) => mode switch
    {
        OpenCode2InstallMode.Separate => ExecutableResolver.TryResolve(OpenCode2Executable.Command, pathEnv: null, [SeparateBin], out var separate)
            ? separate
            : null,
        _ => ExecutableResolver.TryResolve(OpenCode2Executable.Command, _environment("PATH"), [DefaultBin, .. _userBinDirectories], out var found)
            ? found
            : null,
    };

    /// <summary>
    /// The install to start a server from, without running anything: the remembered mode's executable, else a separate
    /// install, else a default one. <see langword="null"/> when there's none.
    /// </summary>
    public (OpenCode2InstallMode Mode, string ExecutablePath)? Locate()
    {
        if (RememberedMode() is { } remembered)
            return Find(remembered) is { } path ? (remembered, path) : null;

        foreach (var mode in (OpenCode2InstallMode[])[OpenCode2InstallMode.Separate, OpenCode2InstallMode.Default])
        {
            if (Find(mode) is { } path)
                return (mode, path);
        }
        return null;
    }

    /// <summary>
    /// Checks the install. With a remembered mode, only that mode's executable counts. Otherwise the first working
    /// V2 (separate, then default) decides the mode and is remembered; with none, the mode is separate when an
    /// OpenCode 1 is installed and default when not.
    /// </summary>
    public async Task<OpenCode2InstallCheck> CheckAsync(CancellationToken ct)
    {
        if (RememberedMode() is { } remembered)
        {
            var path = Find(remembered);
            var availability = path is null
                ? NotInstalled(remembered)
                : Explain(remembered, OpenCode2Executable.RequireOpenCode2(await _probe(path, ct).ConfigureAwait(false)));
            return new OpenCode2InstallCheck(remembered, Remembered: true, availability);
        }

        var failures = new Dictionary<OpenCode2InstallMode, HarnessAvailability>();
        foreach (var mode in (OpenCode2InstallMode[])[OpenCode2InstallMode.Separate, OpenCode2InstallMode.Default])
        {
            if (Find(mode) is not { } path)
                continue;
            var availability = OpenCode2Executable.RequireOpenCode2(await _probe(path, ct).ConfigureAwait(false));
            if (availability.Available)
            {
                Remember(mode);
                return new OpenCode2InstallCheck(mode, Remembered: true, availability);
            }
            failures[mode] = availability;
        }

        var choice = await IsOpenCode1InstalledAsync(ct).ConfigureAwait(false) ? OpenCode2InstallMode.Separate : OpenCode2InstallMode.Default;
        return new OpenCode2InstallCheck(choice, Remembered: false, failures.GetValueOrDefault(choice) ?? NotInstalled(choice));
    }

    /// <summary>The environment Fleet starts the server with, after making the separate install's folders.</summary>
    public IReadOnlyDictionary<string, string> PrepareEnvironment(OpenCode2InstallMode mode)
    {
        if (mode != OpenCode2InstallMode.Separate)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        Directory.CreateDirectory(SeparateConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SeparateDatabase)!);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG_DIR"] = SeparateConfigDirectory,
            ["OPENCODE_DB"] = SeparateDatabase,
        };
    }

    /// <summary>What setup offers for <paramref name="check"/>: the installer to type, sign-in, the folders and what to know.</summary>
    public HarnessSetup Setup(OpenCode2InstallCheck check)
    {
        var mode = check.Mode;
        var executable = check.Availability.ExecutablePath;
        var installed = executable is not null;
        var bin = mode == OpenCode2InstallMode.Separate ? SeparateBin : DefaultBin;
        var notes = new List<string>();

        if (mode == OpenCode2InstallMode.Separate)
        {
            notes.Add(installed
                ? "Installed next to OpenCode 1 in a folder of its own. OpenCode 1's program, settings and sessions are left alone."
                : $"OpenCode 1 is installed here, so OpenCode 2 goes into a folder of its own ({Root}). OpenCode 1's program, settings and sessions are left alone.");
            notes.Add("OpenCode 2 keeps its own provider sign-ins here. Sign in to your providers for it separately; OpenCode 1's sign-ins don't carry over.");
            notes.Add("Both versions still read a repository's opencode.json and .opencode folder.");
        }
        else if (check.Remembered && check.Availability.State == HarnessStates.NotWorking && IsUnder(executable, DefaultBin))
        {
            notes.Add($"OpenCode 1 and OpenCode 2 both install to {DefaultBin}. Installing OpenCode 2 again replaces OpenCode 1 there.");
        }

        if (_windows)
        {
            notes.Add($"OpenCode 2's installer needs bash, and Windows package managers aren't supported yet. Download the Windows build and save opencode.exe as {Path.Combine(bin, "opencode2.exe")}.");
        }

        return new HarnessSetup(
            InstallCommand: InstallCommand(mode),
            SignInCommand: SignInCommand(mode, executable ?? Path.Combine(bin, OpenCode2Executable.Command)),
            DocsUrl: DocsUrl)
        {
            DownloadUrl = _windows ? DocsUrl : null,
            Mode = mode == OpenCode2InstallMode.Separate ? "Separate from OpenCode 1" : "Default install",
            Folders = Folders(mode, executable ?? bin),
            Notes = notes,
        };
    }

    /// <summary>V2's installer as the user types it; <see langword="null"/> on Windows, where it doesn't run.</summary>
    public string? InstallCommand(OpenCode2InstallMode mode)
    {
        if (_windows)
            return null;
        return mode == OpenCode2InstallMode.Separate
            ? $"curl -fsSL {InstallerUrl} | HOME={Word(Root)} bash -s -- --no-modify-path"
            : $"curl -fsSL {InstallerUrl} | bash";
    }

    /// <summary>
    /// Runs the installer again for the install at <paramref name="executablePath"/>, in its mode and with its HOME,
    /// and without touching shell files (the first install did that). <see langword="null"/> for a V2 the installer
    /// didn't put there (npm, Homebrew), and on Windows.
    /// </summary>
    public HarnessCommand? UpdateCommand(string executablePath, string? version)
    {
        if (_windows)
            return null;

        string? home = IsUnder(executablePath, SeparateBin) ? Word(Root)
            : IsUnder(executablePath, DefaultBin) ? string.Empty
            : null;
        if (home is null)
            return null;

        var pin = version is not null && SafeVersion().IsMatch(version) ? $" --version {version}" : string.Empty;
        var display = home.Length > 0
            ? $"curl -fsSL {InstallerUrl} | HOME={home} bash -s -- --no-modify-path{pin}"
            : $"curl -fsSL {InstallerUrl} | bash -s -- --no-modify-path{pin}";
        return new HarnessCommand(ExecutableResolver.Resolve("bash"), ["-c", display], display);
    }

    /// <summary>
    /// V2's provider sign-in. A separate install reads its own config folder and database, so the command names them,
    /// and runs with <c>--standalone</c>: V2's CLI otherwise goes through one background service per machine (a fixed
    /// port), which may be running on the other database and would keep the sign-in.
    /// </summary>
    public string SignInCommand(OpenCode2InstallMode mode, string executablePath)
    {
        var executable = ShellCommand.Executable(executablePath, _windows);
        if (mode != OpenCode2InstallMode.Separate)
            return $"{executable} auth login";

        return _windows
            ? $"$env:OPENCODE_CONFIG_DIR='{SeparateConfigDirectory}'; $env:OPENCODE_DB='{SeparateDatabase}'; {executable} auth login --standalone"
            : $"OPENCODE_CONFIG_DIR={Word(SeparateConfigDirectory)} OPENCODE_DB={Word(SeparateDatabase)} {executable} auth login --standalone";
    }

    private IReadOnlyList<HarnessFolder> Folders(OpenCode2InstallMode mode, string program)
    {
        var logs = Path.Combine(DataHome, "opencode", "log");
        return mode == OpenCode2InstallMode.Separate
            ?
            [
                new("Program", program),
                new("Settings", SeparateConfigDirectory),
                new("Sessions", SeparateDatabase),
                new("Logs, shell output, snapshots (shared with OpenCode 1)", Path.Combine(DataHome, "opencode")),
            ]
            :
            [
                new("Program", program),
                new("Settings", Path.Combine(ConfigHome, "opencode")),
                new("Sessions", Path.Combine(DataHome, "opencode", "opencode.db")),
                new("Logs", logs),
            ];
    }

    private HarnessAvailability NotInstalled(OpenCode2InstallMode mode) => HarnessAvailability.NotInstalled(mode == OpenCode2InstallMode.Separate
        ? $"OpenCode 2 isn't installed: Fleet couldn't find it in {SeparateBin}, where it goes next to OpenCode 1."
        : $"OpenCode 2 isn't installed: Fleet couldn't find {OpenCode2Executable.Command} on PATH or in {DefaultBin}.");

    /// <summary>A default install that runs OpenCode 1 now was most likely replaced by OpenCode 1's installer.</summary>
    private HarnessAvailability Explain(OpenCode2InstallMode mode, HarnessAvailability availability)
    {
        if (mode != OpenCode2InstallMode.Default
            || availability.Version is not { } version
            || OpenCode2Executable.IsOpenCode2(version)
            || !IsUnder(availability.ExecutablePath, DefaultBin))
            return availability;

        return availability with
        {
            Reason = $"{availability.ExecutablePath} runs OpenCode {version} now: OpenCode 1's installer replaced OpenCode 2 in {DefaultBin}, where both install.",
        };
    }

    /// <summary>
    /// Whether an OpenCode 1 is installed: an <c>opencode</c> that isn't OpenCode 2. One that won't say its version
    /// counts too, so V2's installer never overwrites it.
    /// </summary>
    private async Task<bool> IsOpenCode1InstalledAsync(CancellationToken ct)
    {
        if (!ExecutableResolver.TryResolve(OpenCode1Command, _environment("PATH"), [DefaultBin, .. _userBinDirectories], out var path))
            return false;
        var availability = await _probe(path, ct).ConfigureAwait(false);
        return availability.Version is not { } version || !OpenCode2Executable.IsOpenCode2(version);
    }

    private string XdgFolder(string variable, string fallback)
    {
        var value = _environment(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : Path.Combine(_home, fallback);
    }

    private string Word(string value) => ShellCommand.Executable(value, _windows);

    private static bool IsUnder(string? path, string folder)
        => path is not null && string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(path) ?? path).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static OpenCode2InstallMode? ParseMode(string text) => text switch
    {
        "default" => OpenCode2InstallMode.Default,
        "separate" => OpenCode2InstallMode.Separate,
        _ => null,
    };

    private static string ModeName(OpenCode2InstallMode mode) => mode == OpenCode2InstallMode.Separate ? "separate" : "default";

    [GeneratedRegex(@"^[0-9A-Za-z.+-]+$")]
    private static partial Regex SafeVersion();
}
