using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Pages;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Checks a page in Fleet's headless browser (<see cref="ChromeHost"/>), in a tab of its own: loads it once while
/// listening for script errors and files that don't load, then measures the layout at each of
/// <see cref="IPageChecker.Widths"/> for anything wider than the window.
/// </summary>
public sealed class HeadlessChromePageChecker : IPageChecker, IAsyncDisposable
{
    /// <summary>Shorter than a screenshot's wait: the agent is waiting on the tool, and the user can already see the page.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    private const int WindowHeight = 800;

    /// <summary>Enough for the model to act on; a page with more is broken in one way it'll see from the first few.</summary>
    private const int MaxFindings = 8;

    private const int MaxFindingLength = 300;

    private static readonly Action<ILogger, string, int, long, Exception?> LogChecked =
        LoggerMessage.Define<string, int, long>(LogLevel.Information, new EventId(1, "PageChecked"),
            "Checked {Url}: {Findings} findings in {Elapsed} ms");

    private static readonly Action<ILogger, string, Exception?> LogFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "PageCheckFailed"), "Checking a page failed: {Problem}");

    /// <summary>
    /// What the page looks like at the window's width, as JSON: whether it shows anything, whether it scrolls sideways
    /// or is cut off at the right edge, and the first few elements that stick out. An element sticks out when its box
    /// goes past the window and its parent's doesn't (a wide table), or when its own content spills out of a box that
    /// fits and none of its children do (a long line in a pre). Anything inside a box that scrolls or clips is fine:
    /// that's the page's own choice. Fixed elements don't make a page scroll, so off-screen drawers don't count.
    /// </summary>
    private const string LayoutScript = """
        (() => {
          const root = document.documentElement, body = document.body;
          const W = root.clientWidth;
          const style = el => getComputedStyle(el);
          const result = { blank: false, scrolls: false, clipped: false, scrollWidth: root.scrollWidth, found: [] };
          if (!body) return JSON.stringify({ ...result, blank: true });
          result.blank = !body.innerText.trim() && !body.querySelector('img,svg,canvas,video,iframe,picture,object,embed');
          result.scrolls = root.scrollWidth > W + 1;
          result.clipped = ['hidden', 'clip'].some(v => style(body).overflowX === v || style(root).overflowX === v);
          if (!result.scrolls && !result.clipped) return JSON.stringify(result);
          const inside = el => {
            for (let a = el.parentElement; a && a !== body && a !== root; a = a.parentElement) {
              const s = style(a);
              if (s.overflowX !== 'visible' || s.position === 'fixed') return true;
            }
            return false;
          };
          const reach = el => {
            const r = el.getBoundingClientRect();
            return style(el).overflowX === 'visible' ? Math.max(r.right, r.left + el.scrollWidth) : r.right;
          };
          const name = el => {
            const tag = el.tagName.toLowerCase();
            const id = el.id ? '#' + el.id : [...el.classList].slice(0, 2).map(c => '.' + c).join('');
            const text = (el.textContent || '').trim().replace(/\s+/g, ' ');
            return tag + id + (text ? ' ("' + (text.length > 40 ? text.slice(0, 40) + '…' : text) + '")' : '');
          };
          let seen = 0;
          for (const el of body.querySelectorAll('*')) {
            if (++seen > 5000 || result.found.length >= 3) break;
            const s = style(el);
            if (s.position === 'fixed' || s.display === 'none' || s.visibility === 'hidden' || inside(el)) continue;
            const r = el.getBoundingClientRect();
            if (!r.width && !r.height) continue;
            if (r.right > W + 1 && el.parentElement.getBoundingClientRect().right <= W + 1)
              result.found.push({ name: name(el), reach: Math.round(r.right) });
            else if (r.right <= W + 1 && s.overflowX === 'visible' && r.left + el.scrollWidth > W + 1
                     && ![...el.children].some(c => reach(c) > W + 1))
              result.found.push({ name: name(el), reach: Math.round(r.left + el.scrollWidth) });
          }
          return JSON.stringify(result);
        })()
        """;

    /// <summary>Two frames after a resize, so the layout the script measures is the new one.</summary>
    private const string NextFrames =
        "new Promise(done => { requestAnimationFrame(() => requestAnimationFrame(done)); setTimeout(done, 300); })";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChromeHost _host;
    private readonly bool _ownsHost;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Checks with the browser <paramref name="host"/> keeps, shared with screenshots and the agents' tabs.</summary>
    public HeadlessChromePageChecker(ChromeHost host, ILogger<HeadlessChromePageChecker> logger)
    {
        _host = host;
        _logger = logger;
    }

    /// <summary>Checks with a browser of its own, quit when this is disposed.</summary>
    public HeadlessChromePageChecker(FleetOptions options, ILogger<HeadlessChromePageChecker> logger)
        : this(new ChromeHost(options, NullLogger<ChromeHost>.Instance), logger)
    {
        _ownsHost = true;
    }

    public async Task<PageCheckOutcome> CheckAsync(string url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return PageCheckOutcome.Fail("Fleet is shutting down.");

            using var lease = await _host.AcquireAsync(ct);
            if (lease.Connection is not { } cdp)
                return PageCheckOutcome.Fail(lease.Problem ?? ChromeFinder.NotFound);

            try
            {
                var started = Stopwatch.StartNew();
                var outcome = await CheckAsync(cdp, url, ct);
                if (outcome.Problem is { } problem)
                    LogFailed(_logger, problem, null);
                else
                    LogChecked(_logger, url, outcome.Findings.Count, started.ElapsedMilliseconds, null);
                return outcome;
            }
            catch (CdpException error)
            {
                // A browser that died mid-check shouldn't poison the next call.
                await _host.ResetAsync(cdp);
                LogFailed(_logger, error.Message, null);
                return PageCheckOutcome.Fail(error.Message);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A reply in a shape the check didn't expect: the page is still shown, just unchecked.
                LogFailed(_logger, error.Message, null);
                return PageCheckOutcome.Fail("the browser's answer about the page didn't make sense");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<PageCheckOutcome> CheckAsync(CdpConnection cdp, string url, CancellationToken ct)
    {
        var widths = IPageChecker.Widths;
        await using var tab = await HeadlessTab.OpenAsync(cdp, widths[0], WindowHeight, ct);
        var heard = new PageEvents(new Uri(new Uri(url), "."));
        using (cdp.Subscribe(tab.Session, heard.On))
        {
            (await cdp.SendAsync("Runtime.enable", sessionId: tab.Session, ct: ct)).Dispose();
            (await cdp.SendAsync("Log.enable", sessionId: tab.Session, ct: ct)).Dispose();

            var (error, loaded) = await tab.NavigateAsync(url, LoadTimeout, ct);
            if (error is not null)
                return PageCheckOutcome.Fail($"the browser couldn't open {url}: {error}");

            var findings = new List<string>();
            if (!loaded)
                findings.Add($"The page hadn't finished loading after {LoadTimeout.TotalSeconds:0} seconds: something it loads is slow or never answers.");
            await Task.Delay(HeadlessChromeScreenshotter.SettleDelay, ct);

            var layouts = new List<string>();
            for (var i = 0; i < widths.Length; i++)
            {
                if (i > 0)
                {
                    await tab.ResizeAsync(widths[i], WindowHeight, ct);
                    (await EvaluateAsync(cdp, tab, NextFrames, awaitPromise: true, ct)).Dispose();
                }

                using var reply = await EvaluateAsync(cdp, tab, LayoutScript, awaitPromise: false, ct);
                if (Layout(reply.RootElement, widths[i], first: i == 0) is { } found)
                    layouts.AddRange(found);
            }

            // Errors come in while the page loads and while it's resized, so they're read last.
            findings.AddRange(heard.Findings());
            findings.AddRange(layouts);
            return PageCheckOutcome.Found(Trim(findings));
        }
    }

    private static Task<JsonDocument> EvaluateAsync(CdpConnection cdp, HeadlessTab tab, string expression, bool awaitPromise, CancellationToken ct)
        => cdp.SendAsync("Runtime.evaluate", write =>
        {
            write.WriteString("expression", expression);
            write.WriteBoolean("returnByValue", true);
            write.WriteBoolean("awaitPromise", awaitPromise);
        }, tab.Session, ct);

    /// <summary>What <see cref="LayoutScript"/> found at one width, as findings.</summary>
    private static List<string>? Layout(JsonElement reply, int width, bool first)
    {
        var result = reply.GetProperty("result").GetProperty("result");
        if (!result.TryGetProperty("value", out var value) || value.GetString() is not { } json)
            return null;

        using var parsed = JsonDocument.Parse(json);
        var layout = parsed.RootElement;
        var findings = new List<string>();
        // Nothing on the page is the same at every width: say it once.
        if (first && layout.GetProperty("blank").GetBoolean())
            findings.Add("The page shows nothing: no text, images or drawings once it had loaded.");

        var found = layout.GetProperty("found").EnumerateArray()
            .Select(item => $"{item.GetProperty("name").GetString()} reaches {item.GetProperty("reach").GetInt32()} px")
            .ToList();
        if (layout.GetProperty("scrolls").GetBoolean())
        {
            findings.Add(found.Count > 0
                ? $"At {width} px wide the page scrolls sideways: {string.Join("; ", found)}."
                : $"At {width} px wide the page scrolls sideways, to {layout.GetProperty("scrollWidth").GetInt32()} px.");
        }
        else if (layout.GetProperty("clipped").GetBoolean() && found.Count > 0)
        {
            findings.Add($"At {width} px wide content is cut off at the right edge: {string.Join("; ", found)}.");
        }

        return findings;
    }

    private static List<string> Trim(List<string> findings)
    {
        var kept = findings.Distinct().Take(MaxFindings)
            .Select(finding => finding.Length > MaxFindingLength ? finding[..MaxFindingLength] + "…" : finding)
            .ToList();
        var more = findings.Distinct().Count() - kept.Count;
        if (more > 0)
            kept.Add($"…and {more} more.");
        return kept;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            if (_ownsHost)
                await _host.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The script errors and failed files a page reports while it loads, in the order they came. Events arrive on the
    /// connection's reader, so everything here takes the lock.
    /// </summary>
    private sealed class PageEvents(Uri folder)
    {
        private readonly Dictionary<string, string> _urls = [];
        private readonly List<string> _errors = [];
        private readonly List<string> _files = [];

        public void On(string method, JsonElement p)
        {
            lock (this)
            {
                switch (method)
                {
                    case "Network.requestWillBeSent" when p.TryGetProperty("request", out var request):
                        _urls[p.GetProperty("requestId").GetString()!] = request.GetProperty("url").GetString() ?? string.Empty;
                        break;
                    case "Network.responseReceived" when p.TryGetProperty("response", out var response)
                                                         && response.GetProperty("status").GetInt32() is var status and >= 400:
                        AddFile(response.GetProperty("url").GetString(), $"the server answered {status}");
                        break;
                    case "Network.loadingFailed" when !(p.TryGetProperty("canceled", out var canceled) && canceled.GetBoolean()):
                        _urls.TryGetValue(p.GetProperty("requestId").GetString()!, out var failed);
                        AddFile(failed, p.GetProperty("errorText").GetString() ?? "it failed");
                        break;
                    case "Runtime.exceptionThrown" when p.TryGetProperty("exceptionDetails", out var details):
                        var description = details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var d)
                            ? d.GetString()
                            : details.TryGetProperty("text", out var t) ? t.GetString() : null;
                        _errors.Add($"Script error: {FirstLine(description) ?? "an exception"}{Where(details)}.");
                        break;
                    case "Runtime.consoleAPICalled" when p.GetProperty("type").GetString() == "error":
                        var text = string.Join(' ', p.GetProperty("args").EnumerateArray().Select(Text));
                        _errors.Add($"The page logged an error: {FirstLine(text)}");
                        break;
                    // Files that didn't load are already heard from the network, in the page's own words.
                    case "Log.entryAdded" when p.TryGetProperty("entry", out var entry)
                                               && entry.GetProperty("level").GetString() == "error"
                                               && entry.GetProperty("source").GetString() != "network":
                        _errors.Add($"The browser reported: {FirstLine(entry.GetProperty("text").GetString())}");
                        break;
                }
            }
        }

        public List<string> Findings()
        {
            lock (this)
                return [.. _errors, .. _files];
        }

        private void AddFile(string? url, string why)
        {
            // Chrome asks for an icon nobody wrote; its absence isn't the page's problem.
            if (string.IsNullOrEmpty(url) || url.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                return;
            _files.Add($"{Name(url)} didn't load: {why}.");
        }

        /// <summary>A file of the page by its path in the page's folder, anything else by its address.</summary>
        private string Name(string url)
            => url.StartsWith(folder.AbsoluteUri, StringComparison.Ordinal) ? Uri.UnescapeDataString(url[folder.AbsoluteUri.Length..]) : url;

        private string Where(JsonElement details)
        {
            if (!details.TryGetProperty("url", out var url) || url.GetString() is not { Length: > 0 } source)
                return string.Empty;
            // CDP counts lines from 0; editors count from 1.
            var line = details.TryGetProperty("lineNumber", out var number) ? $":{number.GetInt32() + 1}" : string.Empty;
            return $" ({Name(source)}{line})";
        }

        private static string Text(JsonElement arg)
            => arg.TryGetProperty("value", out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
                : arg.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty;

        private static string? FirstLine(string? text)
            => text?.Split('\n', 2)[0].Trim();
    }
}
