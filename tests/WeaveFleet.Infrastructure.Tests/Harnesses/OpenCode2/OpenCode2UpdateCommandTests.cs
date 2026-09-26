using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>Which OpenCode 2 installs Fleet offers to update. Nothing runs; only the command is built.</summary>
public sealed class OpenCode2UpdateCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("fleet-oc2-update-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DefaultExecutable => Path.Combine(_home, ".opencode", "bin", "opencode2");

    [Fact]
    public void An_OpenCode_2_install_is_updated_with_its_installer()
    {
        var command = Runtime().GetUpdateCommand(HarnessAvailability.Ready("2.0.9", DefaultExecutable), "2.0.18");

        command.ShouldNotBeNull().Display.ShouldBe("curl -fsSL https://opencode.ai/v2/install | bash -s -- --no-modify-path --version 2.0.18");
    }

    [Fact]
    public void A_default_install_that_runs_OpenCode_1_now_isnt_offered_an_update()
    {
        // OpenCode 1's installer replaced the program the opencode2 shim runs; V2's installer would replace OpenCode 1 in turn.
        var replaced = HarnessAvailability.NotWorking("runs OpenCode 1.18.32 now", "1.18.32", DefaultExecutable);

        Runtime().GetUpdateCommand(replaced, "2.0.18").ShouldBeNull();
    }

    private OpenCode2HarnessRuntime Runtime()
    {
        var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        return new OpenCode2HarnessRuntime(
            services.GetRequiredService<IHttpClientFactory>(),
            new FleetOptions { DatabasePath = Path.Combine(_home, "fleet.db") },
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenCode2HarnessRuntime>.Instance,
            NullLoggerFactory.Instance,
            analytics: null,
            new OpenCode2Install(_home, _ => null, [], (path, _) => Task.FromResult(HarnessAvailability.Ready(null, path)), windows: false));
    }
}
