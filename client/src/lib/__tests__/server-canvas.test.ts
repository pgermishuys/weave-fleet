import { describe, expect, it } from "vitest";
import { diagramFlowContent, serverCanvasPayload } from "@/lib/server-canvas";

// The state from SignalREventContractTests: the agent opened it, the user moved n1.
const diagramState = {
  direction: "TB",
  nodes: [
    { id: "n1", label: "NuCode session", detail: "NuCode/Sessions", x: 20, y: 44.5, placedByUser: true },
    { id: "n2", label: "SessionEventsHub" },
  ],
  edges: [{ id: "e1", from: "n1", to: "n2", label: "publishes", style: "solid" }],
};

describe("diagramFlowContent", () => {
  it("maps boxes and edges, renaming from/to to source/target", () => {
    expect(diagramFlowContent(diagramState)).toEqual({
      direction: "TB",
      nodes: [
        { id: "n1", label: "NuCode session", detail: "NuCode/Sessions", x: 20, y: 44.5 },
        { id: "n2", label: "SessionEventsHub" },
      ],
      edges: [{ id: "e1", source: "n1", target: "n2", label: "publishes" }],
    });
  });

  it("passes a position through only when the box has both x and y", () => {
    const content = diagramFlowContent({
      nodes: [
        { id: "a", label: "A", x: 10 },
        { id: "b", label: "B", x: 10, y: 20 },
      ],
      edges: [],
    });

    expect(content.nodes).toEqual([{ id: "a", label: "A" }, { id: "b", label: "B", x: 10, y: 20 }]);
  });

  it("keeps the dashed and planned styles and the direction", () => {
    const content = diagramFlowContent({
      direction: "lr",
      nodes: [{ id: "a", label: "A" }, { id: "b", label: "B" }],
      edges: [
        { id: "e1", from: "a", to: "b", style: "dashed" },
        { id: "e2", from: "b", to: "a", style: "planned" },
      ],
    });

    expect(content.direction).toBe("LR");
    expect(content.edges.map((edge) => edge.style)).toEqual(["dashed", "planned"]);
  });

  it("drops malformed boxes and edges to boxes that aren't there", () => {
    const content = diagramFlowContent({
      direction: "sideways",
      nodes: [{ id: "a", label: "A" }, { label: "no id" }, "junk"],
      edges: [
        { id: "e1", from: "a", to: "gone" },
        { id: "e2", from: "a", to: "a" },
      ],
    });

    expect(content).toEqual({
      direction: "TB",
      nodes: [{ id: "a", label: "A" }],
      edges: [{ id: "e2", source: "a", target: "a" }],
    });
  });

  it("returns an empty diagram for a state that isn't one", () => {
    expect(diagramFlowContent(null)).toEqual({ direction: "TB", nodes: [], edges: [] });
  });
});

describe("serverCanvasPayload", () => {
  it("renders a diagram as a flow and a sequence as Mermaid", () => {
    expect(serverCanvasPayload({ canvasId: "cv_1", kind: "diagram", title: "Flow", version: 2, state: diagramState })).toEqual({
      $type: "visual/flow",
      title: "Flow",
      content: diagramFlowContent(diagramState),
    });
    expect(serverCanvasPayload({ canvasId: "cv_2", kind: "sequence", title: "Calls", version: 1, state: { source: "sequenceDiagram\n  A->>B: hi" } })).toEqual({
      $type: "visual/sequence",
      title: "Calls",
      content: "sequenceDiagram\n  A->>B: hi",
    });
  });

  it("returns null for a kind this client doesn't know", () => {
    expect(serverCanvasPayload({ canvasId: "cv_3", kind: "terminal", title: "Shell", version: 1, state: {} })).toBeNull();
  });
});
