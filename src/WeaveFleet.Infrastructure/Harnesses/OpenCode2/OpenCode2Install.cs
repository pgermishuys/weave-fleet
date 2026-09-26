using System.Collections.Concurrent;
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
internal sealed record OpenCode2InstallCheck(OpenCode2InstallMode Mode, bool Remembered, HarnessAvailability Availability)
{
    /// <summary>
    /// Where the user can install it, the recommended place first: set while there's no working install to keep, and
    /// empty once there is (or when a remembered separate install is gone, which is installed again in its place).
    /// </summary>
    public IReadOnlyList<OpenCode2InstallMode> Choices { get; init; } = [];

    /// <summary>The OpenCode 1 found while working out <see cref="Choices"/>, which the default place may replace.</summary>
    public string? OpenCode1Path { get; init; }
}

/// <summary>
/// Where OpenCode 2 lives on this machine, and how Fleet sets it up. Both install modes use OpenCode's own V2 installer;
/// Fleet never downloads it itself. Separate mode is for machines with OpenCode 1: both versions install as
/// <c>~/.opencode/bin/opencode</c>, and they'd share one database, which V2 migrates in place.
/// <para>
/// Until V2 is installed the user picks where it goes: its own folder (recommended, since OpenCode 1 can then be
/// installed at any time) or the default place, as V2's installer does when run as it is, which replaces an OpenCode 1
/// there. Each choice says what it does to the OpenCode 1 Fleet found.
/// The mode is decided when Fleet first finds a working V2 and written to <c>~/.weave/harnesses/opencode2/install-mode</c>.
/// After that it doesn't change while that install works: an OpenCode 1 installed later mustn't move V2's sessions to
/// another database. A default install that stops working (OpenCode 1's installer replaces it) gives way to a separate
/// one, so the user can have both.
/// </para>
/// </summary>
internal sealed partial class OpenCode2Install
{
    public const string InstallerUrl = "https://opencode.ai/v2/install";
    public const string DocsUrl = "https://opencode.ai/v2/docs";

    private readonly string _home;
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyList<string> _userBinDirectories;
    private readonly Func<string, CancellationToken, Task<HarnessAvailability>> _probe;
    private readonly bool _windows;

    // Whether each executable was OpenCode 2 when last run. Locate doesn't run anything, and a plain opencode may be
    // OpenCode 1, so it only takes one a check found to be 2.x.
    private readonly ConcurrentDictionary<string, bool> _isOpenCode2 =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

    /// <summary>The config folder V2 reads for the user in <paramref name="mode"/>: a separate install's own, else OpenCode's.</summary>
    public string UserConfigDirectory(OpenCode2InstallMode mode)
        => mode == OpenCode2InstallMode.Separate ? SeparateConfigDirectory : Path.Combine(ConfigHome, "opencode");

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
    /// Every executable that may be the install in <paramref name="mode"/>, in the order they're tried: each
    /// <c>opencode2</c>, then each <c>opencode</c>. Separate mode only uses its own folder; default mode looks on
    /// <c>PATH</c>, in <c>~/.opencode/bin</c>, then in the usual user folders (a V2 the user installed another way, such
    /// as Homebrew or npm, is used as it is). Which of them is OpenCode 2 is down to the version it reports.
    /// </summary>
    private IEnumerable<string> Candidates(OpenCode2InstallMode mode)
    {
        var (pathEnv, directories) = mode == OpenCode2InstallMode.Separate
            ? (null, [SeparateBin])
            : (_environment("PATH"), (IReadOnlyList<string>)[DefaultBin, .. _userBinDirectories]);
        return ((string[])[OpenCode2Executable.Command, OpenCode2Executable.PlainCommand])
            .SelectMany(name => ExecutableResolver.FindAll(name, pathEnv, directories));
    }

    /// <summary>
    /// The executable of the install in <paramref name="mode"/>, without running anything: the first one the last check
    /// found to be OpenCode 2, or an <c>opencode2</c> not checked yet. A plain <c>opencode</c> only counts once a check
    /// has seen it report 2.x.
    /// </summary>
    public string? Find(OpenCode2InstallMode mode) => Candidates(mode).FirstOrDefault(path =>
        _isOpenCode2.TryGetValue(path, out var isOpenCode2)
            ? isOpenCode2
            : OpenCode2Executable.Name(path) == OpenCode2Executable.Command);

