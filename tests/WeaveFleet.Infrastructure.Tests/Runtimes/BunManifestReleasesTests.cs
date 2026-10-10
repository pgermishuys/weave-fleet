using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunManifestReleasesTests : IDisposable
{
    private const string ManifestPath = "/bun.json";

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fleet-bun-manifest-{Guid.NewGuid():N}");
    private readonly BunReleaseServer _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly List<BunManifestReleases> _started = [];

    public BunManifestReleasesTests() => Directory.CreateDirectory(_home);

    private string Root => Path.Combine(_home, ".weave", "runtimes", "bun");

    private string CachePath => Path.Combine(Root, "bun.json");

    private static BunRelease Pin => BunRelease.Pinned;

    public void Dispose()
    {
        foreach (var service in _started)
            service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

        _server.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // -- schedule ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_fetches_once_five_seconds_after_start_and_then_every_interval()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create(bunPath: "/usr/bin/bun", intervalHours: 4);

        await Start(service);
        _time.Advance(TimeSpan.FromSeconds(4.9));
        await Quiet();
        _server.Requests.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(0.1));
        await Requests(1);

        await AdvanceBy(TimeSpan.FromHours(3));
        _server.Requests.Count.ShouldBe(1);

        await AdvanceUntilRequests(2, TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task With_check_on_startup_off_the_first_fetch_waits_for_the_interval()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create(bunPath: "/usr/bin/bun", checkOnStartup: false, intervalHours: 2);

        await Start(service);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Quiet();
        _server.Requests.ShouldBeEmpty();

        await AdvanceUntilRequests(1, TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task An_interval_of_zero_means_no_periodic_fetch()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create(bunPath: "/usr/bin/bun", intervalHours: 0);

        await Start(service);
        _time.Advance(TimeSpan.FromSeconds(5));
        await Requests(1);

        await AdvanceBy(TimeSpan.FromHours(100));
        await Quiet();
        _server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dev_layout_never_fetches_on_its_schedule()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create(bunPath: "/usr/bin/bun", installedLayout: false);

        await Start(service);
        await AdvanceBy(TimeSpan.FromHours(10));
        await Quiet();

        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dev_layout_still_fetches_when_asked()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create(installedLayout: false);

        await service.RefreshAsync(CancellationToken.None);

        _server.Requests.ShouldBe([ManifestPath]);
    }

    [Fact]
    public async Task When_fleet_has_no_bun_to_look_after_the_schedule_makes_no_request()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create();

        await Start(service);
        await AdvanceBy(TimeSpan.FromHours(9));
        await Quiet();

        _server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_bun_path_makes_the_schedule_fetch()
    {
        Serve(Manifest(Bump(Pin.Version)));
        await Start(Create(bunPath: "/usr/bin/bun"));

        _time.Advance(TimeSpan.FromSeconds(5));

        await Requests(1);
    }

    [Fact]
    public async Task An_installed_version_folder_makes_the_schedule_fetch()
    {
        Serve(Manifest(Bump(Pin.Version)));
        Directory.CreateDirectory(Path.Combine(Root, "1.4.1"));
        await Start(Create());

        _time.Advance(TimeSpan.FromSeconds(5));

        await Requests(1);
    }

    [Fact]
    public async Task A_cached_manifest_makes_the_schedule_fetch()
    {
        Serve(Manifest(Bump(Pin.Version)));
        WriteCache(Manifest(Bump(Pin.Version)));
        await Start(Create());

        _time.Advance(TimeSpan.FromSeconds(5));

        await Requests(1);
    }

    [Fact]
    public async Task Refresh_fetches_even_when_nothing_needs_it()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create();

        await service.RefreshAsync(CancellationToken.None);

        _server.Requests.ShouldBe([ManifestPath]);
        service.Current.Version.ShouldBe(Bump(Pin.Version));
    }

    [Fact]
    public void The_default_address_is_the_update_repositorys_bun_json()
    {
        var options = new FleetOptions { Update = { GitHubRepo = "acme/fleet-releases" } };

        BunManifestReleases.UrlFor(options).ToString().ShouldBe("https://raw.githubusercontent.com/acme/fleet-releases/main/bun.json");
        BunManifestReleases.UrlFor(new FleetOptions { Update = { BunManifestUrl = "http://example.test/x.json" } }).ToString()
            .ShouldBe("http://example.test/x.json");
    }

    // -- cache ------------------------------------------------------------------------------------------------------

    [Fact]
    public void With_no_cache_current_is_the_pin()
    {
        Create().Current.ShouldBe(Pin);
    }

    [Fact]
    public void A_newer_cache_wins_over_the_pin()
    {
        WriteCache(Manifest(Bump(Pin.Version), note: "Fixes things."));

        var current = Create().Current;

        current.Version.ShouldBe(Bump(Pin.Version));
        current.Note.ShouldBe("Fixes things.");
    }

    [Fact]
    public void A_cache_as_new_as_the_pin_wins()
    {
        WriteCache(Manifest(Pin.Version, note: "Same version."));

        Create().Current.Note.ShouldBe("Same version.");
    }

    [Fact]
    public void A_cache_older_than_the_pin_loses_to_it()
    {
        WriteCache(Manifest(Older(Pin.Version)));

        Create().Current.ShouldBe(Pin);
    }

    [Fact]
    public void The_effective_oldest_safe_is_the_higher_of_the_pins_and_the_manifests()
    {
        // The manifest is newer but its floor is lower than the pin's: the pin's floor holds.
        var highPin = Pin with { OldestSafe = Pin.Version };
        WriteCache(Manifest(Bump(Pin.Version, 2), oldestSafe: "1.4.0"));
        var service = Create(pin: highPin);

        service.Current.Version.ShouldBe(Bump(Pin.Version, 2));
        service.Current.OldestSafe.ShouldBe(Pin.Version);

        // And the other way: a manifest that raised the floor beyond the pin's.
        WriteCache(Manifest(Bump(Pin.Version, 2), oldestSafe: Bump(Pin.Version, 1)));
        Create().Current.OldestSafe.ShouldBe(Bump(Pin.Version, 1));
    }

    [Fact]
    public void A_pin_that_beats_an_older_cache_keeps_the_caches_higher_floor()
    {
        // A newer Fleet's pin against a cache an older Fleet wrote.
        var newPin = Pin with { Version = Bump(Pin.Version, 3) };
        WriteCache(Manifest(Bump(Pin.Version, 1), oldestSafe: Bump(Pin.Version, 1)));

        var current = Create(pin: newPin).Current;

        current.Version.ShouldBe(newPin.Version);
        current.OldestSafe.ShouldBe(Bump(Pin.Version, 1));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"schema\":1}")]
    [InlineData("")]
    public void A_corrupt_or_invalid_cache_is_ignored_and_logged(string text)
    {
        WriteCache(Encoding.UTF8.GetBytes(text));
        var logger = new ListLogger();

        var service = Create(logger: logger);

        service.Current.ShouldBe(Pin);
        logger.Messages.ShouldContain(message => message.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_cache_bigger_than_a_manifest_may_be_is_ignored()
    {
        WriteCache(new byte[BunManifest.MaxBytes + 1]);

        Create().Current.ShouldBe(Pin);
    }

    [Fact]
    public async Task An_accepted_manifest_is_cached_and_the_file_parses()
    {
        Serve(Manifest(Bump(Pin.Version), note: "New one."));
        var service = Create();

        await service.RefreshAsync(CancellationToken.None);

        File.Exists(CachePath).ShouldBeTrue();
        BunManifest.TryParse(File.ReadAllBytes(CachePath), out var cached, out var error).ShouldBeTrue(error);
        cached!.Version.ShouldBe(Bump(Pin.Version));
        cached.Note.ShouldBe("New one.");
        Directory.GetFiles(Root).Select(Path.GetFileName).ShouldBe(["bun.json"]);
        Create().Current.Version.ShouldBe(Bump(Pin.Version));
    }

    // -- the downgrade guard ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_json_is_ignored()
    {
        await AssertIgnored(Encoding.UTF8.GetBytes("{ nope"));
    }

    [Fact]
    public async Task A_version_older_than_the_pin_is_ignored()
    {
        await AssertIgnored(Manifest(Older(Pin.Version)));
    }

    [Fact]
    public async Task An_oldest_safe_below_the_pins_is_ignored_when_nothing_is_cached()
    {
        var highPin = Pin with { OldestSafe = Pin.Version };
        Serve(Manifest(Bump(Pin.Version), oldestSafe: "1.4.0"));
        var service = Create(pin: highPin);
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);

        service.Current.ShouldBe(highPin);
        File.Exists(CachePath).ShouldBeFalse();
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_oldest_safe_below_the_last_good_one_is_ignored()
    {
        var good = Manifest(Bump(Pin.Version, 2), oldestSafe: Bump(Pin.Version, 1));
        WriteCache(good);
        Serve(Manifest(Bump(Pin.Version, 3), oldestSafe: Pin.Version));
        var service = Create();
        var events = Record(service);
        var before = service.Current;

        await service.RefreshAsync(CancellationToken.None);

        service.Current.ShouldBe(before);
        File.ReadAllBytes(CachePath).ShouldBe(good);
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused()
    {
        var big = Manifest(Bump(Pin.Version)).Concat(new byte[BunManifest.MaxBytes]).ToArray();
        await AssertIgnored(big);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task A_failing_status_is_ignored(int status)
    {
        _server.Serve(ManifestPath, Manifest(Bump(Pin.Version)), status);
        var service = Create();
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);

        service.Current.ShouldBe(Pin);
        File.Exists(CachePath).ShouldBeFalse();
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_server_that_hangs_is_given_up_on_after_the_timeout()
    {
        _server.Serve(ManifestPath, Manifest(Bump(Pin.Version)), mode: BunServeMode.Stall);
        var service = Create(timeout: TimeSpan.FromMilliseconds(300));
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        service.Current.ShouldBe(Pin);
        File.Exists(CachePath).ShouldBeFalse();
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_token_cancels_the_refresh()
    {
        _server.Serve(ManifestPath, Manifest(Bump(Pin.Version)), mode: BunServeMode.Stall);
        var service = Create();
        using var cancel = new CancellationTokenSource();

        var refresh = service.RefreshAsync(cancel.Token);
        await _server.Stalled.WaitAsync(TimeSpan.FromSeconds(20));
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(refresh);
    }

    // -- events -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_newer_version_raises_changed_with_newer_version_and_not_safer_required()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create();
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);

        var change = events.ShouldHaveSingleItem();
        change.Previous.ShouldBe(Pin);
        change.Current.Version.ShouldBe(Bump(Pin.Version));
        change.NewerVersion.ShouldBeTrue();
        change.SaferRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task A_higher_oldest_safe_raises_safer_required()
    {
        Serve(Manifest(Bump(Pin.Version), oldestSafe: Bump(Pin.Version)));
        var service = Create();
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);

        events.ShouldHaveSingleItem().SaferRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task The_same_manifest_twice_raises_one_event()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create();
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);

        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_changed_note_alone_raises_an_event()
    {
        var service = Create();
        var events = Record(service);
        Serve(Manifest(Bump(Pin.Version), note: "One."));
        await service.RefreshAsync(CancellationToken.None);
        Serve(Manifest(Bump(Pin.Version), note: "Two."));

        await service.RefreshAsync(CancellationToken.None);

        events.Count.ShouldBe(2);
        events[1].NewerVersion.ShouldBeFalse();
        events[1].Current.Note.ShouldBe("Two.");
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_next_one_or_the_service()
    {
        Serve(Manifest(Bump(Pin.Version)));
        var service = Create();
        var seen = new List<BunReleaseChangedEventArgs>();
        service.Changed += (_, _) => throw new InvalidOperationException("boom");
        service.Changed += (_, args) => seen.Add(args);

        await service.RefreshAsync(CancellationToken.None);

        seen.Count.ShouldBe(1);
        service.Current.Version.ShouldBe(Bump(Pin.Version));

        Serve(Manifest(Bump(Pin.Version, 2)));
        await service.RefreshAsync(CancellationToken.None);
        seen.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_rollback_within_the_guard_raises_changed_without_newer_version()
    {
        var service = Create();
        var events = Record(service);
        Serve(Manifest(Bump(Pin.Version, 2)));
        await service.RefreshAsync(CancellationToken.None);
        Serve(Manifest(Bump(Pin.Version, 1)));

        await service.RefreshAsync(CancellationToken.None);

        events.Count.ShouldBe(2);
        events[1].Current.Version.ShouldBe(Bump(Pin.Version, 1));
        events[1].NewerVersion.ShouldBeFalse();
        events[1].SaferRequired.ShouldBeFalse();
        service.Current.Version.ShouldBe(Bump(Pin.Version, 1));
    }

    // -- wiring -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_container_gives_one_shared_instance_which_runs_the_schedule_and_the_installer_gets_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new FleetOptions());
        services.AddLogging();
        services.AddHttpClient();
        services.AddBunRuntime();
        using var provider = services.BuildServiceProvider();

        var releases = provider.GetRequiredService<IBunReleases>();

        releases.ShouldBeOfType<BunManifestReleases>();
        provider.GetRequiredService<IBunReleases>().ShouldBeSameAs(releases);
        provider.GetServices<IHostedService>().ShouldContain(service => ReferenceEquals(service, releases));
        provider.GetRequiredService<IBunRuntime>().ShouldBeOfType<BunRuntimeInstaller>();
    }

    // -- helpers ----------------------------------------------------------------------------------------------------

    private BunManifestReleases Create(
        string bunPath = "",
        bool checkOnStartup = true,
        int intervalHours = 4,
        bool installedLayout = true,
        TimeSpan? timeout = null,
        BunRelease? pin = null,
        ILogger<BunManifestReleases>? logger = null)
    {
        var options = new FleetOptions
        {
            Harness = { BunPath = bunPath },
            Update =
            {
                CheckOnStartup = checkOnStartup,
                CheckIntervalHours = intervalHours,
                BunManifestUrl = new Uri(_server.BaseUri, "bun.json").ToString(),
            },
        };

        return new BunManifestReleases(options, new HttpClientFactoryStub(), _time, logger ?? NullLogger<BunManifestReleases>.Instance)
        {
            Home = _home,
            IsInstalledLayout = () => installedLayout,
            FetchTimeout = timeout ?? TimeSpan.FromSeconds(30),
            Pinned = pin ?? Pin,
        };
    }

    private async Task Start(BunManifestReleases service)
    {
        _started.Add(service);
        await service.StartAsync(CancellationToken.None);
    }

    private void Serve(byte[] body) => _server.Serve(ManifestPath, body);

    private static List<BunReleaseChangedEventArgs> Record(IBunReleases releases)
    {
        var events = new List<BunReleaseChangedEventArgs>();
        releases.Changed += (_, args) => events.Add(args);
        return events;
    }

    private async Task AssertIgnored(byte[] body)
    {
        Serve(body);
        var service = Create();
        var events = Record(service);

        await service.RefreshAsync(CancellationToken.None);

        _server.Requests.ShouldBe([ManifestPath]);
        service.Current.ShouldBe(Pin);
        File.Exists(CachePath).ShouldBeFalse();
        events.ShouldBeEmpty();
    }

    private void WriteCache(byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(CachePath, bytes);
    }

    /// <summary>A version <paramref name="by"/> patch releases newer.</summary>
    private static string Bump(string version, int by = 1)
    {
        var parsed = BunVersion.Parse(version);
        return $"{parsed.Major}.{parsed.Minor}.{parsed.Patch + by}";
    }

    private static string Older(string version)
    {
        var parsed = BunVersion.Parse(version);
        return $"{parsed.Major}.{parsed.Minor}.{parsed.Patch - 1}";
    }

    private static byte[] Manifest(string version, string? oldestSafe = null, string? note = null)
    {
        var assets = string.Join(",", BunManifest.AssetNames.Select(pair =>
            $"\"{pair.Key}\":{{\"name\":\"{pair.Value}\",\"sha256\":\"{new string('a', 64)}\",\"size\":1000}}"));
        var noteJson = note is null ? "null" : $"\"{note}\"";
        return Encoding.UTF8.GetBytes(
            $"{{\"schema\":1,\"version\":\"{version}\",\"oldestSafe\":\"{oldestSafe ?? "1.4.0"}\",\"note\":{noteJson},\"assets\":{{{assets}}}}}");
    }

    private async Task Requests(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_server.Requests.Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {count} requests, saw {_server.Requests.Count}.");

            await Task.Delay(10);
        }
    }

    /// <summary>Moves the clock on in steps, pausing between them, so the schedule's timers exist before time passes them.</summary>
    private async Task AdvanceBy(TimeSpan total)
    {
        var step = TimeSpan.FromMinutes(10);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            _time.Advance(step);
            await Task.Delay(2);
        }
    }

    private async Task AdvanceUntilRequests(int count, TimeSpan atMost)
    {
        var step = TimeSpan.FromMinutes(10);
        for (var elapsed = TimeSpan.Zero; elapsed < atMost && _server.Requests.Count < count; elapsed += step)
        {
            _time.Advance(step);
            await Task.Delay(5);
        }

        await Requests(count);
    }

    private static Task Quiet() => Task.Delay(250);

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ListLogger : ILogger<BunManifestReleases>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
