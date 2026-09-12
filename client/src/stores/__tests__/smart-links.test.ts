import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SmartLinkWire } from "@/lib/smart-links";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import { useSmartLinksStore } from "@/stores/smart-links";

function wire(id: string, relationship: string, overrides: Partial<SmartLinkWire> = {}): SmartLinkWire {
  return {
    id,
    sessionId: "s1",
    url: `https://github.com/o/r/pull/${id}`,
    providerId: "github",
    resourceType: "pull_request",
    resourceId: `o/r#${id}`,
    title: `o/r #${id}: Link ${id}`,
    status: "open",
    statusLabel: "Open",
    metadataJson: null,
    isDismissed: false,
    isTerminal: false,
    createdAt: "",
    updatedAt: "",
    relationship,
    enrichmentStatus: "resolved",
    lastCheckedAt: null,
    ...overrides,
  };
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("useSmartLinksStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
  });

  it("loads a session's links once", async () => {
    apiFetchMock.mockResolvedValue(json([wire("1", "own")]));
    const store = useSmartLinksStore();

    await Promise.all([store.ensureLoaded("s1"), store.ensureLoaded("s1")]);
    await store.ensureLoaded("s1");

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/smart-links/all");
    expect(store.visibleLinks("s1")).toHaveLength(1);
  });

  it("orders header links origin, own, pinned and leaves mentions out", () => {
    const store = useSmartLinksStore();
    store.setLinks("s1", [wire("1", "mentioned"), wire("2", "pinned"), wire("3", "own"), wire("4", "origin")]);

    expect(store.headerLinks("s1").map((l) => l.id)).toEqual(["4", "3", "2"]);
  });

  it("applies pushed updates in place", () => {
    const store = useSmartLinksStore();
    store.setLinks("s1", [wire("1", "mentioned")]);

    store.upsertLink(wire("1", "own", { status: "merged", isTerminal: true }));
    store.upsertLink(wire("2", "mentioned"));

    expect(store.visibleLinks("s1").map((l) => [l.id, l.relationship, l.status])).toEqual([
      ["1", "own", "merged"],
      ["2", "mentioned", "open"],
    ]);
  });

  it("pins optimistically and rolls back when the server refuses", async () => {
    const store = useSmartLinksStore();
    store.setLinks("s1", [wire("1", "mentioned")]);

    apiFetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await store.setPinned("s1", "1", true);
    expect(store.visibleLinks("s1")[0]?.relationship).toBe("pinned");
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/sessions/s1/smart-links/1/pin", { method: "PATCH" });

    apiFetchMock.mockResolvedValueOnce(new Response(null, { status: 500 }));
    await store.setPinned("s1", "1", false);
    expect(store.visibleLinks("s1")[0]?.relationship).toBe("pinned");
  });

  it("explains a URL that isn't a GitHub pull request or issue", async () => {
    const store = useSmartLinksStore();
    apiFetchMock.mockResolvedValueOnce(new Response("bad", { status: 400 }));

    await expect(store.addLink("s1", "https://example.com")).resolves.toBe("Paste a link to a GitHub pull request or issue.");

    apiFetchMock.mockResolvedValueOnce(json(wire("9", "pinned")));
    await expect(store.addLink("s1", "https://github.com/o/r/pull/9")).resolves.toBeNull();
    expect(store.headerLinks("s1").map((l) => l.id)).toEqual(["9"]);
  });

  it("reports the newest check time", () => {
    const store = useSmartLinksStore();
    store.setLinks("s1", [
      wire("1", "own", { lastCheckedAt: "2026-09-12T10:00:00Z" }),
      wire("2", "mentioned", { lastCheckedAt: "2026-09-12T11:00:00Z" }),
    ]);

    expect(store.lastCheckedAt("s1")).toBe("2026-09-12T11:00:00Z");
  });
});