    /// <summary>
    /// Runs the executables of the install in <paramref name="mode"/> until one is OpenCode 2. With none, the first
    /// one's state, so a V2 that OpenCode 1's installer replaced says so; <see langword="null"/> when there are none.
    /// </summary>
    private async Task<HarnessAvailability?> ProbeAsync(OpenCode2InstallMode mode, CancellationToken ct)
    {
        HarnessAvailability? first = null;
        foreach (var path in Candidates(mode))
        {
            var availability = OpenCode2Executable.RequireOpenCode2(await _probe(path, ct).ConfigureAwait(false));
            _isOpenCode2[path] = availability.Available;
            if (availability.Available)
                return availability;
            first ??= availability;
        }
        return first;
    }

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
    /// <see cref="Locate"/>, or when that finds nothing, a check: a plain <c>opencode</c> only counts once it's been run,
    /// and a server may be wanted before anything has checked the install (an automation just after Fleet starts).
    /// </summary>
    public async Task<(OpenCode2InstallMode Mode, string ExecutablePath)?> LocateAsync(CancellationToken ct)
    {
        if (Locate() is { } located)
            return located;
        var check = await CheckAsync(ct).ConfigureAwait(false);
        return check.Availability is { Available: true, ExecutablePath: { } path } ? (check.Mode, path) : null;
    }

