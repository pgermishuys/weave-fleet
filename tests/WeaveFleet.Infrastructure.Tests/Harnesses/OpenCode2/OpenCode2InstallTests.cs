using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Install modes. Executables are files holding the version they report, so no process runs; the probe reads them.
/// </summary>
public sealed class OpenCode2InstallTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("fleet-oc2-install-").FullName;
    private readonly string _path;
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);

    public OpenCode2InstallTests()
    {
        // An npm-style folder on PATH, away from the installers' ~/.opencode/bin.
        _path = Path.Combine(_home, "npm", "bin");
        Directory.CreateDirectory(_path);
        _environment["PATH"] = _path;
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Root => Path.Combine(_home, ".weave", "harnesses", "opencode2");

    private string SeparateBin => Path.Combine(Root, ".opencode", "bin");

    private string DefaultBin => Path.Combine(_home, ".opencode", "bin");

    [Fact]
    public async Task Without_OpenCode_1_the_user_picks_its_own_folder_or_the_default_place()
    {
        var install = Install();

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Choices.ShouldBe([OpenCode2InstallMode.Separate, OpenCode2InstallMode.Default]);
        check.Remembered.ShouldBeFalse();
        check.Availability.State.ShouldBe(HarnessStates.NotInstalled);
        check.Availability.Reason.ShouldBe(
            $"OpenCode 2 isn't installed: Fleet couldn't find it in a folder of its own ({SeparateBin}), on PATH or in {DefaultBin}.");
        var setup = install.Setup(check);
        setup.InstallCommand.ShouldBe($"curl -fsSL https://opencode.ai/v2/install | HOME={Root} bash -s -- --no-modify-path");
        setup.Notes.ShouldBeEmpty();
        setup.InstallChoices.Select(choice => (choice.Id, choice.Label, choice.Command, choice.Recommended)).ShouldBe(
        [
            ("separate", "In its own folder", $"curl -fsSL https://opencode.ai/v2/install | HOME={Root} bash -s -- --no-modify-path", true),
            ("default", "As your main opencode", "curl -fsSL https://opencode.ai/v2/install | bash", false),
        ]);
        setup.InstallChoices[0].Description.ShouldContain("OpenCode 1 can be installed next to it at any time");
        setup.InstallChoices[0].Folders[0].ShouldBe(new HarnessFolder("Program", SeparateBin));
        setup.InstallChoices[1].Description.ShouldContain("installing OpenCode 1 later replaces OpenCode 2");
        setup.InstallChoices[1].Folders[0].ShouldBe(new HarnessFolder("Program", DefaultBin));
    }

    [Fact]
    public async Task Whichever_place_the_user_picked_is_found_and_remembered()
    {
        var executable = Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Default);
        check.Availability.ExecutablePath.ShouldBe(executable);
        check.Choices.ShouldBeEmpty();
        install.Setup(check).InstallChoices.ShouldBeEmpty();
        install.RememberedMode().ShouldBe(OpenCode2InstallMode.Default);
    }

    [Fact]
    public async Task With_OpenCode_1_installed_OpenCode_2_goes_into_a_folder_of_its_own()
    {
        Executable(DefaultBin, "opencode", "1.18.31");
        var install = Install();

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Choices.ShouldBe([OpenCode2InstallMode.Separate]);
        check.Remembered.ShouldBeFalse();
        check.Availability.Reason.ShouldBe($"OpenCode 2 isn't installed: Fleet couldn't find it in {SeparateBin}, where it goes next to OpenCode 1.");
        var setup = install.Setup(check);
        setup.InstallCommand.ShouldBe($"curl -fsSL https://opencode.ai/v2/install | HOME={Root} bash -s -- --no-modify-path");
        setup.InstallChoices.ShouldBeEmpty();
        setup.Mode.ShouldBe("In its own folder");
        setup.Notes!.ShouldContain(note => note.Contains("provider sign-ins"));
        setup.Notes!.ShouldContain("Both versions still read a repository's opencode.json and .opencode folder.");
    }

    [Fact]
    public async Task An_opencode_that_is_OpenCode_2_is_not_OpenCode_1()
    {
        Executable(_path, "opencode", "opencode v2.0.9");

        (await Install().CheckAsync(CancellationToken.None)).Choices.ShouldContain(OpenCode2InstallMode.Default);
    }

    [Fact]
    public async Task An_opencode_that_wont_say_its_version_counts_as_OpenCode_1()
    {
        Executable(_path, "opencode", "");

        (await Install().CheckAsync(CancellationToken.None)).Choices.ShouldBe([OpenCode2InstallMode.Separate]);
    }

    [Fact]
    public async Task A_separate_install_is_found_ready_and_remembered()
    {
        Executable(DefaultBin, "opencode", "1.18.31");
        var executable = Executable(SeparateBin, "opencode2", "opencode v2.0.9");
        var install = Install();

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Remembered.ShouldBeTrue();
        check.Availability.Available.ShouldBeTrue();
        check.Availability.ExecutablePath.ShouldBe(executable);
        install.RememberedMode().ShouldBe(OpenCode2InstallMode.Separate);
        File.ReadAllText(Path.Combine(Root, "install-mode")).ShouldBe("separate");
    }

    [Fact]
    public async Task A_default_install_stays_default_when_OpenCode_1_is_installed_later()
    {
        var executable = Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();
        (await install.CheckAsync(CancellationToken.None)).Mode.ShouldBe(OpenCode2InstallMode.Default);

        Executable(_path, "opencode", "1.18.31");
        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Default);
        check.Availability.Available.ShouldBeTrue();
        check.Availability.ExecutablePath.ShouldBe(executable);
        install.PrepareEnvironment(check.Mode).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_default_install_that_OpenCode_1s_installer_replaced_offers_its_own_folder()
    {
        var executable = Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();
        await install.CheckAsync(CancellationToken.None);

        // OpenCode 1's installer writes ~/.opencode/bin/opencode, which V2's opencode2 shim runs.
        File.WriteAllText(executable, "1.18.31");
        Executable(DefaultBin, "opencode", "1.18.31");
        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Default);
        check.Availability.State.ShouldBe(HarnessStates.NotWorking);
        check.Availability.Reason.ShouldBe(
            $"{executable} runs OpenCode 1.18.31 now: OpenCode 1's installer replaced OpenCode 2 in {DefaultBin}, where both install.");
        // Installing it in the default place again would replace OpenCode 1 in turn.
        check.Choices.ShouldBe([OpenCode2InstallMode.Separate]);
        var setup = install.Setup(check);
        setup.InstallCommand.ShouldBe($"curl -fsSL https://opencode.ai/v2/install | HOME={Root} bash -s -- --no-modify-path");
        setup.Mode.ShouldBe("In its own folder");
        setup.Folders![0].ShouldBe(new HarnessFolder("Program", SeparateBin));
        setup.Notes![0].ShouldStartWith("To have both versions, install OpenCode 2 again in a folder of its own.");
    }

    [Fact]
    public async Task A_separate_install_takes_over_from_a_default_one_OpenCode_1s_installer_replaced()
    {
        var replaced = Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();
        await install.CheckAsync(CancellationToken.None);
        File.WriteAllText(replaced, "1.18.31");
        Executable(DefaultBin, "opencode", "1.18.31");

        var separate = Executable(SeparateBin, "opencode2", "opencode v2.0.9");
        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Availability.Available.ShouldBeTrue();
        check.Availability.ExecutablePath.ShouldBe(separate);
        install.RememberedMode().ShouldBe(OpenCode2InstallMode.Separate);
        install.Locate().ShouldBe((OpenCode2InstallMode.Separate, separate));
    }

    [Fact]
    public async Task A_working_default_install_isnt_replaced_by_a_separate_one()
    {
        var executable = Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();
        await install.CheckAsync(CancellationToken.None);

        Executable(SeparateBin, "opencode2", "opencode v2.0.9");
        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Default);
        check.Availability.ExecutablePath.ShouldBe(executable);
    }

    [Fact]
    public async Task A_remembered_separate_install_that_is_gone_isnt_replaced_by_a_default_one()
    {
        Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var install = Install();
        install.Remember(OpenCode2InstallMode.Separate);

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Availability.State.ShouldBe(HarnessStates.NotInstalled);
        install.Locate().ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_remembered_mode_a_separate_install_wins_over_a_default_one()
    {
        Executable(DefaultBin, "opencode2", "opencode v2.0.9");
        var separate = Executable(SeparateBin, "opencode2", "opencode v2.0.9");
        var install = Install();

        install.Locate().ShouldBe((OpenCode2InstallMode.Separate, separate));
        (await install.CheckAsync(CancellationToken.None)).Mode.ShouldBe(OpenCode2InstallMode.Separate);
    }

    [Fact]
    public async Task An_opencode2_that_runs_OpenCode_1_doesnt_decide_the_mode()
    {
        // The default folder's opencode2 shim runs OpenCode 1 after its installer; nothing was remembered yet.
        Executable(DefaultBin, "opencode2", "1.18.31");
        Executable(DefaultBin, "opencode", "1.18.31");
        var install = Install();

        var check = await install.CheckAsync(CancellationToken.None);

        check.Mode.ShouldBe(OpenCode2InstallMode.Separate);
        check.Availability.State.ShouldBe(HarnessStates.NotInstalled);
        install.RememberedMode().ShouldBeNull();
    }

    [Fact]
    public void A_separate_server_gets_its_own_config_folder_and_database()
    {
        var environment = Install().PrepareEnvironment(OpenCode2InstallMode.Separate);

        environment.ShouldBe(new Dictionary<string, string>
        {
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(Root, "config"),
            ["OPENCODE_DB"] = Path.Combine(Root, "data", "opencode.db"),
        });
        Directory.Exists(Path.Combine(Root, "config")).ShouldBeTrue();
        Directory.Exists(Path.Combine(Root, "data")).ShouldBeTrue();
    }

    [Fact]
    public void An_update_runs_the_installer_again_in_the_installs_mode()
    {
        var install = Install();

        var separate = install.UpdateCommand(Path.Combine(SeparateBin, "opencode2"), "2.0.9").ShouldNotBeNull();
        separate.Display.ShouldBe($"curl -fsSL https://opencode.ai/v2/install | HOME={Root} bash -s -- --no-modify-path --version 2.0.9");
        separate.Arguments.ShouldBe(["-c", separate.Display]);

        install.UpdateCommand(Path.Combine(DefaultBin, "opencode2"), null)!.Display
            .ShouldBe("curl -fsSL https://opencode.ai/v2/install | bash -s -- --no-modify-path");
    }

    [Fact]
    public void A_V2_the_installer_didnt_put_there_isnt_updated_by_Fleet()
        => Install().UpdateCommand(Path.Combine(_path, "opencode2"), "2.0.9").ShouldBeNull();

    [Fact]
    public void A_version_that_isnt_a_plain_version_isnt_passed_to_the_shell()
        => Install().UpdateCommand(Path.Combine(DefaultBin, "opencode2"), "2.0.9; rm -rf ~")!.Display
            .ShouldBe("curl -fsSL https://opencode.ai/v2/install | bash -s -- --no-modify-path");

    [Fact]
    public void A_path_with_spaces_is_quoted()
    {
        var home = Path.Combine(_home, "my home");
        var install = new OpenCode2Install(home, _environment.GetValueOrDefault, [], Probe, windows: false);

        install.InstallCommand(OpenCode2InstallMode.Separate)
            .ShouldBe($"curl -fsSL https://opencode.ai/v2/install | HOME='{home}/.weave/harnesses/opencode2' bash -s -- --no-modify-path");
    }

    [Fact]
    public void Signing_in_to_a_separate_install_names_its_config_folder_and_database()
    {
        var executable = Path.Combine(SeparateBin, "opencode2");

        Install().SignInCommand(OpenCode2InstallMode.Separate, executable).ShouldBe(
            $"OPENCODE_CONFIG_DIR={Root}/config OPENCODE_DB={Root}/data/opencode.db {executable} auth login --standalone");
        Install().SignInCommand(OpenCode2InstallMode.Default, "/usr/local/bin/opencode2").ShouldBe("/usr/local/bin/opencode2 auth login");
    }

    [Fact]
    public async Task The_folders_follow_XDG_in_default_mode()
    {
        var config = Path.Combine(_home, "xdg-config");
        var data = Path.Combine(_home, "xdg-data");
        _environment["XDG_CONFIG_HOME"] = config;
        _environment["XDG_DATA_HOME"] = data;
        var install = Install();

        var setup = install.Setup(await install.CheckAsync(CancellationToken.None));

        setup.InstallChoices[1].Folders.ShouldBe(
        [
            new HarnessFolder("Program", DefaultBin),
            new HarnessFolder("Settings", Path.Combine(config, "opencode")),
            new HarnessFolder("Sessions", Path.Combine(data, "opencode", "opencode.db")),
            new HarnessFolder("Logs", Path.Combine(data, "opencode", "log")),
        ]);
    }

    [Fact]
    public async Task Windows_gets_the_manual_download()
    {
        Executable(DefaultBin, "opencode", "1.18.31");
        var install = new OpenCode2Install(_home, _environment.GetValueOrDefault, [], Probe, windows: true);

        var setup = install.Setup(await install.CheckAsync(CancellationToken.None));

        setup.InstallCommand.ShouldBeNull();
        setup.InstallChoices.ShouldBeEmpty();
        setup.DownloadUrl.ShouldBe("https://opencode.ai/v2/docs");
        setup.Notes!.ShouldContain(note => note.Contains(Path.Combine(SeparateBin, "opencode2.exe")));
        install.UpdateCommand(Path.Combine(SeparateBin, "opencode2"), "2.0.9").ShouldBeNull();
    }

    private OpenCode2Install Install() => new(_home, _environment.GetValueOrDefault, [], Probe, windows: false);

    /// <summary>Reports the version written in the file, as <c>--version</c> would.</summary>
    private static Task<HarnessAvailability> Probe(string path, CancellationToken ct)
    {
        var text = File.ReadAllText(path).Trim();
        return Task.FromResult(text.Length == 0
            ? HarnessAvailability.Ready(null, path)
            : HarnessAvailability.Ready(text.Split(' ')[^1].TrimStart('v'), path));
    }

    private static string Executable(string folder, string name, string version)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(path, version);
        return path;
    }
}
