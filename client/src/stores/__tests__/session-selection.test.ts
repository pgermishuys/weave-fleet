import { describe, expect, it } from "vitest";
import { useSessionSelectionStore } from "@/stores/session-selection";

describe("session selection", () => {
  it("toggles single rows", () => {
    const selection = useSessionSelectionStore();

    selection.toggle("a");
    selection.toggle("b");
    selection.toggle("a");

    expect([...selection.selectedIds]).toEqual(["b"]);
    expect(selection.isSelecting).toBe(true);
  });

  it("takes a Shift range from the last picked row in list order", () => {
    const selection = useSessionSelectionStore();
    selection.setVisibleOrder(["a", "b", "c", "d", "e"]);

    selection.toggle("b");
    selection.extendTo("d", null);

    expect([...selection.selectedIds].sort()).toEqual(["b", "c", "d"]);
  });

  it("starts a Shift range from the open session when nothing is picked yet", () => {
    const selection = useSessionSelectionStore();
    selection.setVisibleOrder(["a", "b", "c", "d"]);

    selection.extendTo("c", "a");

    expect([...selection.selectedIds].sort()).toEqual(["a", "b", "c"]);
  });

  it("drops rows that leave the list", () => {
    const selection = useSessionSelectionStore();
    selection.setVisibleOrder(["a", "b", "c"]);
    selection.toggle("a");
    selection.toggle("c");

    selection.setVisibleOrder(["b", "c"]);

    expect([...selection.selectedIds]).toEqual(["c"]);
  });

  it("clears", () => {
    const selection = useSessionSelectionStore();
    selection.toggle("a");

    selection.clear();

    expect(selection.count).toBe(0);
    expect(selection.isSelecting).toBe(false);
  });
});