    /// <summary>
    /// Checks the install. With a remembered mode, only that mode's executable counts (a default one that stopped
    /// working gives way to a working separate one). Otherwise the first working V2 (separate, then default) decides the
    /// mode and is remembered; with none, the user picks, and the mode is separate until they do.
    /// </summary>
    public async Task<OpenCode2InstallCheck> CheckAsync(CancellationToken ct)
    {
        if (RememberedMode() is { } remembered)
        {
            var availability = await ProbeAsync(remembered, ct).ConfigureAwait(false) is { } probed
                ? Explain(remembered, probed)
                : NotInstalled(remembered);
            if (availability.Available || remembered == OpenCode2InstallMode.Separate)
                return new OpenCode2InstallCheck(remembered, Remembered: true, availability);

            // The default install is gone or runs OpenCode 1 now: one the user has since put in its own folder takes over.
            if (await ProbeAsync(OpenCode2InstallMode.Separate, ct).ConfigureAwait(false) is { Available: true } separate)
            {
                Remember(OpenCode2InstallMode.Separate);
                return new OpenCode2InstallCheck(OpenCode2InstallMode.Separate, Remembered: true, separate);
            }
            return new OpenCode2InstallCheck(remembered, Remembered: true, availability)
            {
                Choices = AllChoices,
                OpenCode1Path = await FindOpenCode1Async(ct).ConfigureAwait(false),
            };
        }

        var failures = new Dictionary<OpenCode2InstallMode, HarnessAvailability>();
        foreach (var mode in (OpenCode2InstallMode[])[OpenCode2InstallMode.Separate, OpenCode2InstallMode.Default])
        {
            if (await ProbeAsync(mode, ct).ConfigureAwait(false) is not { } availability)
                continue;
            if (availability.Available)
            {
                Remember(mode);
                return new OpenCode2InstallCheck(mode, Remembered: true, availability);
            }
            failures[mode] = availability;
        }

        var notInstalled = HarnessAvailability.NotInstalled(
            $"OpenCode 2 isn't installed: Fleet couldn't find an opencode2, or an opencode that's version 2.x, in a folder of its own ({SeparateBin}), on PATH or in {DefaultBin}.");
        return new OpenCode2InstallCheck(AllChoices[0], Remembered: false, failures.GetValueOrDefault(AllChoices[0]) ?? notInstalled)
        {
            Choices = AllChoices,
            OpenCode1Path = await FindOpenCode1Async(ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Where V2 can go: its own folder, recommended since OpenCode 1 can be installed next to it at any time, or the
    /// default place, where both OpenCode 1's installer and V2's write <c>opencode</c>.
    /// </summary>
    private static readonly IReadOnlyList<OpenCode2InstallMode> AllChoices = [OpenCode2InstallMode.Separate, OpenCode2InstallMode.Default];

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
        // Until it's installed, the setup is for the place it would go: the recommended one when there's a choice.
        var mode = check.Choices.Count > 0 ? check.Choices[0] : check.Mode;
        var executable = mode == check.Mode ? check.Availability.ExecutablePath : null;
        var installed = executable is not null;
        var bin = mode == OpenCode2InstallMode.Separate ? SeparateBin : DefaultBin;
        var notes = new List<string>();

        // OpenCode 1's installer replaced the default install; putting V2 back there would replace OpenCode 1 in turn.
        if (check.Remembered && check.Mode == OpenCode2InstallMode.Default && check.Availability.State == HarnessStates.NotWorking)
        {
            notes.Add($"To have both versions, install OpenCode 2 again in a folder of its own. Sessions from the OpenCode 2 in {DefaultBin} stay in {Path.Combine(DataHome, "opencode", "opencode.db")}; the new install starts with its own.");
        }

        // With a choice to make, each choice says what it means.
        var picking = !_windows && check.Choices.Count > 1;
        if (mode == OpenCode2InstallMode.Separate && !picking)
        {
            notes.Add(installed
                ? "Installed in a folder of its own. OpenCode 1's program, settings and sessions are left alone."
                : $"OpenCode 2 goes into a folder of its own ({Root}). OpenCode 1's program, settings and sessions are left alone.");
            notes.Add(SeparateSignInNote);
            notes.Add("Both versions still read a repository's opencode.json and .opencode folder.");
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
            Mode = ModeLabel(mode),
            Folders = Folders(mode, executable ?? bin),
            Notes = notes,
            InstallChoices = picking ? [.. check.Choices.Select(choice => Choice(choice, check.OpenCode1Path))] : [],
        };
    }

    private const string SeparateSignInNote =
        "OpenCode 2 keeps its own provider sign-ins here. Sign in to your providers for it separately; OpenCode 1's sign-ins don't carry over.";

    private static string ModeLabel(OpenCode2InstallMode mode) =>
        mode == OpenCode2InstallMode.Separate ? "In its own folder" : "As your main opencode";

    /// <summary>
    /// A place to install V2, with what picking it means for the OpenCode 1 at <paramref name="openCode1"/>, if there is
    /// one. Separate mode is the recommended one.
    /// </summary>
    private HarnessInstallChoice Choice(OpenCode2InstallMode mode, string? openCode1)
    {
        if (mode == OpenCode2InstallMode.Separate)
        {
            var description = openCode1 is null
                ? $"Fleet runs it from {Root}, with its own settings and sessions, and keeps its own provider sign-ins. " +
                  $"{DefaultBin} stays free, so OpenCode 1 can be installed next to it at any time. It isn't added to your PATH."
                : $"Next to OpenCode 1, which is left alone: Fleet runs OpenCode 2 from {Root}, with its own settings and sessions. " +
                  "It keeps its own provider sign-ins, so OpenCode 1's don't carry over. It isn't added to your PATH.";
            return new HarnessInstallChoice(ModeName(mode), ModeLabel(mode), description, InstallCommand(mode)!, Folders(mode, SeparateBin))
            {
                Recommended = true,
            };
        }

        var main = openCode1 switch
        {
            null => $"OpenCode 2's usual install: {DefaultBin}, added to your PATH, with OpenCode's usual settings and sessions. " +
                    "OpenCode 1 installs to the same folder, so installing OpenCode 1 later replaces OpenCode 2.",
            _ when IsUnder(openCode1, DefaultBin) =>
                $"Replaces OpenCode 1 at {openCode1}, as OpenCode 2's installer does when you run it yourself. OpenCode 2 then uses " +
                "OpenCode 1's settings and sessions, and Fleet's OpenCode harness stops working until OpenCode 1 is installed again, " +
                "which replaces OpenCode 2 in turn.",
            _ => $"OpenCode 2's usual install: {DefaultBin}, added to your PATH, with OpenCode's usual settings and sessions. " +
                 $"OpenCode 1 at {openCode1} stays, but both are called opencode: the one first on your PATH is the one you and " +
                 "Fleet's OpenCode harness get.",
        };
        return new HarnessInstallChoice(ModeName(mode), ModeLabel(mode), main, InstallCommand(mode)!, Folders(mode, DefaultBin));
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
        ? $"OpenCode 2 isn't installed: Fleet couldn't find it in its own folder ({SeparateBin})."
        : $"OpenCode 2 isn't installed: Fleet couldn't find {OpenCode2Executable.Command} or {OpenCode2Executable.PlainCommand} on PATH or in {DefaultBin}.");

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
    /// The OpenCode 1 installed here, if there is one: an <c>opencode</c> that isn't OpenCode 2. One that won't say its
    /// version counts too, so the user is told before V2's installer overwrites it.
    /// </summary>
    private async Task<string?> FindOpenCode1Async(CancellationToken ct)
    {
        if (!ExecutableResolver.TryResolve(OpenCode2Executable.PlainCommand, _environment("PATH"), [DefaultBin, .. _userBinDirectories], out var path))
            return null;
        var availability = await _probe(path, ct).ConfigureAwait(false);
        return availability.Version is not { } version || !OpenCode2Executable.IsOpenCode2(version) ? path : null;
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
