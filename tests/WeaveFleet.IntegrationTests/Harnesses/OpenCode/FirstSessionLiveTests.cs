using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// A user's first OpenCode session after installing Fleet. Fleet always loads its own plugin, and OpenCode answers the
/// first request that names a folder only after it has installed the plugin packages (about 60 MB) into an empty config
/// folder: 30 to 100 s, which is past the 100 s OpenCode requests wait. This starts from a config folder with no
/// packages in it, on purpose: the other live tests start from one that has them, because their subject isn't the first run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FirstSessionLiveTests
{
    private const string Owner = "local-user";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [OpenCodeFact]
    public async Task With_no_plugin_packages_installed_yet_the_first_session_waits_for_the_install_and_starts()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-first-session-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var plugin = Path.Combine(root, "no-op.ts");
        File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");

        // Not seeded, unlike every other live test: this is the one place the install is meant to happen.
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, new Uri("http://127.0.0.1:9"), plugin, seedPluginDependencies: false);
        var packages = Path.Combine(processEnvironment["XDG_CONFIG_HOME"], "opencode", "node_modules");
        Directory.Exists(packages).ShouldBeFalse("the config folder must start without OpenCode's plugin packages");

        // Ordinary requests wait 3 s here, not 100: the install takes longer than that wherever this runs, so the
        // failure shows on any machine and not only on a slow one.
        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath, openCodeRequestTimeout: TimeSpan.FromSeconds(3));
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var runtime = factory.LiveServices.GetRequiredService<OpenCodeHarnessRuntime>();
            await using var session = await runtime.SpawnAsync(
                new HarnessSpawnOptions
                {
                    SessionId = "fleet-first-session-live",
                    WorkingDirectory = workspace,
                    OwnerUserId = Owner,
                    LaunchArtifacts = new OpenCodeLaunchArtifacts(processEnvironment),
                },
                ct);

            session.ResumeToken.ShouldNotBeNullOrWhiteSpace();
            Directory.Exists(Path.Combine(packages, "@opencode-ai", "plugin"))
                .ShouldBeTrue("OpenCode should have installed its plugin packages for the first session; if it no longer does, this test proves nothing");
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* a process still holds a file; the temp folder is cleaned up later */ }
        }
    }
}
