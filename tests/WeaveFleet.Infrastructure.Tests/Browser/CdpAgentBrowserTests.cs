using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

/// <summary>
/// The agent's browser against a real headless Chrome and pages served on loopback. Skipped (passes doing nothing)
/// on a machine without a browser; FLEET_TEST_CHROME names one ChromeFinder wouldn't find.
/// </summary>
[Collection(HeadlessChrome.Collection)]
public sealed class CdpAgentBrowserTests
{
    private const string Session = "session-1";
    private const string User = "local-user";

    private const string Signup = """
        <!doctype html><title>Signup demo</title>
        <h1>Join the beta</h1>
        <form onsubmit="event.preventDefault();const n=document.getElementById('name').value;document.getElementById('msg').textContent='Saved, '+n+'!';console.log('saved',n);">
          <label for="name">Your name</label><input id="name">
          <label for="plan">Plan</label><select id="plan"><option value="free">Free</option><option value="pro">Pro</option></select>
          <label><input type="checkbox" id="terms"> I agree</label>
          <button type="submit">Save</button>
        </form>
        <div id="msg" role="status"></div>
        <a href="OTHER">Elsewhere</a>
        <button id="warn" onclick="alert('Name is required')">Check</button>
        <button id="broken" onclick="fetch('/missing')">Load</button>
        """;

    [Fact]
    public async Task An_agent_fills_the_form_clicks_save_and_reads_the_result_and_the_console()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        var steps = new Steps();
        await using var browser = Browser(options, steps, Limits(site));

        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        tab.Title.ShouldBe("Signup demo");

