#:project _build/WeaveFleetBuild.csproj

using static Bullseye.Targets;
using static SimpleExec.Command;

const string Restore = "restore";
const string Build = "build";
const string Clean = "clean";
const string Test = "test";
const string TestE2E = "test-e2e";
const string CheckFormatting = "check-formatting";

const string Slnx = "WeaveFleet.slnx";

string[] unitTestProjects =
[
    "tests/WeaveFleet.Api.Tests",
    "tests/WeaveFleet.Application.Tests",
    "tests/WeaveFleet.Domain.Tests",
    "tests/WeaveFleet.Infrastructure.Tests",
    "tests/WeaveFleet.TestHarness.Tests",
];

Target(Clean, () =>
    RunAsync("dotnet", $"clean {Slnx} -c Release"));

Target(Restore, () =>
    RunAsync("dotnet", $"restore {Slnx}"));

Target(Build, dependsOn: [Restore], () =>
    RunAsync("dotnet", $"build {Slnx} -c Release --no-restore"));

Target(Test, dependsOn: [Build], async () =>
{
    foreach (var project in unitTestProjects)
    {
        var trxName = project.Replace('/', '-') + "-tests.trx";
        await RunAsync("dotnet",
            $"test {project} -c Release --no-build --logger \"trx;LogFileName={trxName}\"");
    }
});

Target(TestE2E, dependsOn: [Build], () =>
    RunAsync("dotnet", "test tests/WeaveFleet.E2E -c Release --no-build"));

Target(CheckFormatting, dependsOn: [Restore], () =>
    RunAsync("dotnet", $"format {Slnx} --verify-no-changes --no-restore"));

Target("default", dependsOn: [Build, Test]);

await RunTargetsAndExitAsync(args, messageOnly: ex => ex is SimpleExec.ExitCodeException);
