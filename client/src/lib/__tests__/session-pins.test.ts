import { describe, expect, it } from "vitest";
import { pinOrderFor, pinnedNeighbourFor, splitPinned } from "@/lib/session-pins";

function item(id: string, extra: Record<string, unknown> = {}) {
  return { session: { id }, ...extra };
}

const ids = (items: ReadonlyArray<{ session: { id: string } }>) => items.map((entry) => entry.session.id);

describe("splitPinned", () => {
  it("puts pinned sessions in their pinned order and keeps the rest in the list's order", () => {
    const { pinned, rest } = splitPinned([
      item("newest"),
      item("second", { pinOrder: 2 }),
      item("middle"),
      item("first", { pinOrder: 0.5 }),
      item("oldest"),
    ]);

    expect(ids(pinned)).toEqual(["first", "second"]);
    expect(ids(rest)).toEqual(["newest", "middle", "oldest"]);
  });

  it("takes what came from a pinned session with it, however deep", () => {
    const { pinned, rest } = splitPinned([
      item("grandchild", { spawnedBySessionId: "child", spawnKind: "api" }),
      item("child", { forkedFromSessionId: "parent", spawnKind: "fork" }),
      item("parent", { pinOrder: 1 }),
      item("other"),
    ]);

    expect(ids(pinned)).toEqual(["parent", "grandchild", "child"]);
    expect(ids(rest)).toEqual(["other"]);
  });

  it("lets a pinned session stand on its own when the one it came from isn't pinned", () => {
    const { pinned, rest } = splitPinned([
      item("fork", { forkedFromSessionId: "parent", spawnKind: "fork", pinOrder: 1 }),
      item("parent"),
    ]);

    expect(ids(pinned)).toEqual(["fork"]);
    expect(ids(rest)).toEqual(["parent"]);
  });

  it("leaves a session moved out of a pinned parent where it is", () => {
    const { pinned, rest } = splitPinned([
      item("fork", { forkedFromSessionId: "parent", spawnKind: "fork", lineageDetachedAt: "2026-10-07T10:00:00Z" }),
      item("parent", { pinOrder: 1 }),
    ]);

    expect(ids(pinned)).toEqual(["parent"]);
    expect(ids(rest)).toEqual(["fork"]);
  });
});

describe("pinOrderFor", () => {
  const items = [item("a", { pinOrder: 1 }), item("b", { pinOrder: 2 }), item("c", { pinOrder: 3 }), item("d")];

  it("goes at the end when there's nothing to sit before", () => {
    expect(pinOrderFor(items, "d", null)).toBe(4);
    expect(pinOrderFor([item("d")], "d", null)).toBe(1);
    expect(pinOrderFor(items, "d", "not-pinned")).toBe(4);
  });

  it("sits halfway between the pinned session before it and the one it goes before", () => {
    expect(pinOrderFor(items, "d", "b")).toBe(1.5);
    expect(pinOrderFor(items, "d", "a")).toBe(0.5);
  });

  it("leaves itself out when it's already pinned", () => {
    expect(pinOrderFor(items, "c", "b")).toBe(1.5);
    expect(pinOrderFor(items, "a", null)).toBe(4);
  });
});

describe("pinnedNeighbourFor", () => {
  const items = [item("a", { pinOrder: 1 }), item("b", { pinOrder: 2 }), item("c", { pinOrder: 3 }), item("d")];

  it("says which pinned session it goes before when it moves up or down", () => {
    expect(pinnedNeighbourFor(items, "b", -1)).toBe("a");
    expect(pinnedNeighbourFor(items, "a", 1)).toBe("c");
    expect(pinnedNeighbourFor(items, "b", 1)).toBeNull();
  });

  it("can't move past either end, or move a session that isn't pinned", () => {
    expect(pinnedNeighbourFor(items, "a", -1)).toBeUndefined();
    expect(pinnedNeighbourFor(items, "c", 1)).toBeUndefined();
    expect(pinnedNeighbourFor(items, "d", -1)).toBeUndefined();
  });
});