        var before = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id });
        var name = Ref(before.Content!, "[textbox] \"Your name\"");
        var save = Ref(before.Content!, "[button] \"Save\"");

        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Fill) { TabId = tab.Id, Ref = name, Text = "Ada" })).Ok.ShouldBeTrue();
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Click) { TabId = tab.Id, Ref = save })).Ok.ShouldBeTrue();

        var after = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Find) { TabId = tab.Id, Text = "Saved" });
        after.Content.ShouldNotBeNull().ShouldContain("Saved, Ada!");
        var console = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Console) { TabId = tab.Id });
        console.Console.ShouldNotBeNull().ShouldContain(entry => entry.Text == "saved Ada" && entry.Level == "info");

        var shot = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Screenshot) { TabId = tab.Id });
        shot.Image.ShouldNotBeNull().Take(4).ShouldBe(new byte[] { 137, 80, 78, 71 });

        // Each action is a step in plain words, with the element it acted on and where it was.
        steps.All.Select(step => step.Summary).ShouldBe([
            $"Opened 127.0.0.1:{site.Port} in its own tab",
            "Read the page: 9 controls",
            "Typed “Ada” in “Your name”",
            "Clicked “Save”",
            "Looked for “Saved”: 1 match",
            "Read the console: 1 line, no errors",
            "Took a screenshot",
        ]);
        steps.All[3].Box.ShouldNotBeNull().Width.ShouldBeGreaterThan(0);
        steps.All[3].Detail.ShouldStartWith("click @" + save.TrimStart('@'));
    }

    [Fact]
    public async Task Pages_outside_the_sessions_own_are_refused_and_a_link_to_one_is_blocked()
    {
        if (Chrome() is not { } options)
            return;
        using var other = new LocalSite("<title>Other</title>elsewhere");
        using var site = new LocalSite(Signup.Replace("OTHER", other.Url, StringComparison.Ordinal));
        await using var browser = Browser(options, new Steps(), Limits(site));

        var refused = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = other.Url });
        refused.Failure.ShouldNotBeNull().Code.ShouldBe(AgentBrowserFailures.Denied);
        refused.Failure.Message.ShouldContain("this session's own pages");

        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        var page = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id });
        await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Click) { TabId = tab.Id, Ref = Ref(page.Content!, "[link] \"Elsewhere\"") });

        // The tab shows Fleet's page saying why, the agent is told, and the other app never saw the request.
        var now = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsList))).Tabs.ShouldNotBeNull().ShouldHaveSingleItem();
        now.LoadError.ShouldNotBeNull().ShouldContain("Fleet blocked " + other.Url);
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id })).Content.ShouldNotBeNull().ShouldContain("Fleet blocked this page");
        other.Hits.ShouldBe(0);
    }

    [Fact]
    public async Task Scripts_run_only_when_the_user_allows_them()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        var allowed = false;
        await using var browser = Browser(options, new Steps(), _ => Limits(site)(Session) with { Settings = new AgentBrowserSettings(Scripts: allowed) });
        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();

        var off = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Evaluate) { TabId = tab.Id, Script = "document.title" });
        off.Failure.ShouldNotBeNull().Code.ShouldBe(AgentBrowserFailures.Denied);

        allowed = true;
        var on = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Evaluate) { TabId = tab.Id, Script = "document.title" });
        on.Value.ShouldNotBeNull().GetString().ShouldBe("Signup demo");
    }

    [Fact]
    public async Task A_dialog_stops_the_action_and_the_agent_answers_it()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        await using var browser = Browser(options, new Steps(), Limits(site));
        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        var page = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id });

        var clicked = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Click) { TabId = tab.Id, Ref = Ref(page.Content!, "[button] \"Check\"") });
        clicked.Failure.ShouldNotBeNull().Message.ShouldContain("Name is required");

        var blocked = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id });
        blocked.Failure.ShouldNotBeNull().Message.ShouldContain("dialog is open");

        var answered = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Dialog) { TabId = tab.Id, DialogAction = "accept" });
        answered.Dialog.ShouldNotBeNull().Message.ShouldBe("Name is required");
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id })).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Dropdowns_checkboxes_and_keys_work_like_a_person_using_them()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        await using var browser = Browser(options, new Steps(), Limits(site));
        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        var page = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id })).Content!;

        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Select) { TabId = tab.Id, Ref = Ref(page, "[combobox] \"Plan\""), Values = ["pro"] })).Ok.ShouldBeTrue();
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Check) { TabId = tab.Id, Ref = Ref(page, "[checkbox] \"I agree\""), Checked = true })).Ok.ShouldBeTrue();
        var wrong = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Select) { TabId = tab.Id, Ref = Ref(page, "[combobox] \"Plan\""), Values = ["Pro"] });
        wrong.Failure.ShouldNotBeNull().Message.ShouldContain("options are");

        await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Fill) { TabId = tab.Id, Ref = Ref(page, "[textbox] \"Your name\""), Text = "Grace" });
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Press) { TabId = tab.Id, Key = "Enter" })).Ok.ShouldBeTrue();
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Wait) { TabId = tab.Id, Condition = "text", Text = "Saved, Grace!" })).Ok.ShouldBeTrue();

        var after = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id })).Content!;
        after.ShouldContain("[checkbox] \"I agree\" checked=true");
    }

    [Fact]
    public async Task A_failed_request_shows_in_the_tabs_requests()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        await using var browser = Browser(options, new Steps(), Limits(site));
        var tab = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        var page = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = tab.Id })).Content!;

        await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Click) { TabId = tab.Id, Ref = Ref(page, "[button] \"Load\"") });
        await Task.Delay(500);

        var requests = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.NetworkList) { TabId = tab.Id, UrlContains = "/missing" })).Requests.ShouldNotBeNull();
        var missing = requests.ShouldHaveSingleItem();
        missing.StatusCode.ShouldBe(404);
        missing.ResourceType.ShouldBe("fetch");
    }

    [Fact]
    public async Task A_tab_behind_another_sessions_tab_still_takes_clicks_at_once_and_its_picture_is_current()
    {
        if (Chrome() is not { } options)
            return;
        using var site = new LocalSite(Signup);
        await using var browser = Browser(options, new Steps(), Limits(site));

        // Two sessions, a tab each; the second one opened is in front.
        var mine = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url })).Tab.ShouldNotBeNull();
        (await browser.RunAsync(new AgentBrowserCall("session-2", User, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = site.Url }))).Ok.ShouldBeTrue();
        var page = (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = mine.Id })).Content!;
        await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Fill) { TabId = mine.Id, Ref = Ref(page, "[textbox] \"Your name\""), Text = "Grace" });

        var clicking = System.Diagnostics.Stopwatch.StartNew();
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Click) { TabId = mine.Id, Ref = Ref(page, "[button] \"Save\"") })).Ok.ShouldBeTrue();
        clicking.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        // Another session's tab goes in front again; the picture of mine still shows what it did.
        await browser.RunAsync(new AgentBrowserCall("session-2", User, new AgentBrowserAction(AgentBrowserKinds.TabsList)));
        (await browser.FrameAsync(Session, mine.Id)).ShouldNotBeNull().Length.ShouldBeGreaterThan(1000);
        (await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Find) { TabId = mine.Id, Text = "Saved" })).Content.ShouldNotBeNull().ShouldContain("Saved, Grace!");
    }

    [Fact]
    public async Task With_the_browser_off_nothing_runs()
    {
        await using var browser = Browser(new FleetOptions(), new Steps(), _ => new AgentBrowserLimits(new AgentBrowserSettings(Enabled: false), new HashSet<int>(), null, []));

        var result = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.TabsOpen) { Url = "http://127.0.0.1:1/" });

        result.Failure.ShouldNotBeNull().Code.ShouldBe(AgentBrowserFailures.Off);
    }

    [Fact]
    public async Task A_tab_that_isnt_the_sessions_is_unavailable()
    {
        await using var browser = Browser(new FleetOptions(), new Steps(), _ => new AgentBrowserLimits(new AgentBrowserSettings(), new HashSet<int>(), null, []));

        var result = await Run(browser, new AgentBrowserAction(AgentBrowserKinds.Snapshot) { TabId = "tab_00000000-0000-0000-0000-000000000000" });

        result.Failure.ShouldNotBeNull().Code.ShouldBe(AgentBrowserFailures.TabUnavailable);
    }

    [Fact]
    public void The_sessions_own_pages_are_its_apps_ports_and_its_page_canvases_on_Fleet()
    {
        var limits = new AgentBrowserLimits(new AgentBrowserSettings(), new HashSet<int> { 5173 }, "http://127.0.0.1:2113", ["/pages/p1/"]);

        limits.Allows(new Uri("http://localhost:5173/settings")).ShouldBeTrue();
        limits.Allows(new Uri("http://127.0.0.1:2113/pages/p1/index.html")).ShouldBeTrue();
        limits.Allows(new Uri("http://127.0.0.1:2113/api/sessions")).ShouldBeFalse();
        limits.Allows(new Uri("http://localhost:3000/")).ShouldBeFalse();
        limits.Allows(new Uri("https://example.com/")).ShouldBeFalse();
        limits.Allows(new Uri("about:blank")).ShouldBeTrue();
        (limits with { Settings = new AgentBrowserSettings(Pages: AgentBrowserPages.Machine) }).Allows(new Uri("http://localhost:3000/")).ShouldBeTrue();
        (limits with { Settings = new AgentBrowserSettings(Pages: AgentBrowserPages.Any) }).Allows(new Uri("https://example.com/")).ShouldBeTrue();
        limits.Allows(new Uri("file:///etc/passwd")).ShouldBeFalse();
    }

    [Fact]
    public void Settings_default_to_on_own_pages_and_no_scripts()
    {
        AgentBrowserSettings.From(new Dictionary<string, string>()).ShouldBe(new AgentBrowserSettings(true, AgentBrowserPages.Session, false));
        AgentBrowserSettings.From(new Dictionary<string, string>
        {
            [AgentBrowserSettings.EnabledKey] = "false",
            [AgentBrowserSettings.PagesKey] = "any",
            [AgentBrowserSettings.ScriptsKey] = "true",
        }).ShouldBe(new AgentBrowserSettings(false, AgentBrowserPages.Any, true));
        AgentBrowserSettings.From(new Dictionary<string, string> { [AgentBrowserSettings.PagesKey] = "nonsense" }).Pages.ShouldBe(AgentBrowserPages.Session);
    }

    private static async Task<AgentBrowserResult> Run(CdpAgentBrowser browser, AgentBrowserAction action)
        => await browser.RunAsync(new AgentBrowserCall(Session, User, action));

    /// <summary>The ref on the snapshot line that contains <paramref name="line"/>.</summary>
    private static string Ref(string snapshot, string line)
    {
        var found = snapshot.Split('\n').FirstOrDefault(l => l.Contains(line, StringComparison.Ordinal));
        found.ShouldNotBeNull($"no \"{line}\" in:\n{snapshot}");
        return found.Trim().Split(' ')[0];
    }

    private static Func<string, AgentBrowserLimits> Limits(LocalSite site)
        => _ => new AgentBrowserLimits(new AgentBrowserSettings(), new HashSet<int> { site.Port }, null, []);

    private static CdpAgentBrowser Browser(FleetOptions options, Steps steps, Func<string, AgentBrowserLimits> limits)
        => new(
            new ChromeHost(options, NullLogger<ChromeHost>.Instance),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new NoUserScope(),
            NullLogger<CdpAgentBrowser>.Instance,
            steps)
        {
            LimitsOverride = limits,
        };

    private static FleetOptions? Chrome()
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

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class Steps : IAgentBrowserSteps
    {
        private readonly ConcurrentQueue<AgentBrowserStep> _steps = new();

        public IReadOnlyList<AgentBrowserStep> All => [.. _steps];

        public Task<AgentBrowserStep> RecordAsync(AgentBrowserStep entry, string userId, CancellationToken ct = default)
        {
            var recorded = entry with { Seq = _steps.Count + 1 };
            _steps.Enqueue(recorded);
            return Task.FromResult(recorded);
        }

        public Task<IReadOnlyList<AgentBrowserStep>> ListAsync(string sessionId, int limit = 500, CancellationToken ct = default)
            => Task.FromResult(All);

        public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Pages on a loopback port: <c>/</c> is the page, a favicon so the console stays quiet, anything else a 404.</summary>
    private sealed class LocalSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _hits;

        public LocalSite(string html)
        {
            Port = FreePort();
            Url = $"http://127.0.0.1:{Port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        Interlocked.Increment(ref _hits);
                        if (context.Request.Url!.AbsolutePath == "/favicon.ico")
                        {
                            context.Response.StatusCode = 204;
                        }
                        else if (context.Request.Url!.AbsolutePath == "/")
                        {
                            context.Response.ContentType = "text/html; charset=utf-8";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html));
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

        public int Port { get; }
        public string Url { get; }
        public int Hits => Volatile.Read(ref _hits);

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
