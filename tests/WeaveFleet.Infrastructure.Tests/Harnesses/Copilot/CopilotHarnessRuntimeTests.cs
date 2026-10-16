using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotHarnessRuntimeTests
{
    private sealed class Preferences(Dictionary<string, string> values) : IUserPreferenceRepository
    {
        public Task<string?> GetAsync(string key) => Task.FromResult(values.TryGetValue(key, out var v) ? v : null);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        public Task SetAsync(string key, string value) { values[key] = value; return Task.CompletedTask; }
    }

    private static CopilotHarnessRuntime Runtime(params (string Key, string Value)[] on)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUserPreferenceRepository>(_ => new Preferences(on.ToDictionary(p => p.Key, p => p.Value)));
        return new CopilotHarnessRuntime(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task with_the_experimental_switch_off_nothing_starts_and_it_says_why()
    {
        var runtime = Runtime();

        (await runtime.CheckAvailabilityAsync(CancellationToken.None)).Reason.ShouldBe(CopilotHarnessRuntime.FeatureOffMessage);
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SpawnAsync(
            new HarnessSpawnOptions { SessionId = "s", WorkingDirectory = Path.GetTempPath(), OwnerUserId = "u" }, CancellationToken.None)))
            .Message.ShouldBe(CopilotHarnessRuntime.FeatureOffMessage);
        await Should.ThrowAsync<InvalidOperationException>(() => runtime.ResumeAsync(
            new HarnessResumeOptions { SessionId = "s", WorkingDirectory = Path.GetTempPath(), ResumeToken = "t", OwnerUserId = "u" }, CancellationToken.None));
        (await runtime.GetCatalogAsync("u", Path.GetTempPath(), null, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void the_cli_counts_as_found_when_fleet_installed_one_the_env_names_one_or_path_has_one()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cli = Path.Combine(folder, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
            File.WriteAllText(cli, "");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            CopilotHarnessRuntime.CliFound(null, null, null, null).ShouldBeFalse();
            CopilotHarnessRuntime.CliFound("/managed/copilot", null, null, null).ShouldBeTrue();
            // Fleet hands its own CLI over only to the weave-copilot it installed: a custom one has to find a CLI itself.
            CopilotHarnessRuntime.CliFound("/managed/copilot", "/opt/my/weave-copilot", null, null).ShouldBeFalse();
            CopilotHarnessRuntime.CliFound(null, null, cli, null).ShouldBeTrue();
            CopilotHarnessRuntime.CliFound(null, null, Path.Combine(folder, "gone"), folder).ShouldBeFalse();
            CopilotHarnessRuntime.CliFound(null, "/opt/my/weave-copilot", null, folder).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void a_start_failure_says_what_to_do_about_a_missing_cli_and_keeps_other_reasons()
    {
        CopilotHarnessRuntime.StartFailure(new AcpException("Copilot CLI not found: pass --copilot-cli <path>, set WEAVE_COPILOT_CLI, or put `copilot` on PATH"))
            .Message.ShouldBe(CopilotHarnessRuntime.CliMissingMessage);
        CopilotHarnessRuntime.StartFailure(new AcpException("The token was rejected.")).Message.ShouldBe("The token was rejected.");
    }
}
