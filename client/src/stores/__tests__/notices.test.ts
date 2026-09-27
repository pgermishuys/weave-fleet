import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useNoticesStore, type Notice } from "@/stores/notices";

const update = (version: string): Notice => ({ id: `update:app:${version}`, title: `Fleet ${version} is ready`, chip: `Fleet ${version} ready` });

describe("notices", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("waits to open a new notice until the host opens it, then settles it into its chip", () => {
    const store = useNoticesStore();
    store.post(update("0.37.0"));

    expect(store.nextWaiting).toBe("update:app:0.37.0");
    expect(store.open).toBeNull();
    expect(store.chips).toHaveLength(0);

    store.openNext();
    expect(store.open?.id).toBe("update:app:0.37.0");
    expect(store.chips).toHaveLength(0);

    store.settle();
    expect(store.open).toBeNull();
    expect(store.chips.map((notice) => notice.chip)).toEqual(["Fleet 0.37.0 ready"]);
  });

  it("never opens the same notice twice, even after a restart", () => {
    const first = useNoticesStore();
    first.post(update("0.37.0"));
    first.openNext();
    first.settle();

    // A new page load: fresh store, same browser.
    setActivePinia(createPinia());
    const second = useNoticesStore();
    second.post(update("0.37.0"));

    expect(second.nextWaiting).toBeNull();
    expect(second.chips.map((notice) => notice.id)).toEqual(["update:app:0.37.0"]);

    second.post(update("0.37.1"));
    expect(second.nextWaiting).toBe("update:app:0.37.1");
  });

  it("opens one card at a time, the next once the first settles", () => {
    const store = useNoticesStore();
    store.post(update("0.37.0"));
    store.post({ id: "update:server:0.37.0", title: "Fleet 0.37.0 is ready", chip: "Fleet 0.37.0 ready" });

    store.openNext();
    store.openNext();
    expect(store.open?.id).toBe("update:app:0.37.0");

    store.settle();
    store.openNext();
    expect(store.open?.id).toBe("update:server:0.37.0");
  });

  it("reopens a settled card from its chip, pinned open", () => {
    const store = useNoticesStore();
    store.post(update("0.37.0"));
    store.openNext();
    expect(store.pinned).toBe(false);
    store.settle();

    store.reopen("update:app:0.37.0");
    expect(store.open?.id).toBe("update:app:0.37.0");
    expect(store.pinned).toBe(true);
  });

  it("pins a card whose content changes, so a question stays until it's answered", () => {
    const store = useNoticesStore();
    store.post(update("0.37.0"));
    store.openNext();

    store.update("update:app:0.37.0", { title: "2 sessions are working" });

    expect(store.open?.title).toBe("2 sessions are working");
    expect(store.pinned).toBe(true);
  });

  it("drops a notice without a chip when it settles", () => {
    const store = useNoticesStore();
    store.post({ id: "hello", title: "Hello" });
    store.openNext();
    store.settle();

    expect(store.notices).toHaveLength(0);
  });

  it("keeps a quiet notice as a chip only, and lets it expire", () => {
    vi.useFakeTimers();
    const store = useNoticesStore();
    store.post({ id: "updated:app:0.37.0", title: "Updated", chip: "Updated to 0.37.0", quiet: true, expiresMs: 5000 });

    expect(store.nextWaiting).toBeNull();
    expect(store.chips).toHaveLength(1);

    vi.advanceTimersByTime(5000);
    expect(store.chips).toHaveLength(0);
  });

  it("removes a family of notices, keeping one", () => {
    const store = useNoticesStore();
    store.post(update("0.37.0"));
    store.post(update("0.37.1"));
    store.post({ id: "update:server:0.37.0", title: "Server", chip: "Server" });

    store.removeWhere("update:app:", "update:app:0.37.1");

    expect(store.notices.map((notice) => notice.id)).toEqual(["update:app:0.37.1", "update:server:0.37.0"]);
    expect(store.nextWaiting).toBe("update:app:0.37.1");
  });
});
