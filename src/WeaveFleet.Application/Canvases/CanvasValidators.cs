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

/// <summary>
/// Checks a whole diagram after a batch of ops: ids are valid and unique across boxes and edges,
/// edges point at boxes that exist, styles and direction are known, and the box limit holds.
/// </summary>
public static class DiagramStateValidator
{
    public static CanvasError? Validate(DiagramState state)
    {
        if (!DiagramDirections.All.Contains(state.Direction))
            return Invalid($"Direction must be one of: {string.Join(", ", DiagramDirections.All)}.");

        if (state.Nodes.Count > CanvasLimits.MaxDiagramNodes)
        {
            return new CanvasError(
                CanvasErrorKind.TooLarge,
                $"The diagram would have {state.Nodes.Count} boxes; the limit is {CanvasLimits.MaxDiagramNodes}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes)
        {
            if (!CanvasValidators.IsValidId(node.Id))
                return Invalid($"Box id \"{node.Id}\" must be 1-64 letters, digits, or _ . : - characters.");
            if (!ids.Add(node.Id))
                return Invalid($"Id \"{node.Id}\" is used more than once.");
            if (string.IsNullOrWhiteSpace(node.Label))
                return Invalid($"Box \"{node.Id}\" needs a label.");
        }

        var nodeIds = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var edge in state.Edges)
        {
            if (!CanvasValidators.IsValidId(edge.Id))
                return Invalid($"Edge id \"{edge.Id}\" must be 1-64 letters, digits, or _ . : - characters.");
            if (!ids.Add(edge.Id))
                return Invalid($"Id \"{edge.Id}\" is used more than once.");
            if (!nodeIds.Contains(edge.From))
                return Invalid($"Edge \"{edge.Id}\" starts at box \"{edge.From}\", which isn't on the diagram.");
            if (!nodeIds.Contains(edge.To))
                return Invalid($"Edge \"{edge.Id}\" ends at box \"{edge.To}\", which isn't on the diagram.");
            if (!DiagramEdgeStyles.All.Contains(edge.Style))
                return Invalid($"Edge \"{edge.Id}\" style must be one of: {string.Join(", ", DiagramEdgeStyles.All)}.");
        }

        return null;
    }

    private static CanvasError Invalid(string message) => new(CanvasErrorKind.Invalid, message);
}

public static class BrowserStateValidator
{
    public static CanvasError? Validate(BrowserState state)
        => LoopbackUrl.TryParse(state.Url, out _)
            ? null
            : new CanvasError(CanvasErrorKind.Invalid, LoopbackUrl.Requirement);
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

public static class SequenceStateValidator
{
    public static CanvasError? Validate(SequenceState state)
        => string.IsNullOrWhiteSpace(state.Source)
            ? new CanvasError(CanvasErrorKind.Invalid, "The sequence diagram needs Mermaid source.")
            : null;
}
