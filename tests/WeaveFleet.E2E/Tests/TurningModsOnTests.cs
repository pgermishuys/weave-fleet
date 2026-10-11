using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Turning Mods on in Settings → Features, against a local stand-in for Bun's downloads: the row says what Fleet will
/// install, a blocked download says why and turns the switch back off, and Retry shows the install's progress until
/// Bun is ready. Fleet's Bun goes into a temporary home, never the real one.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TurningModsOnTests : E2ETestBase,
    IClassFixture<TurningModsOnTests.ModsFleetFactory>,
    IClassFixture<PlaywrightFixture>
{
    private readonly ModsFleetFactory _factory;

    public TurningModsOnTests(ModsFleetFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) => _factory = factory;

    [Fact]
    public async Task Turning_Mods_on_says_why_a_download_failed_then_installs_Bun_with_progress()
    {
        await WithFailureCapture(async () =>
        {
            // Settings checks the session before it shows a section, and a click on the nav before that is lost.
            await Page.GotoAsync("/settings");
            await Assertions.Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Exact = true })).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Page.GetByRole(AriaRole.Button, new() { Name = "Features", Exact = true }).ClickAsync();
            var toggle = Page.Locator("[data-testid='mods-switch']");
            var panel = Page.Locator("[data-testid='mods-panel']");
            await Assertions.Expect(toggle).ToBeVisibleAsync(new() { Timeout = 15_000 });

            // Nothing downloads until the user chooses, and the switch stays off.
            await toggle.ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Mods need Bun");
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "false");
            Assert.Equal(0, _factory.Bun.Requests);

            _factory.Bun.Forbid = true;
            await panel.GetByRole(AriaRole.Button, new() { Name = "Turn on and install" }).ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Couldn't download Bun", new() { Timeout = 15_000 });
            await Assertions.Expect(panel).ToContainTextAsync("answered 403 Forbidden");
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "false");

            // Retry: the download stops half way until the test lets it finish, so the progress is there to see.
            _factory.Bun.Forbid = false;
            await panel.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Downloading Bun 1.4.2", new() { Timeout = 15_000 });
            await Assertions.Expect(panel.GetByRole(AriaRole.Progressbar)).ToBeVisibleAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "true");

            _factory.Bun.Finish();
            await Assertions.Expect(panel).ToContainTextAsync("Mods on · Bun 1.4.2", new() { Timeout = 15_000 });
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "true");
            Assert.True(File.Exists(Path.Combine(_factory.Home, ".weave", "runtimes", "bun", "1.4.2", ModsFleetFactory.Executable)));
        });
    }

    /// <summary>Fleet with its Bun release pointing at <see cref="Bun"/> and installing into a temporary home.</summary>
    public sealed class ModsFleetFactory : FleetWebApplicationFactory, IAsyncDisposable
    {
        internal static readonly string Executable = OperatingSystem.IsWindows() ? "bun.exe" : "bun";

        public BunDownloads Bun { get; } = new();

        public string Home { get; } = Directory.CreateTempSubdirectory("fleet-mods-e2e-").FullName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBunReleases>();
                services.AddSingleton<IBunReleases>(new FixedRelease(Bun.Release));
                services.RemoveAll<IBunRuntime>();
                services.AddSingleton<IBunRuntime>(sp => new BunRuntimeInstaller(
                    sp.GetRequiredService<FleetOptions>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<ILogger<BunRuntimeInstaller>>())
                {
                    Home = Home,
                    DownloadBase = Bun.BaseUrl,
                });
            });
        }

        public new async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Bun.Dispose();
            try
            {
                Directory.Delete(Home, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }

        private sealed class FixedRelease(BunRelease release) : IBunReleases
        {
            public BunRelease Current => release;
        }
    }

    /// <summary>
    /// A stand-in for github.com/oven-sh/bun/releases/download serving one small Bun archive. It answers 403 while
    /// <see cref="Forbid"/> is set; otherwise it sends half the archive and waits for <see cref="Finish"/>.
    /// </summary>
    public sealed class BunDownloads : IDisposable
    {
        private const string Asset = "bun-e2e.zip";
        private readonly HttpListener _listener = new();
        private readonly byte[] _archive = BuildArchive();
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public BunDownloads()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUrl.ToString());
            _listener.Start();
            _ = Task.Run(ServeAsync);

            var asset = new BunAsset(BunRelease.CurrentRid(), Asset, Convert.ToHexStringLower(SHA256.HashData(_archive))) { Size = _archive.Length };
            Release = new BunRelease("1.4.2", [asset]);
        }

        public Uri BaseUrl { get; }

        public BunRelease Release { get; }

        private volatile bool _forbid;

        /// <summary>Answer 403, as a proxy blocking GitHub would.</summary>
        public bool Forbid
        {
            get => _forbid;
            set => _forbid = value;
        }

        public int Requests => Volatile.Read(ref _requests);

        public void Finish() => _finish.TrySetResult();

        public void Dispose()
        {
            _finish.TrySetResult();
            _listener.Close();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(context));
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _requests);
            var response = context.Response;
            try
            {
                if (Forbid || context.Request.Url?.AbsolutePath != $"/bun-v{Release.Version}/{Asset}")
                {
                    response.StatusCode = Forbid ? 403 : 404;
                    response.Close();
                    return;
                }

                response.ContentLength64 = _archive.Length;
                var half = _archive.Length / 2;
                await response.OutputStream.WriteAsync(_archive.AsMemory(0, half));
                await response.OutputStream.FlushAsync();
                await _finish.Task;
                await response.OutputStream.WriteAsync(_archive.AsMemory(half));
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // Fleet went away mid-download.
            }
        }

        /// <summary>A zip shaped like Bun's: one folder named after the asset, holding the executable (and padding, so there's a middle).</summary>
        private static byte[] BuildArchive()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var folder = Path.GetFileNameWithoutExtension(Asset);
                using (var writer = new StreamWriter(zip.CreateEntry($"{folder}/{ModsFleetFactory.Executable}").Open()))
                    writer.Write("#!/bin/sh\necho 1.4.2\n");
                var padding = zip.CreateEntry($"{folder}/LICENSE", CompressionLevel.NoCompression).Open();
                using (padding)
                    padding.Write(RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
            }

            return buffer.ToArray();
        }
    }
}
