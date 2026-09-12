using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasConflictsTests
{
    /// <summary>
    /// A canvas and its revisions in memory, written the way the canvas service will write them:
    /// every accepted change bumps the version and stores its applied ops, and an agent open, write
    /// or read moves the agent's seen version.
    /// </summary>
    private sealed class Board
    {
        private readonly List<CanvasRevision> _revisions = [];

        public Board(string openState)
        {
            var opened = CanvasOps.Open(CanvasKinds.Diagram, JsonNode.Parse(openState));
            opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
            Record(opened.Value, CanvasActor.Agent);
        }

        public string StateJson { get; private set; } = string.Empty;
        public int Version { get; private set; }
        public int AgentSeenVersion { get; private set; }

        public IReadOnlyList<CanvasUserRemoval> RemovalsSinceSeen
            => CanvasConflicts.UserRemovals(_revisions.Where(r => r.Version > AgentSeenVersion));

        public void User(string ops)
        {
            var parsed = CanvasOps.Parse(CanvasKinds.Diagram, CanvasActor.User, JsonNode.Parse(ops));
            parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
            var applied = CanvasOps.Apply(CanvasKinds.Diagram, CanvasActor.User, StateJson, parsed.Value);
            applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
            Record(applied.Value, CanvasActor.User);
        }

        /// <summary>Returns the error, or <c>null</c> if the change was accepted.</summary>
        public CanvasError? Agent(string ops)
        {
            var parsed = CanvasOps.Parse(CanvasKinds.Diagram, CanvasActor.Agent, JsonNode.Parse(ops));
            parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
            if (CanvasConflicts.FindRefusal(parsed.Value, RemovalsSinceSeen) is { } refusal)
                return refusal;

            var applied = CanvasOps.Apply(CanvasKinds.Diagram, CanvasActor.Agent, StateJson, parsed.Value);
            if (!applied.IsSuccess)
                return applied.Error;

            Record(applied.Value, CanvasActor.Agent);
            return null;
        }

        public string AgentRead()
        {
            var text = CanvasText.RenderChangesSince(AgentSeenVersion, RemovalsSinceSeen);
            AgentSeenVersion = Version;
            return text;
        }

        private void Record(CanvasChange change, CanvasActor actor)
        {
            Version++;
            StateJson = change.StateJson;
            _revisions.Add(new CanvasRevision
            {
                CanvasId = "cv_1",
                Version = Version,
                Actor = actor.ToRevisionActor(),
                OpsJson = CanvasOps.ToJson(change.Ops),
            });
            if (actor == CanvasActor.Agent)
                AgentSeenVersion = Version;
        }
    }

    private const string Flow = """
        {
          "nodes": [
            { "id": "n1", "label": "NuCode session" },
            { "id": "n2", "label": "SessionEventsHub" },
            { "id": "n7", "label": "use-sessions.ts" }
          ],
          "edges": [
            { "id": "e1", "from": "n1", "to": "n2" },
            { "id": "e3", "from": "n2", "to": "n7", "label": "pushes" }
          ]
        }
        """;

    [Fact]
    public void AgentUpdateOnABoxTheUserRemoved_IsRefusedWithOneLine()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"moveNode","id":"n1","x":20,"y":44}]""");
        board.User("""[{"op":"removeNode","id":"n7"}]""");

        var error = board.Agent("""[{"op":"updateNode","id":"n7","label":"useSessions"}]""");

        error.ShouldNotBeNull();
        error.Kind.ShouldBe(CanvasErrorKind.Refused);
        error.Message.ShouldBe("Refused: the user removed box \"use-sessions.ts\" (n7) in v3. Call fleet_canvas_read to see the current diagram.");
        board.Version.ShouldBe(3);
    }

    [Fact]
    public void AfterARead_TheRefusalIsGone_AndReAddingTheIdSucceeds()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"moveNode","id":"n1","x":20,"y":44}]""");
        board.User("""[{"op":"removeNode","id":"n7"}]""");
        board.Agent("""[{"op":"addNode","id":"n7","label":"useSessions"}]""")!.Kind.ShouldBe(CanvasErrorKind.Refused);

        board.AgentRead().ShouldBe("Since v1 the user removed:\nbox \"use-sessions.ts\" (n7), with edge e3");

        // The box is gone, so updating it is now a plain unknown-id error rather than a refusal.
        board.Agent("""[{"op":"updateNode","id":"n7","label":"useSessions"}]""")!.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        board.Agent("""[{"op":"addNode","id":"n7","label":"useSessions"},{"op":"addEdge","id":"e3","from":"n2","to":"n7"}]""").ShouldBeNull();

        var state = DiagramState.Parse(board.StateJson);
        state.FindNode("n7")!.Label.ShouldBe("useSessions");
        var moved = state.FindNode("n1")!;
        (moved.X, moved.Y, moved.PlacedByUser).ShouldBe((20d, 44d, true));
    }

    [Fact]
    public void EveryKindOfReferenceToARemovedBoxOrItsEdges_IsRefused()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"removeNode","id":"n7"}]""");

        var error = board.Agent("""
            [
              { "op": "removeNode", "id": "n7" },
              { "op": "addEdge", "id": "e9", "from": "n1", "to": "n7" },
              { "op": "updateEdge", "id": "e3", "style": "dashed" },
              { "op": "removeEdge", "id": "e3" }
            ]
            """);

        error.ShouldNotBeNull();
        error.Message.ShouldBe(
            "Refused: the user removed box \"use-sessions.ts\" (n7) in v2.\n" +
            "Refused: edge e3 went when the user removed box \"use-sessions.ts\" (n7) in v2. Call fleet_canvas_read to see the current diagram.");
    }

    [Fact]
    public void AgentOpsThatDoNotTouchRemovedItems_AreAccepted()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"removeNode","id":"n7"}]""");

        board.Agent("""[{"op":"updateNode","id":"n2","label":"Hub"},{"op":"addNode","id":"n8","label":"Toast"}]""").ShouldBeNull();

        board.Version.ShouldBe(3);
    }

    [Fact]
    public void AnIdThatNeverExisted_IsAPlainError()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"removeNode","id":"n7"}]""");

        var error = board.Agent("""[{"op":"updateNode","id":"n99","label":"Nope"}]""");

        error!.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        error.Message.ShouldBe("Op 1 (updateNode): no box with id \"n99\". " + CanvasText.ReadHint);
    }

    [Fact]
    public void UserMoves_NeverConflict_AndNeverShowUpInTheAgentsRead()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"moveNode","id":"n7","x":1,"y":2}]""");
        board.User("""[{"op":"moveNode","id":"n2","x":3,"y":4}]""");

        board.RemovalsSinceSeen.ShouldBeEmpty();
        board.Agent("""[{"op":"updateNode","id":"n7","label":"useSessions"}]""").ShouldBeNull();
        board.User("""[{"op":"moveNode","id":"n1","x":5,"y":6}]""");
        board.AgentRead().ShouldBe("No changes since v4.");
    }

    [Fact]
    public void RemovalsTheAgentAlreadySaw_AreNotRefused()
    {
        var board = new Board(Flow);
        board.User("""[{"op":"removeNode","id":"n7"}]""");
        board.AgentRead();

        board.RemovalsSinceSeen.ShouldBeEmpty();
        board.Agent("""[{"op":"addNode","id":"n7","label":"Back"}]""").ShouldBeNull();
    }

    [Fact]
    public void UserRemovals_IgnoreTheAgentsOwnRemovals()
    {
        var revisions = new[]
        {
            new CanvasRevision { Version = 2, Actor = CanvasRevision.AgentActor, OpsJson = CanvasOps.ToJson([new RemoveNodeOp("n1") { RemovedLabel = "A" }]) },
            new CanvasRevision { Version = 3, Actor = CanvasRevision.UserActor, OpsJson = CanvasOps.ToJson([new MoveNodeOp("n2", 0, 0), new RemoveNodeOp("n2") { RemovedLabel = "B", RemovedEdgeIds = ["e1"] }]) },
        };

        CanvasConflicts.UserRemovals(revisions).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Version.ShouldBe(3),
            r => r.NodeId.ShouldBe("n2"),
            r => r.Label.ShouldBe("B"),
            r => r.EdgeIds.ShouldBe(["e1"]));
    }
}
