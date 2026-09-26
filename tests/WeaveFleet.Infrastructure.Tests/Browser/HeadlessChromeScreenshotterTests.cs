using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class HeadlessChromeScreenshotterTests
{
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void A_configured_browser_that_is_not_there_is_not_replaced_by_a_guess()
        => ChromeFinder.Find(Path.Combine(Path.GetTempPath(), "no-such-chrome")).ShouldBeNull();

    [Fact]
    public void A_configured_browser_wins_over_the_usual_places()
    {
        var file = Path.Combine(Path.GetTempPath(), "fleet-chrome-" + Guid.NewGuid().ToString("n"));
        File.WriteAllText(file, "#!/bin/sh\n");
        try
        {
            ChromeFinder.Find(file).ShouldBe(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Without_a_browser_on_the_machine_the_agent_is_told_what_to_install()
    {
        var options = new FleetOptions();
        options.Browser.ChromePath = Path.Combine(Path.GetTempPath(), "no-such-chrome");
        await using var screenshots = new HeadlessChromeScreenshotter(options, NullLogger<HeadlessChromeScreenshotter>.Instance);

        var shot = await screenshots.CaptureAsync(new ScreenshotRequest("http://127.0.0.1:1/", 1280, 800));

        shot.Image.ShouldBeNull();
        shot.Problem.ShouldBe(ChromeFinder.NotFound);
    }

    [Fact]
    public async Task A_page_on_this_machine_comes_back_as_a_PNG_of_the_window_it_was_asked_for()
    {
        if (Browser() is not { } options)
            return;

        using var site = new LocalSite("<body style='margin:0;background:#101317'><h1 style='color:#fff'>Shot</h1>");
        await using var screenshots = new HeadlessChromeScreenshotter(options, NullLogger<HeadlessChromeScreenshotter>.Instance);

        var shot = await screenshots.CaptureAsync(new ScreenshotRequest(site.Url, 800, 600));

        shot.Problem.ShouldBeNull();
        var image = shot.Image.ShouldNotBeNull();
        image.Png.Take(8).ShouldBe(PngHeader);
        Size(image.Png).ShouldBe((800, 600));

        // The browser stays up between shots, so the second one doesn't pay for a launch.
        var again = await screenshots.CaptureAsync(new ScreenshotRequest(site.Url, 390, 844));
        Size(again.Image.ShouldNotBeNull().Png).ShouldBe((390, 844));
    }

    [Fact]
    public async Task A_page_that_does_not_answer_says_so_instead_of_hanging()
    {
        if (Browser() is not { } options)
            return;

        await using var screenshots = new HeadlessChromeScreenshotter(options, NullLogger<HeadlessChromeScreenshotter>.Instance);

        // Port 1 is never a dev server: the browser reports a net error and the tool passes it on.
        var shot = await screenshots.CaptureAsync(new ScreenshotRequest("http://127.0.0.1:1/", 800, 600));

        shot.Image.ShouldBeNull();
        shot.Problem.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_shot_after_a_change_shows_the_change_and_not_the_page_the_browser_saw_before()
    {
        if (Browser() is not { } options)
            return;

        using var site = new LocalSite("<body style='margin:0;background:#101317'>before");
        await using var screenshots = new HeadlessChromeScreenshotter(options, NullLogger<HeadlessChromeScreenshotter>.Instance);

        var before = await screenshots.CaptureAsync(new ScreenshotRequest(site.Url, 400, 300));
        site.Html = "<body style='margin:0;background:#101317'>after";
        var after = await screenshots.CaptureAsync(new ScreenshotRequest(site.Url, 400, 300));

        after.Image.ShouldNotBeNull().Png.ShouldNotBe(before.Image.ShouldNotBeNull().Png);
    }

    [Fact]
    public async Task A_phone_shot_lays_the_page_out_narrow_instead_of_shrinking_the_desktop_one()
    {
        if (Browser() is not { } options)
            return;

        // Red only where a narrow layout applies, and no viewport meta tag — the case Chrome's mobile emulation
        // renders 980 px wide and scales down, which would show the desktop layout in miniature.
        using var page = new LocalSite("<style>body{margin:0;background:#00ff00}@media (max-width:500px){body{background:#ff0000}}</style><body>");
        using var narrow = new LocalSite("<style>body{margin:0;background:#ff0000}</style><body>");
        await using var screenshots = new HeadlessChromeScreenshotter(options, NullLogger<HeadlessChromeScreenshotter>.Instance);

        var phone = await screenshots.CaptureAsync(new ScreenshotRequest(page.Url, 390, 844));
        var allRed = await screenshots.CaptureAsync(new ScreenshotRequest(narrow.Url, 390, 844));
        var desktop = await screenshots.CaptureAsync(new ScreenshotRequest(page.Url, 1280, 800));
        var desktopRed = await screenshots.CaptureAsync(new ScreenshotRequest(narrow.Url, 1280, 800));

        // Same pixels, same encoder: a phone shot of the page is the all-red page, and a desktop one isn't.
        Png(phone).ShouldBe(Png(allRed));
        Png(desktop).ShouldNotBe(Png(desktopRed));
    }

    /// <summary>The shot's picture, or a failure that says why there isn't one.</summary>
    private static byte[] Png(ScreenshotOutcome shot) => shot.Image.ShouldNotBeNull(shot.Problem).Png;

    /// <summary>
    /// Options pointing at a browser to drive, or null when this machine has none and the test can only pass by
    /// doing nothing. FLEET_TEST_CHROME names one that <see cref="ChromeFinder"/> wouldn't find (a checkout of
    /// Playwright's Chromium, say), so the capture path can be exercised on a machine without Chrome installed.
    /// </summary>
    private static FleetOptions? Browser()
    {
        var path = Environment.GetEnvironmentVariable("FLEET_TEST_CHROME");
        if (string.IsNullOrEmpty(path))
            return ChromeFinder.Find() is null ? null : new FleetOptions();
        if (!File.Exists(path))
            return null;

        var options = new FleetOptions();
        options.Browser.ChromePath = path;
        return options;
    }

    /// <summary>PNG width and height, from the IHDR chunk that always comes first.</summary>
    private static (int Width, int Height) Size(byte[] png)
        => (BinaryPrimitives(png, 16), BinaryPrimitives(png, 20));

    private static int BinaryPrimitives(byte[] png, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));

    /// <summary>A page served on a loopback port, so the test doesn't depend on anything outside this machine.</summary>
    private sealed class LocalSite : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LocalSite(string html)
        {
            Html = html;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        context.Response.ContentType = "text/html; charset=utf-8";
                        // What a static file server sends, and what makes a browser cache a page it wasn't
                        // told to: no Cache-Control, but something to date the file by.
                        context.Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("R");
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Html));
                        context.Response.Close();
                    }
                    catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public string Url { get; }

        /// <summary>What the page says now; changing it is a developer editing their app.</summary>
        public string Html { get; set; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }
}
