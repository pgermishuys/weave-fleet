using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Canvases;

public sealed class CanvasServiceTests
{
    private const string SessionId = "ses-1";
    private const string Title = "Session event flow";

    private const string Flow = """
        {
          "nodes": [
            { "id": "n1", "label": "NuCode session", "detail": "NuCode/Sessions" },
            { "id": "n2", "label": "SessionEventsHub" },
            { "id": "n7", "label": "use-sessions.ts" }
          ],
          "edges": [
            { "id": "e1", "from": "n1", "to": "n2", "label": "publishes" },
            { "id": "e3", "from": "n2", "to": "n7", "label": "pushes" }
          ]
        }
        """;

    private readonly InMemoryCanvasRepository _repository = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly CanvasService _service;

    public CanvasServiceTests()
    {
        _repository.AddSession(SessionId);
        _service = new CanvasService(_repository, _broadcaster, new TestUserContext());
    }

    private async Task<Canvas> OpenFlowAsync()
    {
        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, Title, JsonNode.Parse(Flow));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.Value.Canvas;
    }

    private Task<CanvasResult<CanvasOutcome>> UserAsync(string canvasId, string ops)
        => _service.ApplyAsync(SessionId, canvasId, CanvasActor.User, JsonNode.Parse(ops));

    private Task<CanvasResult<CanvasOutcome>> AgentAsync(string canvasId, string ops)
        => _service.ApplyAsync(SessionId, canvasId, CanvasActor.Agent, JsonNode.Parse(ops));

    private async Task<DiagramState> StateAsync(string canvasId)
        => DiagramState.Parse((await _service.GetAsync(SessionId, canvasId))!.StateJson);

    private IReadOnlyList<string> BroadcastTypes => _broadcaster.Broadcasts.Select(b => b.Type).ToList();

    [Fact]
    public async Task UserEditsSurviveAnAgentThatWasRefusedThenReadAndCarriedOn()
    {
        var canvas = await OpenFlowAsync();
        (await UserAsync(canvas.Id, """[{"op":"moveNode","id":"n1","x":20,"y":44}]""")).IsSuccess.ShouldBeTrue();
        (await UserAsync(canvas.Id, """[{"op":"removeNode","id":"n7"}]""")).IsSuccess.ShouldBeTrue();

        var refused = await AgentAsync(canvas.Id, """[{"op":"updateNode","id":"n7","label":"useSessions"}]""");
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Kind.ShouldBe(CanvasErrorKind.Refused);
        refused.Error.Message.ShouldBe("Refused: the user removed box \"use-sessions.ts\" (n7) in v3. Call fleet_canvas_read to see the current diagram.");

        var read = await _service.ReadAsync(SessionId, canvas.Id, full: false);
        read.Value.ShouldBe("Since v1 the user removed:\nbox \"use-sessions.ts\" (n7), with edge e3");

        var carriedOn = await AgentAsync(canvas.Id, """
            [
              { "op": "addNode", "id": "n7", "label": "useSessions" },
              { "op": "addEdge", "id": "e4", "from": "n2", "to": "n7", "style": "planned" }
            ]
            """);
        carriedOn.IsSuccess.ShouldBeTrue(carriedOn.Error?.Message);
        carriedOn.Value.Canvas.Version.ShouldBe(4);
        carriedOn.Value.Summary.ShouldBe("+1 box, +1 edge");

        var state = await StateAsync(canvas.Id);
        var moved = state.FindNode("n1")!;
        (moved.X, moved.Y, moved.PlacedByUser).ShouldBe((20d, 44d, true));
        state.FindNode("n7")!.Label.ShouldBe("useSessions");
        state.FindEdge("e3").ShouldBeNull();

        BroadcastTypes.ShouldBe(["canvas.updated", "canvas.focused", "canvas.updated", "canvas.updated", "canvas.updated"]);
        var last = _broadcaster.Broadcasts[^1].DomainEvent.ShouldBeOfType<CanvasUpdated>().Payload;
        (last.Version, last.Actor, last.Summary).ShouldBe((4, "agent", "+1 box, +1 edge"));
        _repository.Revisions.Select(r => (r.Version, r.Actor)).ShouldBe([(1, "agent"), (2, "user"), (3, "user"), (4, "agent")]);
    }

    [Fact]
    public async Task OpenAsync_CreatesTheCanvasAtV1_AndBroadcastsUpdatedThenFocused()
    {
        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, $"  {Title} ", JsonNode.Parse(Flow));

        opened.IsSuccess.ShouldBeTrue();
        var canvas = opened.Value.Canvas;
        opened.Value.Created.ShouldBeTrue();
        canvas.Id.ShouldStartWith("cv_");
        (canvas.Title, canvas.Kind, canvas.Version, canvas.AgentSeenVersion, canvas.UserId).ShouldBe((Title, "diagram", 1, 1, TestUserContext.DefaultUserId));
        opened.Value.Summary.ShouldBe("+3 boxes, +2 edges");

        _broadcaster.Broadcasts.Count.ShouldBe(2);
        var updated = _broadcaster.Broadcasts[0];
        (updated.Topic, updated.Type, updated.UserId).ShouldBe(($"session:{SessionId}", "canvas.updated", TestUserContext.DefaultUserId));
        updated.DomainEvent.ShouldBeOfType<CanvasUpdated>();
        updated.Payload.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        updated.Payload.GetProperty("canvasId").GetString().ShouldBe(canvas.Id);
        updated.Payload.GetProperty("kind").GetString().ShouldBe("diagram");
        updated.Payload.GetProperty("title").GetString().ShouldBe(Title);
        updated.Payload.GetProperty("version").GetInt32().ShouldBe(1);
        updated.Payload.GetProperty("actor").GetString().ShouldBe("agent");
        updated.Payload.GetProperty("summary").GetString().ShouldBe("+3 boxes, +2 edges");
        updated.Payload.GetProperty("state").GetProperty("nodes").GetArrayLength().ShouldBe(3);

        var focused = _broadcaster.Broadcasts[1];
        focused.Type.ShouldBe("canvas.focused");
        focused.DomainEvent.ShouldBeOfType<CanvasFocused>().Payload.CanvasId.ShouldBe(canvas.Id);
        focused.Payload.GetRawText().ShouldBe($$"""{"sessionId":"{{SessionId}}","canvasId":"{{canvas.Id}}"}""");
    }

    [Fact]
    public async Task OpenAsync_InAnUnknownSession_IsNotFound()
    {
        var opened = await _service.OpenAsync("ses-missing", CanvasKinds.Diagram, Title, JsonNode.Parse(Flow));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        _broadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_NeedsATitleOfSensibleLength()
    {
        foreach (var title in new[] { "   ", new string('x', CanvasLimits.MaxTitleLength + 1) })
        {
            var opened = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, title, JsonNode.Parse(Flow));

            opened.Error!.Message.ShouldBe("\"title\" must be 1-120 characters.");
        }

        (await _service.OpenAsync(SessionId, CanvasKinds.Diagram, new string('x', CanvasLimits.MaxTitleLength), JsonNode.Parse(Flow)))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitle_ReopensAndReplacesTheCanvas_KeepingTheUsersPositions()
    {
        var canvas = await OpenFlowAsync();
        await UserAsync(canvas.Id, """[{"op":"moveNode","id":"n1","x":20,"y":44}]""");
        await _service.CloseAsync(SessionId, canvas.Id);

        var reopened = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, Title, JsonNode.Parse("""
            {
              "nodes": [
                { "id": "n1", "label": "NuCode session" },
                { "id": "n2", "label": "SessionEventsHub" },
                { "id": "n9", "label": "Toast" }
              ],
              "edges": [{ "id": "e1", "from": "n1", "to": "n2", "label": "publishes", "style": "dashed" }]
            }
            """));

        reopened.IsSuccess.ShouldBeTrue(reopened.Error?.Message);
        reopened.Value.Created.ShouldBeFalse();
        reopened.Value.Canvas.Id.ShouldBe(canvas.Id);
        reopened.Value.Canvas.Version.ShouldBe(3);
        reopened.Value.Canvas.ClosedAt.ShouldBeNull();
        reopened.Value.Summary.ShouldBe("+1 box, −1 box, 1 box edited, −1 edge, 1 edge edited");

        var state = await StateAsync(canvas.Id);
        state.Nodes.Select(n => n.Id).ShouldBe(["n1", "n2", "n9"]);
        var kept = state.FindNode("n1")!;
        (kept.Detail, kept.X, kept.Y, kept.PlacedByUser).ShouldBe((null, 20d, 44d, true));
        state.Edges.ShouldHaveSingleItem().Style.ShouldBe("dashed");

        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem().Canvas.Id.ShouldBe(canvas.Id);
        BroadcastTypes.TakeLast(2).ShouldBe(["canvas.updated", "canvas.focused"]);
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitleAndTheSameState_OnlyFocuses()
    {
        var canvas = await OpenFlowAsync();

        var again = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, Title, JsonNode.Parse(Flow));

        again.IsSuccess.ShouldBeTrue();
        again.Value.Canvas.Version.ShouldBe(canvas.Version);
        again.Value.Summary.ShouldBe("no changes");
        BroadcastTypes.ShouldBe(["canvas.updated", "canvas.focused", "canvas.focused"]);
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitleOfAnotherKind_IsRejected()
    {
        var canvas = await OpenFlowAsync();

        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Sequence, Title, JsonNode.Parse("""{"source":"sequenceDiagram"}"""));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
        opened.Error.Message.ShouldBe($"\"{Title}\" ({canvas.Id}) is already a diagram canvas. Use another title.");
    }

    [Fact]
    public async Task OpenAsync_WithAnExistingTitle_ThatBringsBackABoxTheUserRemoved_IsRefused()
    {
        var canvas = await OpenFlowAsync();
        await UserAsync(canvas.Id, """[{"op":"removeNode","id":"n7"}]""");

        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Diagram, Title, JsonNode.Parse(Flow));

        opened.Error!.Kind.ShouldBe(CanvasErrorKind.Refused);
        opened.Error.Message.ShouldStartWith("Refused: the user removed box \"use-sessions.ts\" (n7) in v2.");
        (await StateAsync(canvas.Id)).FindNode("n7").ShouldBeNull();
    }

    [Fact]
    public async Task AnAgentPatchThatMissesARemovedBox_DoesNotUseUpTheRefusal()
    {
        var canvas = await OpenFlowAsync();
        await UserAsync(canvas.Id, """[{"op":"removeNode","id":"n7"}]""");

        (await AgentAsync(canvas.Id, """[{"op":"updateNode","id":"n1","label":"NuCode"}]""")).IsSuccess.ShouldBeTrue();

        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem().ChangedByUser.ShouldBeTrue();
        (await AgentAsync(canvas.Id, """[{"op":"addNode","id":"n7","label":"Back"}]""")).Error!.Kind.ShouldBe(CanvasErrorKind.Refused);

        await _service.ReadAsync(SessionId, canvas.Id, full: false);
        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem().ChangedByUser.ShouldBeFalse();
        (await AgentAsync(canvas.Id, """[{"op":"addNode","id":"n7","label":"Back"}]""")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAgentPatch_WithOnlyUserMovesSinceItLooked_CountsAsSeeingTheCanvas()
    {
        var canvas = await OpenFlowAsync();
        await UserAsync(canvas.Id, """[{"op":"moveNode","id":"n2","x":1,"y":2}]""");

        var patched = await AgentAsync(canvas.Id, """[{"op":"updateNode","id":"n1","label":"NuCode"}]""");

        patched.Value!.Canvas.AgentSeenVersion.ShouldBe(3);
        (await _service.GetAsync(SessionId, canvas.Id))!.AgentSeenVersion.ShouldBe(3);
    }

    [Fact]
    public async Task AnAgentPatch_OnAClosedCanvas_IsRefused()
    {
        var canvas = await OpenFlowAsync();
        await _service.CloseAsync(SessionId, canvas.Id);

        var patched = await AgentAsync(canvas.Id, """[{"op":"updateNode","id":"n1","label":"NuCode"}]""");

        patched.Error!.Kind.ShouldBe(CanvasErrorKind.Refused);
        patched.Error.Message.ShouldBe($"Refused: the user closed \"{Title}\" ({canvas.Id}). Call fleet_canvas_open with its title to reopen it.");
    }

    [Fact]
    public async Task CloseThenFocus_ClosesAndReopensTheCanvas()
    {
        var canvas = await OpenFlowAsync();

        (await _service.CloseAsync(SessionId, canvas.Id)).Value!.ClosedAt.ShouldNotBeNull();
        (await _service.CloseAsync(SessionId, canvas.Id)).IsSuccess.ShouldBeTrue();
        (await _service.ListAsync(SessionId)).ShouldBeEmpty();

        (await _service.FocusAsync(SessionId, canvas.Id)).Value!.ClosedAt.ShouldBeNull();
        (await _service.ListAsync(SessionId)).ShouldHaveSingleItem();

        BroadcastTypes.Skip(2).ShouldBe(["canvas.closed", "canvas.updated", "canvas.focused"]);
        _broadcaster.Broadcasts[3].DomainEvent.ShouldBeOfType<CanvasUpdated>().Payload.Summary.ShouldBe("reopened");
    }

    [Fact]
    public async Task AUserOp_OnABoxTheAgentRemoved_IsAnUnknownId()
    {
        var canvas = await OpenFlowAsync();
        await AgentAsync(canvas.Id, """[{"op":"removeNode","id":"n7"}]""");

        var moved = await UserAsync(canvas.Id, """[{"op":"moveNode","id":"n7","x":1,"y":2}]""");

        moved.Error!.Kind.ShouldBe(CanvasErrorKind.UnknownId);
        moved.Error.Message.ShouldBe("Op 1 (moveNode): no box with id \"n7\".");
    }

    [Fact]
    public async Task TheUser_CanOnlyMoveAndRemove()
    {
        var canvas = await OpenFlowAsync();

        var added = await UserAsync(canvas.Id, """[{"op":"addNode","id":"n8","label":"Mine"}]""");

        added.Error!.Kind.ShouldBe(CanvasErrorKind.Invalid);
    }

    [Fact]
    public async Task ApplyAsync_RetriesAgainstTheNewState_WhenAnotherWriterGetsThereFirst()
    {
        var canvas = await OpenFlowAsync();
        _repository.BeforeNextUpdate = async () =>
            (await UserAsync(canvas.Id, """[{"op":"moveNode","id":"n2","x":5,"y":6}]""")).IsSuccess.ShouldBeTrue();

        var patched = await AgentAsync(canvas.Id, """[{"op":"updateNode","id":"n1","label":"NuCode"}]""");

        patched.IsSuccess.ShouldBeTrue(patched.Error?.Message);
        patched.Value.Canvas.Version.ShouldBe(3);
        var state = await StateAsync(canvas.Id);
        state.FindNode("n1")!.Label.ShouldBe("NuCode");
        (state.FindNode("n2")!.X, state.FindNode("n2")!.Y).ShouldBe((5d, 6d));
        _repository.Revisions.Select(r => (r.Version, r.Actor)).ShouldBe([(1, "agent"), (2, "user"), (3, "agent")]);
    }

    [Fact]
    public async Task ReadAsync_Full_RendersTheCanvasAndMarksItSeen()
    {
        var canvas = await OpenFlowAsync();
        await UserAsync(canvas.Id, """[{"op":"removeNode","id":"n7"}]""");

        var read = await _service.ReadAsync(SessionId, canvas.Id, full: true);

        read.Value.ShouldBe($"""
            diagram {canvas.Id} "{Title}" v2 TB
            n1 NuCode session · NuCode/Sessions
            n2 SessionEventsHub
            e1 n1 -> n2 publishes
            """.ReplaceLineEndings("\n"));
        (await _service.GetAsync(SessionId, canvas.Id))!.AgentSeenVersion.ShouldBe(2);
        (await _service.ReadAsync(SessionId, canvas.Id, full: false)).Value.ShouldBe("No changes since v2.");
    }

    [Fact]
    public async Task UnknownCanvases_AreNotFound()
    {
        (await _service.ReadAsync(SessionId, "cv_nope", full: true)).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        (await AgentAsync("cv_nope", """[{"op":"removeEdge","id":"e1"}]""")).Error!.Message
            .ShouldBe("No canvas cv_nope in this session. Call fleet_canvas_list to see the open canvases.");
        (await _service.FocusAsync(SessionId, "cv_nope")).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        (await _service.CloseAsync(SessionId, "cv_nope")).Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
    }

    [Fact]
    public async Task Sequence_OpenAndPatch()
    {
        var opened = await _service.OpenAsync(SessionId, CanvasKinds.Sequence, "Open flow", JsonNode.Parse("""{"source":"sequenceDiagram\n  Agent->>Fleet: open"}"""));
        var patched = await AgentAsync(opened.Value!.Canvas.Id, """[{"op":"setSource","source":"sequenceDiagram\n  Agent->>Fleet: open\n  Fleet-->>Agent: v1"}]""");

        patched.Value!.Canvas.Version.ShouldBe(2);
        patched.Value.Summary.ShouldBe("3 lines");
        var state = _broadcaster.Broadcasts[^1].Payload.GetProperty("state");
        state.GetProperty("source").GetString().ShouldBe("sequenceDiagram\n  Agent->>Fleet: open\n  Fleet-->>Agent: v1");
        state.ValueKind.ShouldBe(JsonValueKind.Object);
    }
}
