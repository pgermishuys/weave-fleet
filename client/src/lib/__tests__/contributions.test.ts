import { computed, nextTick, watch } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineContributionPoint } from "@/lib/contributions";

interface Thing {
  id: string;
  label?: string;
  order?: number;
  group?: string;
}

function thingPoint() {
  return defineContributionPoint<Thing>({ name: "things", idOf: (thing) => thing.id });
}

describe("defineContributionPoint", () => {
  let warn: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  });

  afterEach(() => {
    warn.mockRestore();
  });

  describe("reading", () => {
    it("starts empty", () => {
      const point = thingPoint();

      expect(point.items.value).toEqual([]);
      expect(point.get("x")).toBeUndefined();
      expect(point.ownerOf("x")).toBeUndefined();
    });

    it("finds an item and its owner by id", () => {
      const point = thingPoint();
      point.contribute("alpha", [{ id: "a" }, { id: "b" }]);

      expect(point.get("b")).toEqual({ id: "b" });
      expect(point.ownerOf("b")).toBe("alpha");
    });
  });

  describe("ordering", () => {
    it("sorts by order, counting a missing order as 0", () => {
      const point = thingPoint();
      point.contribute("o", [{ id: "late", order: 20 }, { id: "none" }, { id: "early", order: -5 }, { id: "mid", order: 10 }]);

      expect(point.items.value.map((t) => t.id)).toEqual(["early", "none", "mid", "late"]);
    });

    it("breaks an order tie by group, then by label", () => {
      const point = thingPoint();
      point.contribute("o", [
        { id: "1", order: 1, group: "b", label: "Apple" },
        { id: "2", order: 1, group: "a", label: "Zebra" },
        { id: "3", order: 1, group: "a", label: "Mango" },
        { id: "4", order: 0, group: "z", label: "First by order" },
      ]);

      expect(point.items.value.map((t) => t.id)).toEqual(["4", "3", "2", "1"]);
    });

    it("sorts by the label the point names when the item has no label field", () => {
      const point = defineContributionPoint<{ id: string; title: string }>({
        name: "sections",
        idOf: (section) => section.id,
        labelOf: (section) => section.title,
      });
      point.contribute("o", [{ id: "x", title: "Beta" }, { id: "y", title: "Alpha" }]);

      expect(point.items.value.map((s) => s.id)).toEqual(["y", "x"]);
    });

    it("keeps the order contributed in when everything else ties, across owners", () => {
      const point = thingPoint();
      point.contribute("one", [{ id: "a" }, { id: "b" }]);
      point.contribute("two", [{ id: "c" }]);

      expect(point.items.value.map((t) => t.id)).toEqual(["a", "b", "c"]);
    });

    it("does not reorder the caller's array", () => {
      const point = thingPoint();
      const given: Thing[] = [{ id: "b", order: 2 }, { id: "a", order: 1 }];
      point.contribute("o", given);

      expect(given.map((t) => t.id)).toEqual(["b", "a"]);
    });
  });

  describe("ownership", () => {
    it("removes only what the disposer's contribution added", () => {
      const point = thingPoint();
      point.contribute("keeper", [{ id: "k" }]);
      const dispose = point.contribute("leaver", [{ id: "a" }, { id: "b" }]);

      dispose();

      expect(point.items.value.map((t) => t.id)).toEqual(["k"]);
    });

    it("lets a disposer run twice", () => {
      const point = thingPoint();
      const dispose = point.contribute("o", [{ id: "a" }]);

      dispose();
      dispose();

      expect(point.items.value).toEqual([]);
    });

    it("removeByOwner removes everything the owner contributed, across calls", () => {
      const point = thingPoint();
      point.contribute("leaver", [{ id: "a" }]);
      point.contribute("keeper", [{ id: "k" }]);
      point.contribute("leaver", [{ id: "b" }]);

      point.removeByOwner("leaver");

      expect(point.items.value.map((t) => t.id)).toEqual(["k"]);
    });

    it("removeByOwner for an owner with nothing is a no-op", () => {
      const point = thingPoint();
      point.contribute("keeper", [{ id: "k" }]);

      point.removeByOwner("nobody");

      expect(point.items.value.map((t) => t.id)).toEqual(["k"]);
    });

    it("clear removes everything", () => {
      const point = thingPoint();
      point.contribute("a", [{ id: "1" }]);
      point.contribute("b", [{ id: "2" }]);

      point.clear();

      expect(point.items.value).toEqual([]);
    });

    it("contributing nothing changes nothing", () => {
      const point = thingPoint();
      const before = point.items.value;

      point.contribute("o", [])();

      expect(point.items.value).toBe(before);
    });
  });

  describe("duplicate ids", () => {
    it("lets the later contribution replace the earlier one, in the earlier one's place", () => {
      const point = thingPoint();
      point.contribute("first", [{ id: "dup", label: "old" }, { id: "other", label: "z" }]);

      point.contribute("second", [{ id: "dup", label: "new" }]);

      expect(point.items.value.map((t) => `${t.id}:${t.label}`)).toEqual(["dup:new", "other:z"]);
      expect(point.ownerOf("dup")).toBe("second");
    });

    it("warns in development, naming both owners", () => {
      const point = thingPoint();
      point.contribute("first", [{ id: "dup" }]);

      point.contribute("second", [{ id: "dup" }]);

      expect(warn).toHaveBeenCalledTimes(1);
      expect(warn.mock.calls[0][0]).toContain("first");
      expect(warn.mock.calls[0][0]).toContain("second");
    });

    it("does not warn when ids are distinct", () => {
      const point = thingPoint();
      point.contribute("first", [{ id: "a" }]);
      point.contribute("second", [{ id: "b" }]);

      expect(warn).not.toHaveBeenCalled();
    });

    it("keeps the last of two duplicates inside one call", () => {
      const point = thingPoint();

      point.contribute("o", [{ id: "dup", label: "one" }, { id: "dup", label: "two" }]);

      expect(point.items.value.map((t) => t.label)).toEqual(["two"]);
    });

    it("does not let the replaced contribution's disposer remove its replacement", () => {
      const point = thingPoint();
      const disposeFirst = point.contribute("first", [{ id: "dup" }, { id: "own" }]);
      point.contribute("second", [{ id: "dup", label: "kept" }]);

      disposeFirst();

      expect(point.items.value.map((t) => t.label ?? t.id)).toEqual(["kept"]);
    });

    it("does not let the replaced owner's removeByOwner remove its replacement", () => {
      const point = thingPoint();
      point.contribute("first", [{ id: "dup" }]);
      point.contribute("second", [{ id: "dup", label: "kept" }]);

      point.removeByOwner("first");

      expect(point.get("dup")?.label).toBe("kept");
    });
  });

  describe("reactivity", () => {
    it("updates a computed that reads items when something contributes", () => {
      const point = thingPoint();
      const ids = computed(() => point.items.value.map((t) => t.id));
      expect(ids.value).toEqual([]);

      point.contribute("o", [{ id: "a" }]);

      expect(ids.value).toEqual(["a"]);
    });

    it("updates a computed that reads get when its item arrives, is replaced and leaves", () => {
      const point = thingPoint();
      const label = computed(() => point.get("x")?.label ?? "none");
      expect(label.value).toBe("none");

      const dispose = point.contribute("o", [{ id: "x", label: "one" }]);
      expect(label.value).toBe("one");

      point.contribute("p", [{ id: "x", label: "two" }]);
      expect(label.value).toBe("two");

      dispose();
      point.removeByOwner("p");
      expect(label.value).toBe("none");
    });

    it("notifies a watcher on removal, and on clear", async () => {
      const point = thingPoint();
      const seen: number[] = [];
      watch(() => point.items.value.length, (length) => seen.push(length));

      point.contribute("o", [{ id: "a" }, { id: "b" }]);
      await nextTick();
      point.removeByOwner("o");
      await nextTick();
      point.contribute("o", [{ id: "c" }]);
      await nextTick();
      point.clear();
      await nextTick();

      expect(seen).toEqual([2, 0, 1, 0]);
    });

    it("hands out a new array after a change so shallow readers see it", () => {
      const point = thingPoint();
      point.contribute("o", [{ id: "a" }]);
      const before = point.items.value;

      point.contribute("o", [{ id: "b" }]);

      expect(point.items.value).not.toBe(before);
      expect(before.map((t) => t.id)).toEqual(["a"]);
    });
  });
});
