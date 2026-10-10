using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// Fleet's transport against the real host (<c>mods/host/dist/host.js</c> on Bun): what M1 answers is what Fleet reads.
/// </summary>
[Trait("Category", RealModHost.Category)]
public sealed class RealModHostConnectionTests : IAsyncDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-realhost-").FullName;
    private readonly RecordingModHostCalls _calls = new();
    private readonly CapturingLogger _log = new();
    private IModHostConnection? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<IModHostConnection?> StartAsync()
    {
        if (RealModHost.Find() is not { } real)
            return null;
        _host = await new ModHostConnectionFactory(_log).StartAsync(
            new ModHostLaunch(real.Bun, real.HostScript, Path.Combine(_folder, ".host"), "0.49.0-test", "test-user"), _calls, CancellationToken.None);
        return _host;
    }

    /// <summary>A kept version's folder, as the store lays it out: {name}/v{n}/.</summary>
    private string Kept(string fixture, int number = 1) => RealModHost.CopyFixture(fixture, Path.Combine(_folder, fixture, $"v{number}"));

    [Fact]
    public async Task Initialize_answers_protocol_1_with_the_host_and_Bun_versions()
    {
        if (await StartAsync() is not { } host)
            return;

        host.Host.Protocol.ShouldBe(1);
        host.Host.HostVersion.ShouldNotBeNullOrWhiteSpace();
        host.Host.BunVersion.ShouldStartWith("1.");
    }

    [Fact]
    public async Task Check_of_test_chips_matches_the_contracts_report()
    {
        if (await StartAsync() is not { } host)
            return;
        var root = Kept("test-chips");

        var report = await host.CheckAsync(root, Path.Combine(root, "mod.json"), CancellationToken.None);

        // docs/mods/api.md, "Worked example: test-chips": test-chips 0.1.0 · 47 lines, one ui.render hook, calls ui.resolve.
        report.GetProperty("ok").GetBoolean().ShouldBeTrue();
        report.GetProperty("name").GetString().ShouldBe("test-chips");
        report.GetProperty("version").GetString().ShouldBe("0.1.0");
        report.GetProperty("lines").GetInt32().ShouldBe(47);
        report.GetProperty("hooks").GetRawText().ShouldBe("""[{"event":"ui.render","matcher":{"component":["ToolUse","ToolResult"],"props":{"tool":["bash","shell"]}}}]""");
        report.GetProperty("calls").GetRawText().ShouldBe("""["ui.resolve"]""");
        report.GetProperty("state").GetArrayLength().ShouldBe(0);
        report.GetProperty("pages").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Load_reports_the_hooks_Fleet_routes_with_and_a_render_returns_the_chips()
    {
        if (await StartAsync() is not { } host)
            return;

        var loaded = await host.LoadAsync(ModLoadParams.Kept("test-chips", 1, Kept("test-chips")), CancellationToken.None);
        loaded.Hooks.Count.ShouldBe(1);
        loaded.Hooks[0].Event.ShouldBe("ui.render");
        ModRouting.ChainFor([new ModRoute("test-chips@v1", "test-chips", null, loaded.Hooks)], "ui.render", "ses_test1", RealModHost.DotnetTestRow("ToolUse"))
            .ShouldBe(["test-chips@v1"]);

        var drawn = await host.DispatchAsync(
            new ModWireDispatch("ui.render", "ses_test1", RealModHost.DotnetTestRow("ToolUse"), ["test-chips@v1"], null), TimeSpan.FromSeconds(15), CancellationToken.None);

        drawn.Result.GetRawText().ShouldBe("""{"type":"Box","props":{"flexDirection":"row","gap":1},"children":[{"type":"Pill","props":{"tone":"good","label":"212 passed"}},{"type":"Pill","props":{"tone":"bad","label":"2 failed"}},{"type":"Pill","props":{"tone":"neutral","label":"4 skipped"}}]}""");
        drawn.DrawnBy.ShouldBe(["test-chips@v1"]);
        drawn.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_load_the_check_refuses_answers_minus_32001_with_the_report()
    {
        if (await StartAsync() is not { } host)
            return;
        var root = Kept("test-chips");
        File.WriteAllText(Path.Combine(root, "mod.ts"), "import fs from \"node:fs\";\nexport const register = () => {};\n");

        var refused = await Should.ThrowAsync<ModHostRpcException>(() => host.LoadAsync(ModLoadParams.Kept("test-chips", 1, root), CancellationToken.None));

        refused.Code.ShouldBe(ModHostErrorCodes.NotLoaded);
        refused.Data.ShouldNotBeNull();
        refused.Data.Value.GetProperty("ok").GetBoolean().ShouldBeFalse();
        refused.Data.Value.GetProperty("errors")[0].GetProperty("code").GetString().ShouldBe("import");
    }

    [Fact]
    public async Task Store_and_session_calls_cross_back_to_Fleet_during_a_dispatch()
    {
        if (await StartAsync() is not { } host)
            return;
        await host.LoadAsync(ModLoadParams.Kept("probe-mod", 1, Kept("probe-mod")), CancellationToken.None);

        // The status chip's render runs session.start first (which counts into $.store), then reads the count and the title.
        var drawn = await host.DispatchAsync(new ModWireDispatch("ui.render", "ses_test1", RealModHost.Site("StatusChip"), ["probe-mod@v1"], null), TimeSpan.FromSeconds(15), CancellationToken.None);

        drawn.Result.GetRawText().ShouldBe("""{"type":"Text","props":{},"children":["1 starts in Make test output readable"]}""");
        _calls.Store["probe-mod@v1/starts"].GetInt32().ShouldBe(1);
        _calls.Requests.Select(r => r.Method).ShouldBe(["store.get", "store.set", "store.get", "session.get"]);
        _calls.Of("log").ShouldContain(l => l.GetProperty("text").GetString() == "start start" && l.GetProperty("mod").GetString() == "probe-mod@v1");
    }

    [Fact]
    public async Task A_hook_that_never_yields_leaves_the_dispatch_unanswered_and_running_names_it()
    {
        if (await StartAsync() is not { } host)
            return;
        // Draws first, then hangs: the outer mod passes on to the inner one, which loops.
        var band = RealModHost.CopyFixture("band-draft", Path.Combine(_folder, "band-draft", "v1"));
        File.WriteAllText(Path.Combine(band, "mod.ts"), """
            import type { Register } from "fleet-mods";

            export const register: Register = (on) => {
              on("ui.render", { component: "ComposerBand" }, ($, e, next) => next(e));
            };
            """);
        await host.LoadAsync(ModLoadParams.Kept("band-draft", 1, band), CancellationToken.None);
        await host.LoadAsync(ModLoadParams.Kept("hang-hook", 1, Kept("hang-hook")), CancellationToken.None);

        await Should.ThrowAsync<TimeoutException>(() => host.DispatchAsync(
            new ModWireDispatch("ui.render", "ses_test1", RealModHost.Site("ComposerBand"), ["band-draft@v1", "hang-hook@v1"], null), TimeSpan.FromSeconds(2), CancellationToken.None));

        _calls.Of("running").Last().GetProperty("mod").GetString().ShouldBe("hang-hook@v1");
        host.Exited.IsCompleted.ShouldBeFalse();
        host.Kill();
        await host.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_exits_0_within_2_seconds()
    {
        if (await StartAsync() is not { } host)
            return;
        await host.LoadAsync(ModLoadParams.Kept("test-chips", 1, Kept("test-chips")), CancellationToken.None);

        var clock = Stopwatch.StartNew();
        await host.ShutdownAsync(TimeSpan.FromSeconds(2));

        (await host.Exited).ShouldBe(0);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task The_host_runs_with_an_empty_environment_in_its_working_folder()
    {
        Environment.SetEnvironmentVariable("FLEET_SECRET_FOR_TEST", "do-not-leak");
        try
        {
            if (await StartAsync() is not { } host)
                return;

            if (OperatingSystem.IsLinux())
            {
                var environment = File.ReadAllText($"/proc/{host.ProcessId}/environ");
                environment.ShouldNotContain("FLEET_SECRET_FOR_TEST");
                environment.ShouldBeEmpty();
                Path.GetFullPath(new DirectoryInfo($"/proc/{host.ProcessId}/cwd").ResolveLinkTarget(true)!.FullName)
                    .ShouldBe(Path.GetFullPath(Path.Combine(_folder, ".host")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLEET_SECRET_FOR_TEST", null);
        }
    }
}
