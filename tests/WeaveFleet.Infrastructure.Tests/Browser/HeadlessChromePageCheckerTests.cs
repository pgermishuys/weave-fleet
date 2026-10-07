using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Pages;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

[Collection(HeadlessChrome.Collection)]
public sealed class HeadlessChromePageCheckerTests
{
    private const string Viewport = "<meta name='viewport' content='width=device-width, initial-scale=1'>";

    [Fact]
    public async Task Without_a_browser_on_the_machine_the_check_says_why_it_did_not_run()
    {
        var options = new FleetOptions();
        options.Browser.ChromePath = Path.Combine(Path.GetTempPath(), "no-such-chrome");
        await using var checker = new HeadlessChromePageChecker(options, NullLogger<HeadlessChromePageChecker>.Instance);

        var check = await checker.CheckAsync("http://127.0.0.1:1/");

        check.Findings.ShouldBeEmpty();
        check.Problem.ShouldBe(ChromeFinder.NotFound);
    }

    [Fact]
    public async Task A_page_that_works_at_both_widths_has_nothing_to_report()
    {
        if (Browser() is not { } options)
            return;

        // A wide table in a box that scrolls is the page's own choice, not a page that scrolls sideways.
        using var site = new LocalSite(new()
        {
            ["/report/index.html"] = $"""
                {Viewport}<link rel="stylesheet" href="style.css">
                <h1>Results</h1>
                <div style="overflow-x:auto"><table style="width:900px"><tr><td>Run</td><td>Passed</td></tr></table></div>
                """,
            ["/report/style.css"] = "body { margin: 0 16px; font: 16px system-ui; }",
        });
        await using var checker = new HeadlessChromePageChecker(options, NullLogger<HeadlessChromePageChecker>.Instance);

        var check = await checker.CheckAsync(site.Url("/report/index.html"));

        check.Problem.ShouldBeNull();
        check.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Script_errors_and_files_that_did_not_load_are_named_by_their_place_in_the_page()
    {
        if (Browser() is not { } options)
            return;

        using var site = new LocalSite(new()
        {
            ["/report/index.html"] = $"""
                {Viewport}<h1>Results</h1>
                <script src="charts/chart.js"></script>
                <script>
                  console.error("no data for run 4");
                  drawChart();
                </script>
                """,
        });
        await using var checker = new HeadlessChromePageChecker(options, NullLogger<HeadlessChromePageChecker>.Instance);

        var check = await checker.CheckAsync(site.Url("/report/index.html"));

        check.Problem.ShouldBeNull();
        check.Findings.ShouldBe(
        [
            "The page logged an error: no data for run 4",
            "Script error: ReferenceError: drawChart is not defined (index.html:5).",
            "charts/chart.js didn't load: the server answered 404.",
        ]);
    }

    [Fact]
    public async Task Content_wider_than_a_phone_is_named_with_how_far_it_reaches()
    {
        if (Browser() is not { } options)
            return;

        // The case the design skill's phone screenshot kept missing: a long command in a card that fits.
        using var site = new LocalSite(new()
        {
            ["/site/index.html"] = Viewport + """
                <style>body{margin:0 16px} pre{margin:0}</style>
                <div class="card"><pre class="install">curl -fsSL https://get.example.com/install.sh | bash -s -- --channel=stable</pre></div>
                """,
        });
        await using var checker = new HeadlessChromePageChecker(options, NullLogger<HeadlessChromePageChecker>.Instance);

        var check = await checker.CheckAsync(site.Url("/site/index.html"));

        check.Problem.ShouldBeNull();
        var finding = check.Findings.ShouldHaveSingleItem();
        finding.ShouldStartWith("At 390 px wide the page scrolls sideways: pre.install (\"curl -fsSL https://get.example.com/insta…\") reaches 6");
    }

    [Fact]
    public async Task A_page_that_draws_nothing_says_so()
    {
        if (Browser() is not { } options)
            return;

        using var site = new LocalSite(new() { ["/app/index.html"] = $"{Viewport}<div id=\"app\"></div>" });
        await using var checker = new HeadlessChromePageChecker(options, NullLogger<HeadlessChromePageChecker>.Instance);

        var check = await checker.CheckAsync(site.Url("/app/index.html"));

        check.Findings.ShouldBe(["The page shows nothing: no text, images or drawings once it had loaded."]);
    }

    /// <summary>
    /// Options pointing at a browser to drive, or null when this machine has none and the test can only pass by
    /// doing nothing. FLEET_TEST_CHROME names one that <see cref="ChromeFinder"/> wouldn't find.
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

    /// <summary>Files served on a loopback port by path, like Fleet serves a page's folder; anything else is a 404.</summary>
    private sealed class LocalSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _root;

        public LocalSite(Dictionary<string, string> files)
        {
            _root = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(_root);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        var path = context.Request.Url!.AbsolutePath;
                        if (files.TryGetValue(path, out var body))
                        {
                            context.Response.ContentType = path.EndsWith(".css", StringComparison.Ordinal) ? "text/css"
                                : path.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript"
                                : "text/html; charset=utf-8";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                        }
                        else
                        {
                            context.Response.StatusCode = 404;
                        }

                        context.Response.Close();
                    }
                    catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public string Url(string path) => _root + path.TrimStart('/');

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
