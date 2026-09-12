using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasOpsTests
{
    private const string TwoBoxes = """
        {
          "nodes": [
            { "id": "n1", "label": "NuCode session", "detail": "NuCode/Sessions" },
            { "id": "n2", "label": "SessionEventsHub" }
          ],
          "edges": [{ "id": "e1", "from": "n1", "to": "n2", "label": "publishes" }]
        }
        """;

    private static string OpenDiagram(string state = TwoBoxes)
    {
        var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonNode.Parse(state));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.Value.StateJson;
    }

    private static IReadOnlyList<CanvasOp> ParseOps(string ops, CanvasActor actor = CanvasActor.Agent, string kind = CanvasKinds.Diagram)
    {
        var parsed = CanvasOps.Parse(kind, actor, JsonNode.Parse(ops));
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        return parsed.Value;
    }

    private static CanvasChange ApplyOk(string stateJson, string ops, CanvasActor actor = CanvasActor.Agent)
    {
        var applied = CanvasOps.Apply(CanvasKinds.Diagram, actor, stateJson, ParseOps(ops, actor));
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
        return applied.Value;
    }

    private static CanvasError ApplyFails(string stateJson, string ops, CanvasActor actor = CanvasActor.Agent)
    {
        var applied = CanvasOps.Apply(CanvasKinds.Diagram, actor, stateJson, ParseOps(ops, actor));
        applied.IsSuccess.ShouldBeFalse();
        return applied.Error;
    }

    private static CanvasError ParseFails(string ops, CanvasActor actor = CanvasActor.Agent, string kind = CanvasKinds.Diagram)
    {
        var parsed = CanvasOps.Parse(kind, actor, JsonNode.Parse(ops));
        parsed.IsSuccess.ShouldBeFalse();
        return parsed.Error;
    }

    [Fact]
    public void Open_Diagram_BuildsStateWithoutPositions_AndRecordsHowItWasBuilt()
    {
        var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonNode.Parse(TwoBoxes));

        opened.IsSuccess.ShouldBeTrue();
        opened.Value.StateJson.ShouldBe(
            """{"direction":"TB","nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions"},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes","style":"solid"}]}""");
        opened.Value.Ops.Select(op => op.Name).ShouldBe(["addNode", "addNode", "addEdge"]);
        opened.Value.Summary.ShouldBe("+2 boxes, +1 edge");
    }

    [Fact]
    public void Open_Diagram_WithDirection_RecordsItAsTheFirstOp()
    {
        var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonNode.Parse("""{"direction":"lr","nodes":[{"id":"a","label":"A"}]}"""));

        opened.IsSuccess.ShouldBeTrue();
        DiagramState.Parse(opened.Value.StateJson).Direction.ShouldBe("LR");
        opened.Value.Ops[0].ShouldBe(new SetDirectionOp("LR"));
        opened.Value.Summary.ShouldBe("+1 box, direction LR");
    }

    [Fact]
    public void Open_AcceptsStateSentAsAJsonString()
    {
        var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonValue.Create(TwoBoxes));

        opened.IsSuccess.ShouldBeTrue();
        DiagramState.Parse(opened.Value.StateJson).Nodes.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"n1","label":"A","x":10,"y":20}]}""", "state.nodes[0]: boxes can't carry positions. The user places them.")]
    [InlineData("""{"nodes":[{"id":"n1"}]}""", "state.nodes[0]: \"label\" is required.")]
    [InlineData("""{"nodes":[{"id":"n 1","label":"A"}]}""", "state.nodes[0]: \"id\" must be 1-64 letters, digits, or _ . : - characters.")]
    [InlineData("""{"nodes":[{"id":"n1","label":"A","shape":"round"}]}""", "state.nodes[0]: unknown field \"shape\". Allowed: id, label, detail.")]
    [InlineData("""{"nodes":[{"id":"n1","label":"A"}],"edges":[{"id":"e1","from":"n1","to":"n1","style":"bold"}]}""", "state.edges[0]: \"style\" must be one of: solid, dashed, planned.")]
    [InlineData("""{"nodes":[]}""", "\"state\" has no boxes. Give at least one in \"nodes\".")]
    [InlineData("""["n1"]""", "\"state\" must be an object with \"nodes\" and \"edges\".")]
    public void Open_Diagram_RejectsBadStateWithItsPath(string state, string message)
    {
        var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonNode.Parse(state));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldBe(message);
    }

    [Fact]
    public void Open_Diagram_EdgeToAMissingBox_NamesTheEdgeInTheState()
    {
        var opened = CanvasOps.Open(
            CanvasKinds.Diagram,
            JsonNode.Parse("""{"nodes":[{"id":"n1","label":"A"}],"edges":[{"id":"e1","from":"n1","to":"n9"}]}"""));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        opened.Error.Message.ShouldBe("state.edges[0]: no box with id \"n9\". " + CanvasText.ReadHint);
    }

    [Fact]
    public void Open_Diagram_DuplicateIds_AreRejected()
    {
        var opened = CanvasOps.Open(
            CanvasKinds.Diagram,
            JsonNode.Parse("""{"nodes":[{"id":"n1","label":"A"},{"id":"n1","label":"B"}]}"""));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Message.ShouldBe("state.nodes[1]: id \"n1\" is already used.");
    }

    [Theory]
    [InlineData("""{"source":"sequenceDiagram\n  Agent->>Fleet: open"}""")]
    [InlineData("\"sequenceDiagram\\n  Agent->>Fleet: open\"")]
    public void Open_Sequence_AcceptsAnObjectOrPlainSource(string state)
    {
        var opened = CanvasOps.Open(CanvasKinds.Sequence, JsonNode.Parse(state));

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.Value.StateJson.ShouldBe("""{"source":"sequenceDiagram\n  Agent->>Fleet: open"}""");
        opened.Value.Summary.ShouldBe("2 lines");
    }

    [Fact]
    public void Open_UnknownKind_IsRejected()
    {
        var opened = CanvasOps.Open("markdown", JsonNode.Parse("{}"));

        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Message.ShouldBe("Unknown canvas kind \"markdown\". Use diagram or sequence.");
    }

    [Fact]
    public void Parse_AcceptsASingleOpAndOpsSentAsAString()
    {
        ParseOps("""{"op":"removeEdge","id":"e1"}""").ShouldBe([new RemoveEdgeOp("e1")]);
        CanvasOps.Parse(CanvasKinds.Diagram, CanvasActor.Agent, JsonValue.Create("""[{"op":"removeEdge","id":"e1"}]"""))
            .Value.ShouldBe([new RemoveEdgeOp("e1")]);
    }

    [Theory]
    [InlineData("""[{"op":"addNode","id":"n3","label":"New","x":1,"y":2}]""", "Op 1 (addNode): boxes can't carry positions. The user places them.")]
    [InlineData("""[{"op":"moveNode","id":"n1","x":1,"y":2}]""", "Op 1 (moveNode): not allowed for the agent on a diagram canvas. Use one of: addNode, updateNode, removeNode, addEdge, updateEdge, removeEdge, setDirection.")]
    [InlineData("""[{"op":"setSource","source":"x"}]""", "Op 1 (setSource): not allowed for the agent on a diagram canvas. Use one of: addNode, updateNode, removeNode, addEdge, updateEdge, removeEdge, setDirection.")]
    [InlineData("""[{"op":"renameNode","id":"n1"}]""", "Op 1: unknown op \"renameNode\". Use one of: addNode, updateNode, removeNode, addEdge, updateEdge, removeEdge, setDirection.")]
    [InlineData("""[{"id":"n1"}]""", "Op 1: \"op\" is required. Use one of: addNode, updateNode, removeNode, addEdge, updateEdge, removeEdge, setDirection.")]
    [InlineData("""[{"op":"updateNode","id":"n1"}]""", "Op 1 (updateNode): give \"label\" or \"detail\" to change.")]
    [InlineData("""[{"op":"updateNode","id":"n1","label":"  "}]""", "Op 1 (updateNode): \"label\" can't be empty.")]
    [InlineData("""[{"op":"addNode","id":"n3","label":7}]""", "Op 1 (addNode): \"label\" must be a string.")]
    [InlineData("""[{"op":"setDirection","direction":"UP"}]""", "Op 1 (setDirection): \"direction\" must be one of: TB, LR, BT, RL.")]
    [InlineData("""[]""", "\"ops\" must be a non-empty array of changes.")]
    public void Parse_AgentOps_RejectsBadInput(string ops, string message)
    {
        var error = ParseFails(ops);

        error.Kind.ShouldBe(CanvasErrorKind.Invalid);
        error.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData("""[{"op":"addNode","id":"n3","label":"New"}]""", "Op 1 (addNode): not allowed for the user on a diagram canvas. Use one of: moveNode, removeNode.")]
    [InlineData("""[{"op":"moveNode","id":"n1","x":"10","y":2}]""", "Op 1 (moveNode): \"x\" must be a number.")]
    public void Parse_UserOps_OnlyMoveAndRemove(string ops, string message)
    {
        var error = ParseFails(ops, CanvasActor.User);

        error.Kind.ShouldBe(CanvasErrorKind.Invalid);
        error.Message.ShouldBe(message);
    }

    [Fact]
    public void Parse_UserOpsOnASequence_AreRejected()
    {
        ParseFails("""[{"op":"setSource","source":"x"}]""", CanvasActor.User, CanvasKinds.Sequence)
            .Message.ShouldBe("The user can't change a sequence canvas.");
    }

    [Fact]
    public void Apply_AgentOps_ChangeBoxesAndEdges()
    {
        var change = ApplyOk(OpenDiagram(), """
            [
              { "op": "addNode", "id": "n3", "label": "Client store", "detail": "stores/sessions.ts" },
              { "op": "addEdge", "id": "e2", "from": "n2", "to": "n3", "label": "pushes", "style": "planned" },
              { "op": "updateNode", "id": "n1", "label": "NuCode", "detail": "" },
              { "op": "updateEdge", "id": "e1", "style": "dashed" },
              { "op": "setDirection", "direction": "LR" }
            ]
            """);

        var state = DiagramState.Parse(change.StateJson);
        state.Direction.ShouldBe("LR");
        state.Nodes.Select(n => (n.Id, n.Label, n.Detail)).ShouldBe([
            ("n1", "NuCode", null),
            ("n2", "SessionEventsHub", null),
            ("n3", "Client store", "stores/sessions.ts"),
        ]);
        state.Edges.Select(e => (e.Id, e.From, e.To, e.Label, e.Style)).ShouldBe([
            ("e1", "n1", "n2", "publishes", "dashed"),
            ("e2", "n2", "n3", "pushes", "planned"),
        ]);
        change.Summary.ShouldBe("+1 box, 1 box edited, +1 edge, 1 edge edited, direction LR");
    }

    [Fact]
    public void Apply_RemoveNode_RemovesItsEdges_AndRecordsWhatWent()
    {
        var change = ApplyOk(OpenDiagram(), """[{"op":"removeNode","id":"n2"}]""", CanvasActor.User);

        var state = DiagramState.Parse(change.StateJson);
        state.Nodes.Select(n => n.Id).ShouldBe(["n1"]);
        state.Edges.ShouldBeEmpty();
        var removed = change.Ops.ShouldHaveSingleItem().ShouldBeOfType<RemoveNodeOp>();
        removed.RemovedLabel.ShouldBe("SessionEventsHub");
        removed.RemovedEdgeIds.ShouldBe(["e1"]);
        change.Summary.ShouldBe("−1 box, −1 edge");
    }

    [Fact]
    public void Apply_UserMove_SetsPositionAndMarksTheBoxPlaced()
    {
        var change = ApplyOk(OpenDiagram(), """[{"op":"moveNode","id":"n1","x":20,"y":44.5},{"op":"moveNode","id":"n1","x":24,"y":48}]""", CanvasActor.User);

        var node = DiagramState.Parse(change.StateJson).FindNode("n1")!;
        node.X.ShouldBe(24);
        node.Y.ShouldBe(48);
        node.PlacedByUser.ShouldBeTrue();
        DiagramState.Parse(change.StateJson).FindNode("n2")!.PlacedByUser.ShouldBeFalse();
        change.Summary.ShouldBe("1 box moved");
    }

    [Fact]
    public void Apply_AgentUpdate_KeepsTheUsersPosition()
    {
        var moved = ApplyOk(OpenDiagram(), """[{"op":"moveNode","id":"n1","x":20,"y":44}]""", CanvasActor.User);

        var change = ApplyOk(moved.StateJson, """[{"op":"updateNode","id":"n1","label":"Renamed"}]""");

        var node = DiagramState.Parse(change.StateJson).FindNode("n1")!;
        (node.Label, node.X, node.Y, node.PlacedByUser).ShouldBe(("Renamed", 20d, 44d, true));
    }

    [Fact]
    public void Apply_IsAllOrNothing()
    {
        var before = OpenDiagram();

        var error = ApplyFails(before, """
            [
              { "op": "addNode", "id": "n3", "label": "Fine" },
              { "op": "updateNode", "id": "n9", "label": "Missing" }
            ]
            """);

        error.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        error.Message.ShouldBe("Op 2 (updateNode): no box with id \"n9\". " + CanvasText.ReadHint);
    }

    [Fact]
    public void Apply_UnknownIdForTheUser_HasNoAgentHint()
    {
        var error = ApplyFails(OpenDiagram(), """[{"op":"moveNode","id":"n9","x":0,"y":0}]""", CanvasActor.User);

        error.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        error.Message.ShouldBe("Op 1 (moveNode): no box with id \"n9\".");
    }

    [Theory]
    [InlineData("""[{"op":"addNode","id":"e1","label":"Clash"}]""", "Op 1 (addNode): id \"e1\" is already used.")]
    [InlineData("""[{"op":"addEdge","id":"n1","from":"n1","to":"n2"}]""", "Op 1 (addEdge): id \"n1\" is already used.")]
    public void Apply_IdsAreUniqueAcrossBoxesAndEdges(string ops, string message)
    {
        var error = ApplyFails(OpenDiagram(), ops);

        error.Kind.ShouldBe(CanvasErrorKind.Invalid);
        error.Message.ShouldBe(message);
    }

    [Fact]
    public void Apply_OpsFromAnotherActor_AreRejectedEvenIfBuiltInCode()
    {
        var applied = CanvasOps.Apply(CanvasKinds.Diagram, CanvasActor.Agent, OpenDiagram(), [new MoveNodeOp("n1", 1, 2)]);

        applied.IsSuccess.ShouldBeFalse();
        applied.Error.Message.ShouldBe("Op 1 (moveNode): the agent can't use moveNode on a diagram canvas.");
    }

    [Fact]
    public void Apply_OverTheBoxLimit_IsTooLarge()
    {
        var ops = Enumerable.Range(3, CanvasLimits.MaxDiagramNodes - 1)
            .Select(i => (CanvasOp)new AddNodeOp($"n{i}", $"Box {i}", null))
            .ToList();

        var applied = CanvasOps.Apply(CanvasKinds.Diagram, CanvasActor.Agent, OpenDiagram(), ops);

        applied.IsSuccess.ShouldBeFalse();
        applied.Error.Kind.ShouldBe(CanvasErrorKind.TooLarge);
        applied.Error.Message.ShouldBe("The diagram would have 201 boxes; the limit is 200.");
    }

    [Fact]
    public void Apply_OverTheSizeLimit_IsTooLarge()
    {
        var applied = CanvasOps.Apply(
            CanvasKinds.Diagram,
            CanvasActor.Agent,
            OpenDiagram(),
            [new UpdateNodeOp("n1", null, new string('x', CanvasLimits.MaxStateBytes))]);

        applied.IsSuccess.ShouldBeFalse();
        applied.Error.Kind.ShouldBe(CanvasErrorKind.TooLarge);
        applied.Error.Message.ShouldStartWith("The canvas would be 257 KB; the limit is 256 KB.");
    }

    [Fact]
    public void Apply_Sequence_ReplacesTheSource()
    {
        var opened = CanvasOps.Open(CanvasKinds.Sequence, JsonNode.Parse("""{"source":"sequenceDiagram"}"""));
        var ops = ParseOps("""[{"op":"setSource","source":"sequenceDiagram\n  A->>B: hi\n  B-->>A: ok"}]""", kind: CanvasKinds.Sequence);

        var applied = CanvasOps.Apply(CanvasKinds.Sequence, CanvasActor.Agent, opened.Value!.StateJson, ops);

        applied.IsSuccess.ShouldBeTrue();
        SequenceState.Parse(applied.Value.StateJson).Source.ShouldBe("sequenceDiagram\n  A->>B: hi\n  B-->>A: ok");
        applied.Value.Summary.ShouldBe("3 lines");
    }

    [Fact]
    public void RecordedOps_RoundTrip_IncludingWhatARemovalTookWithIt()
    {
        IReadOnlyList<CanvasOp> ops =
        [
            new AddNodeOp("n1", "A", "detail"),
            new UpdateNodeOp("n1", null, ""),
            new RemoveNodeOp("n2") { RemovedLabel = "B", RemovedEdgeIds = ["e1", "e2"] },
            new AddEdgeOp("e3", "n1", "n4", "label", "planned"),
            new UpdateEdgeOp("e3", "", null),
            new RemoveEdgeOp("e3"),
            new SetDirectionOp("LR"),
            new MoveNodeOp("n1", 12.5, -4),
            new SetSourceOp("sequenceDiagram"),
        ];

        var read = CanvasOps.ReadRecorded(CanvasOps.ToJson(ops));

        read.Count.ShouldBe(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is RemoveNodeOp expected)
            {
                var actual = read[i].ShouldBeOfType<RemoveNodeOp>();
                (actual.Id, actual.RemovedLabel).ShouldBe((expected.Id, expected.RemovedLabel));
                actual.RemovedEdgeIds.ShouldBe(expected.RemovedEdgeIds);
            }
            else
            {
                read[i].ShouldBe(ops[i]);
            }
        }
    }
}
