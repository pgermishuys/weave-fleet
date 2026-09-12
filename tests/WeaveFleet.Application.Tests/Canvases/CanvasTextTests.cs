using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasTextTests
{
    private static Canvas Diagram(string stateJson, string title = "Session event flow") => new()
    {
        Id = "cv_01J",
        Kind = CanvasKinds.Diagram,
        Title = title,
        Version = 5,
        StateJson = stateJson,
    };

    [Fact]
    public void RenderFull_Diagram_IsCompactAndLeavesOutPositions()
    {
        var canvas = Diagram("""
            {"direction":"TB",
             "nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions","x":20,"y":44,"placedByUser":true},
                      {"id":"n3","label":"SessionEventsHub","detail":"Api/Hubs"},
                      {"id":"n5","label":"Client\nstore"}],
             "edges":[{"id":"e1","from":"n1","to":"n3","label":"publishes","style":"solid"},
                      {"id":"e9","from":"n3","to":"n5","label":"SessionListChanged","style":"planned"},
                      {"id":"e10","from":"n5","to":"n1","style":"dashed"}]}
            """);

        CanvasText.RenderFull(canvas).ShouldBe("""
            diagram cv_01J "Session event flow" v5 TB
            n1 NuCode session · NuCode/Sessions
            n3 SessionEventsHub · Api/Hubs
            n5 Client store
            e1 n1 -> n3 publishes
            e9 n3 -> n5 SessionListChanged [planned]
            e10 n5 -> n1 [dashed]
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void RenderFull_EmptyDiagram_SaysSo()
    {
        CanvasText.RenderFull(Diagram("""{"direction":"LR","nodes":[],"edges":[]}""", title: "Say \"hi\""))
            .ShouldBe("diagram cv_01J \"Say \\\"hi\\\"\" v5 LR\n(no boxes)");
    }

    [Fact]
    public void RenderFull_Sequence_IsHeaderAndSource()
    {
        var canvas = new Canvas
        {
            Id = "cv_02",
            Kind = CanvasKinds.Sequence,
            Title = "Open flow",
            Version = 3,
            StateJson = """{"source":"sequenceDiagram\n  Agent->>Fleet: fleet_canvas_open"}""",
        };

        CanvasText.RenderFull(canvas).ShouldBe("sequence cv_02 \"Open flow\" v3\nsequenceDiagram\n  Agent->>Fleet: fleet_canvas_open");
    }

    [Fact]
    public void RenderChangesSince_ListsRemovedBoxesAndTheirEdges()
    {
        CanvasText.RenderChangesSince(5, []).ShouldBe("No changes since v5.");
        CanvasText.RenderChangesSince(5, [
            new CanvasUserRemoval(6, "n7", "use-sessions.ts", ["e3", "e4"]),
            new CanvasUserRemoval(8, "n2", "Api", []),
            new CanvasUserRemoval(9, "n9", null, ["e5"]),
        ]).ShouldBe("""
            Since v5 the user removed:
            box "use-sessions.ts" (n7), with edges e3, e4
            box "Api" (n2)
            box n9, with edge e5
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void RenderListLine_FlagsCanvasesTheUserChanged()
    {
        var canvas = Diagram("{}");

        CanvasText.RenderListLine(canvas, changedByUser: false).ShouldBe("cv_01J diagram \"Session event flow\" v5");
        CanvasText.RenderListLine(canvas, changedByUser: true).ShouldBe("cv_01J diagram \"Session event flow\" v5 (changed by user)");
    }

    [Fact]
    public void Summarize_CountsEachKindOfChange()
    {
        CanvasText.Summarize([
            new AddNodeOp("n1", "A", null),
            new AddNodeOp("n2", "B", null),
            new RemoveEdgeOp("e1"),
        ]).ShouldBe("+2 boxes, −1 edge");

        CanvasText.Summarize([
            new RemoveNodeOp("n1") { RemovedEdgeIds = ["e1", "e2"] },
            new MoveNodeOp("n2", 0, 0),
            new MoveNodeOp("n2", 1, 1),
            new MoveNodeOp("n3", 1, 1),
        ]).ShouldBe("−1 box, 2 boxes moved, −2 edges");

        CanvasText.Summarize([]).ShouldBe("no changes");
    }
}
