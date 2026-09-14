import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it } from "vitest";
import { useFileBuffersStore } from "../file-buffers";

describe("file buffers store", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("creates a buffer once, loading", () => {
    const store = useFileBuffersStore();
    const first = store.ensure("s1", "src/app.ts");
    expect(store.ensure("s1", "src/app.ts")).toBe(first);
    expect(store.info("s1", "src/app.ts")?.status).toBe("loading");
  });

  it("keeps sessions and paths apart", () => {
    const store = useFileBuffersStore();
    store.ensure("s1", "a.ts");
    store.ensure("s2", "a.ts");
    store.patch("s1", "a.ts", { dirty: true });
    expect(store.isDirty("s1", "a.ts")).toBe(true);
    expect(store.isDirty("s2", "a.ts")).toBe(false);
    expect(store.openPaths("s1")).toEqual(["a.ts"]);
  });

  it("lists unsaved files across sessions, and forgets closed ones", () => {
    const store = useFileBuffersStore();
    store.ensure("s1", "a.ts");
    store.ensure("s2", "src/b.ts");
    store.patch("s1", "a.ts", { dirty: true });
    store.patch("s2", "src/b.ts", { dirty: true });
    expect(store.unsaved).toEqual([{ sessionId: "s1", path: "a.ts" }, { sessionId: "s2", path: "src/b.ts" }]);

    store.remove("s1", "a.ts");
    expect(store.unsaved).toEqual([{ sessionId: "s2", path: "src/b.ts" }]);
    expect(store.record("s1", "a.ts")).toBeUndefined();
  });

  it("doesn't replace the info object when nothing changed", () => {
    const store = useFileBuffersStore();
    store.ensure("s1", "a.ts");
    const before = store.infos;
    store.patch("s1", "a.ts", { dirty: false });
    expect(store.infos).toBe(before);
  });
});
