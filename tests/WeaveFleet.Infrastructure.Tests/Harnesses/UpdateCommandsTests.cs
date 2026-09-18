using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class UpdateCommandsTests
{
    [Theory]
    [InlineData(@"C:\Users\jo\AppData\Local\Microsoft\WinGet\Links\opencode.exe", true)]
    [InlineData(@"C:\Users\jo\AppData\Local\Microsoft\WinGet\Packages\SST.opencode_Microsoft.Winget.Source_8wekyb3d8bbwe\opencode.exe", true)]
    [InlineData(@"C:\Users\jo\.opencode\bin\opencode.exe", false)]
    [InlineData("/home/you/.opencode/bin/opencode", false)]
    public void Knows_a_winget_install_by_its_folder(string path, bool expected)
    {
        UpdateCommands.IsWingetInstall(path).ShouldBe(expected);
    }

    [Fact]
    public void Winget_upgrades_one_package_without_prompts_and_shows_the_short_form()
    {
        var command = UpdateCommands.Winget("SST.opencode");

        command.Arguments.ShouldBe(["upgrade", "--id", "SST.opencode", "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements"]);
        command.Display.ShouldBe("winget upgrade --id SST.opencode --exact");
    }

    [Fact]
    public void A_harness_that_is_too_old_needs_an_update_before_sessions_can_use_it()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var runtime = new FakeHarnessRuntime("opencode") { MinimumVersion = "1.15.10" };

        var old = HarnessRegistry.RequireMinimumVersion(harness, runtime, HarnessAvailability.Ready("1.12.0", "/home/you/.opencode/bin/opencode"));

        (old.Available, old.State, old.Version).ShouldBe((false, HarnessStates.UpdateNeeded, "1.12.0"));
        old.Reason.ShouldBe("Fleet needs OpenCode 1.15.10 or newer. You have 1.12.0.");

        HarnessRegistry.RequireMinimumVersion(harness, runtime, HarnessAvailability.Ready("1.15.10", "/x")).State.ShouldBe(HarnessStates.Ready);
        HarnessRegistry.RequireMinimumVersion(harness, runtime, HarnessAvailability.NotInstalled("Missing.")).State.ShouldBe(HarnessStates.NotInstalled);
    }
}
