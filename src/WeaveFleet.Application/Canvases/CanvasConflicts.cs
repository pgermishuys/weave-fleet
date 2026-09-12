using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>A box the user removed, and the edges that went with it.</summary>
public sealed record CanvasUserRemoval(int Version, string NodeId, string? Label, IReadOnlyList<string> EdgeIds);

/// <summary>
/// The conflict rule: an agent change is refused if it touches a box the user removed since the agent
/// last read or wrote the canvas, or an edge that went with that box. Moves never conflict, and a
/// reference to an id that never existed is left to <see cref="CanvasOps.Apply"/> as a plain error.
/// </summary>
public static class CanvasConflicts
{
    /// <summary>Collects the user's box removals from <paramref name="revisions"/>, oldest first.</summary>
    public static IReadOnlyList<CanvasUserRemoval> UserRemovals(IEnumerable<CanvasRevision> revisions)
    {
        var removals = new List<CanvasUserRemoval>();
        foreach (var revision in revisions.OrderBy(r => r.Version))
        {
            if (revision.Actor != CanvasRevision.UserActor)
                continue;

            foreach (var op in CanvasOps.ReadRecorded(revision.OpsJson))
            {
                if (op is RemoveNodeOp remove)
                    removals.Add(new CanvasUserRemoval(revision.Version, remove.Id, remove.RemovedLabel, remove.RemovedEdgeIds));
            }
        }

        return removals;
    }

    /// <summary>
    /// Returns a <see cref="CanvasErrorKind.Refused"/> error with one line per removed item that
    /// <paramref name="agentOps"/> touch, or <c>null</c> if they touch none.
    /// </summary>
    public static CanvasError? FindRefusal(IReadOnlyList<CanvasOp> agentOps, IReadOnlyList<CanvasUserRemoval> removalsSinceSeen)
    {
        if (removalsSinceSeen.Count == 0)
            return null;

        var removedNodes = new Dictionary<string, CanvasUserRemoval>(StringComparer.Ordinal);
        var removedEdges = new Dictionary<string, CanvasUserRemoval>(StringComparer.Ordinal);
        foreach (var removal in removalsSinceSeen)
        {
            removedNodes[removal.NodeId] = removal;
            foreach (var edgeId in removal.EdgeIds)
                removedEdges[edgeId] = removal;
        }

        var lines = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in agentOps)
        {
            foreach (var nodeId in NodeReferences(op))
            {
                if (removedNodes.TryGetValue(nodeId, out var removal) && reported.Add("box:" + nodeId))
                    lines.Add($"Refused: the user removed {CanvasText.BoxName(removal.Label, nodeId)} in v{removal.Version}.");
            }

            foreach (var edgeId in EdgeReferences(op))
            {
                if (removedEdges.TryGetValue(edgeId, out var removal) && reported.Add("edge:" + edgeId))
                    lines.Add($"Refused: edge {edgeId} went when the user removed {CanvasText.BoxName(removal.Label, removal.NodeId)} in v{removal.Version}.");
            }
        }

        if (lines.Count == 0)
            return null;

        lines[^1] += " " + CanvasText.ReadHint;
        return new CanvasError(CanvasErrorKind.Refused, string.Join('\n', lines));
    }

    private static IEnumerable<string> NodeReferences(CanvasOp op) => op switch
    {
        AddNodeOp add => [add.Id],
        UpdateNodeOp update => [update.Id],
        RemoveNodeOp remove => [remove.Id],
        AddEdgeOp add => [add.From, add.To],
        _ => [],
    };

    private static IEnumerable<string> EdgeReferences(CanvasOp op) => op switch
    {
        AddEdgeOp add => [add.Id],
        UpdateEdgeOp update => [update.Id],
        RemoveEdgeOp remove => [remove.Id],
        _ => [],
    };
}
