using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Weave in OpenCode 2, with a real <c>opencode2</c> and Weave's real V2 adapter from npm: Settings → Weave finds it,
/// Add Weave puts it in the user's (scratch) config and running servers load it without a restart, and a config kept
/// in Fleet reaches them. Needs npm, and an OpenCode 2 the adapter supports (<see cref="WeaveOpenCode2FactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed class OpenCode2WeaveLiveTests(OpenCode2LiveFleet fleet) : IClassFixture<OpenCode2LiveFleet>
{
    private const string Harness = OpenCode2HarnessSession.Type;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    [WeaveOpenCode2Fact]
    public async Task Weave_added_from_fleet_loads_in_the_running_server_and_takes_the_config_kept_in_fleet()
    {
        using var cts = new CancellationTokenSource(Timeout);
        KeepOpenCode1OffTheRealHome();
        var folder = fleet.NewFolder("weave");
        var configFile = Path.Combine(fleet.Runtime.ServerEnvironment["OPENCODE_CONFIG_DIR"], "opencode.json");

        // A session, so a server is running when Weave is added.
        await fleet.CreateSessionAsync(folder, "Weave", cts.Token);
        (await AgentsAsync(folder, cts.Token)).ShouldNotContain("loom");

        var before = Detection((await WeaveAsync(w => w.GetAsync(redetect: true, cts.Token))).Value);
        before.Checked.ShouldBeTrue();
        before.Installs.ShouldBeEmpty();
        before.AddTo.ShouldBe(configFile);

        // Add Weave: the adapter goes into the user's config at an exact version, and V2 loads it without a restart.
        var added = await WeaveAsync(w => w.AddPluginAsync(Harness, cts.Token));
        added.IsSuccess.ShouldBeTrue(added.IsFailure ? added.Error.Description : null);
        added.Value.Loaded.ShouldBeTrue(added.Value.Message);
        added.Value.Entry.ShouldStartWith(OpenCode2Weave.Package + "@");
        WeavePluginList.Read(await File.ReadAllTextAsync(configFile, cts.Token), "plugins").ShouldBe([added.Value.Entry]);
        var install = Detection(added.Value.Config).Installs.ShouldHaveSingleItem();
        install.AcceptsFleetConfig.ShouldBeTrue("This adapter doesn't read the folder Fleet points it at.");
        install.AddedByFleet.ShouldBeTrue();
        await WaitForAgentAsync(folder, "loom", cts.Token);

        // A config kept in Fleet is tried by a throwaway server first, then reaches the running one.
        var saved = await WeaveAsync(w => w.SaveAsync("fleet", new Dictionary<string, string>
        {
            ["config.weave"] = """
                agent fleet-live-agent {
                  description "Kept in Fleet"
                  prompt "You were set up in Fleet."
                  mode subagent
                }
                """,
        }, cts.Token));
        saved.IsSuccess.ShouldBeTrue(saved.IsFailure ? saved.Error.Description : null);
        var check = saved.Value.Checks.Single(c => c.HarnessType == Harness);
        check.Check.Ok.ShouldBeTrue(check.Check.Error);
        check.Check.Agents.ShouldContain("fleet-live-agent");
        saved.Value.Saved.ShouldBeTrue();
        await WaitForAgentAsync(folder, "fleet-live-agent", cts.Token);

        // Remove takes out what Add Weave put in, and V2 drops it.
        var removed = await WeaveAsync(w => w.RemovePluginAsync(Harness, cts.Token));
        removed.IsSuccess.ShouldBeTrue(removed.IsFailure ? removed.Error.Description : null);
        removed.Value.Loaded.ShouldBeTrue(removed.Value.Message);
        WeavePluginList.Read(await File.ReadAllTextAsync(configFile, cts.Token), "plugins").ShouldBeEmpty();
        Detection(removed.Value.Config).Installs.ShouldBeEmpty();
    }

    /// <summary>
    /// Detection asks every harness that's set up; OpenCode 1 would start with the test runner's HOME. Point it at a
    /// scratch one, as its own live tests do.
    /// </summary>
    private void KeepOpenCode1OffTheRealHome()
    {
        var root = Path.Combine(Path.GetDirectoryName(fleet.Runtime.ServerEnvironment["HOME"])!, "opencode1");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var directory in environment.Values)
            Directory.CreateDirectory(directory);
        fleet.Services.GetRequiredService<OpenCodeHarnessRuntime>().ProcessEnvironment = environment;
    }

    private static WeaveHarnessDetection Detection(WeaveConfigView view) => view.Harnesses.Single(h => h.HarnessType == Harness);

    private async Task<T> WeaveAsync<T>(Func<WeaveConfigService, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<WeaveConfigService>());
    }

    private async Task<IReadOnlyList<string>> AgentsAsync(string folder, CancellationToken ct)
        => (await fleet.Runtime.GetCatalogAsync(OpenCode2LiveFleet.Owner, folder, profile: null, ct))?.Agents.Select(a => a.Name).ToList() ?? [];

    /// <summary>The composer's agent list for <paramref name="folder"/> gets <paramref name="agent"/>: a reload settles in a few seconds.</summary>
    private async Task WaitForAgentAsync(string folder, string agent, CancellationToken ct)
    {
        IReadOnlyList<string> agents = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            agents = await AgentsAsync(folder, ct);
            if (agents.Contains(agent))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        throw new ShouldAssertException($"{agent} never showed up in {folder}. Agents: {string.Join(", ", agents)}");
    }
}

/// <summary>
/// A test that needs Weave's V2 adapter on a real OpenCode 2: an <c>opencode2</c> at the version the adapter targets
/// (2.0.16) or later, and npm. Skipped otherwise; CI pins an older OpenCode 2.
/// </summary>
internal sealed class WeaveOpenCode2FactAttribute : FactAttribute
{
    public const string MinimumVersion = "2.0.16";

    public WeaveOpenCode2FactAttribute()
    {
        if (OpenCode2LiveFleet.FindExecutable() is not { } executable || InstalledVersion(executable) is not { } version)
            Skip = "OpenCode 2 isn't installed where the harness looks.";
        else if (HarnessVersion.IsOlder(version, MinimumVersion))
            Skip = $"Weave's OpenCode 2 adapter targets OpenCode {MinimumVersion}; this one is {version}.";
    }

    private static string? InstalledVersion(string executable)
    {
        try
        {
            var probe = HarnessProbe.RunAsync(executable, ["--version"], CancellationToken.None).GetAwaiter().GetResult();
            return HarnessProbe.ParseVersion(probe.StandardOutput);
        }
        catch
        {
            return null;
        }
    }
}
