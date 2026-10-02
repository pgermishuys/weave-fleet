using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// What a session's agent may do in its browser right now: the user's Settings → Browser, and with
/// <see cref="AgentBrowserPages.Session"/> the pages that are the session's own (its apps' ports, the pages its browser
/// canvases show, and the pages Fleet serves for its page canvases).
/// </summary>
public sealed record AgentBrowserLimits(
    AgentBrowserSettings Settings,
    IReadOnlySet<int> Ports,
    string? FleetOrigin,
    IReadOnlyList<string> PagePrefixes)
{
    /// <summary>Whether the agent's tab may load <paramref name="url"/> as its page.</summary>
    public bool Allows(Uri url)
    {
        if (url.AbsoluteUri == "about:blank")
            return true;
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            return false;
        if (Settings.Pages == AgentBrowserPages.Any)
            return true;
        if (!LoopbackUrl.IsLoopbackHost(url.Host))
            return false;
        if (Settings.Pages == AgentBrowserPages.Machine)
            return true;

        // Fleet's own address is the session's only for the pages it serves for the session's page canvases.
        if (FleetOrigin is not null && string.Equals(url.GetLeftPart(UriPartial.Authority), FleetOrigin, StringComparison.OrdinalIgnoreCase))
            return PagePrefixes.Any(prefix => url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal));
        return Ports.Contains(url.Port);
    }

    /// <summary>Why <paramref name="url"/> isn't allowed, and what to do instead.</summary>
    public string Refusal(Uri url) => Settings.Pages switch
    {
        AgentBrowserPages.Machine =>
            $"Fleet's browser settings only let agents open pages on this machine, and {url} isn't one. Ask the user to allow any address in Settings → Browser if you need it.",
        _ =>
            $"Fleet's browser settings only let agents open this session's own pages: apps it started (fleet_app_start) and pages in its canvases. {url} isn't one of them. "
            + "Start the app with fleet_app_start or show the page with fleet_browser_open first, or ask the user to allow more pages in Settings → Browser.",
    };
}

/// <summary>
/// Tells whoever needs it that a user changed Settings → Browser, e.g. a harness adapter that attaches or detaches
/// its sessions' browser when it's switched on or off.
/// </summary>
public sealed class AgentBrowserSettingsChanges
{
    public event Action<string, AgentBrowserSettings>? Changed;

    public void Notify(string userId, AgentBrowserSettings settings) => Changed?.Invoke(userId, settings);
}

/// <summary>Reads <see cref="AgentBrowserLimits"/> for a session, and the user's Settings → Browser. Runs as the user.</summary>
public sealed class AgentBrowserAccess(
    IUserPreferenceRepository preferences,
    AppRunService apps,
    ICanvasService canvases,
    ILocalFleetUrl? fleetUrl = null)
{
    public async Task<AgentBrowserSettings> SettingsAsync()
        => AgentBrowserSettings.From(await preferences.GetAllAsync());

    /// <summary>Saves the settings; null leaves a setting as it is. Returns them as saved.</summary>
    public async Task<AgentBrowserSettings> SaveAsync(bool? enabled, string? pages, bool? scripts)
    {
        if (enabled is { } on)
            await preferences.SetAsync(AgentBrowserSettings.EnabledKey, on ? "true" : "false");
        if (pages is not null)
            await preferences.SetAsync(AgentBrowserSettings.PagesKey, pages);
        if (scripts is { } allowed)
            await preferences.SetAsync(AgentBrowserSettings.ScriptsKey, allowed ? "true" : "false");
        return await SettingsAsync();
    }

    public async Task<AgentBrowserLimits> LimitsAsync(string sessionId, CancellationToken ct = default)
    {
        var settings = await SettingsAsync();
        var fleetOrigin = fleetUrl?.TryGet() is { } fleet && Uri.TryCreate(fleet, UriKind.Absolute, out var origin)
            ? origin.GetLeftPart(UriPartial.Authority)
            : null;
        if (!settings.Enabled || settings.Pages != AgentBrowserPages.Session)
            return new AgentBrowserLimits(settings, new HashSet<int>(), fleetOrigin, []);

        var ports = new HashSet<int>();
        foreach (var app in await apps.ListAsync(sessionId))
        {
            if (app.Port > 0)
                ports.Add(app.Port);
            ports.UnionWith(app.Ports);
        }

        List<string> pages = [];
        foreach (var item in await canvases.ListAsync(sessionId, ct))
        {
            var canvas = item.Canvas;
            if (canvas.Kind == CanvasKinds.Browser
                && BrowserState.Parse(canvas.StateJson).Url is { Length: > 0 } shown
                && LoopbackUrl.TryParse(shown, out var page))
            {
                ports.Add(page.Port);
            }
            else if (canvas.Kind == CanvasKinds.Page && PageState.Parse(canvas.StateJson).PageId is { Length: > 0 } pageId)
            {
                pages.Add($"/pages/{pageId}/");
            }
        }

        return new AgentBrowserLimits(settings, ports, fleetOrigin, pages);
    }
}
