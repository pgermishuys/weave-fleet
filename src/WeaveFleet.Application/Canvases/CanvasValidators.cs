using System.Text;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Canvases;

public static partial class CanvasValidators
{
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>Rejects serialized state over <see cref="CanvasLimits.MaxStateBytes"/>.</summary>
    public static CanvasError? CheckSize(string stateJson)
    {
        var bytes = Encoding.UTF8.GetByteCount(stateJson);
        return bytes <= CanvasLimits.MaxStateBytes
            ? null
            : new CanvasError(
                CanvasErrorKind.TooLarge,
                $"The canvas would be {(bytes + 1023) / 1024} KB; the limit is {CanvasLimits.MaxStateBytes / 1024} KB.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}$")]
    private static partial Regex IdPattern();
}

public static class BrowserStateValidator
{
    /// <summary>The page has to be on this machine. It can be empty while the app Fleet started has yet to serve one.</summary>
    public static CanvasError? Validate(BrowserState state)
        => LoopbackUrl.TryParse(state.Url, out _) || (state.Url.Length == 0 && !string.IsNullOrEmpty(state.AppId))
            ? null
            : new CanvasError(CanvasErrorKind.Invalid, LoopbackUrl.Requirement);
}

public static class PageStateValidator
{
    /// <summary>The page has to be one of Fleet's copies: the state only ever comes from <c>fleet_page_show</c>.</summary>
    public static CanvasError? Validate(PageState state)
        => WeaveFleet.Application.Pages.PageIds.IsValid(state.PageId) && state.Entry.Length > 0 && state.Source.Length > 0
            ? null
            : new CanvasError(CanvasErrorKind.Invalid, "A page canvas shows a page Fleet copied: show one with fleet_page_show.");
}

/// <summary>
/// The pages a browser canvas can show: http or https on this machine. Fleet proxies them, so anything
/// else would let a canvas make Fleet fetch arbitrary hosts.
/// </summary>
public static class LoopbackUrl
{
    public const string Requirement = "\"url\" must be an http or https address on this machine, e.g. http://localhost:5173/.";

    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!IsLoopbackHost(parsed.Host))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary><c>0.0.0.0</c> and <c>[::]</c> count: servers print the address they bound, and that means "this machine".</summary>
    public static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
           || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address)
               && (System.Net.IPAddress.IsLoopback(address)
                   || address.Equals(System.Net.IPAddress.Any)
                   || address.Equals(System.Net.IPAddress.IPv6Any)));
}
