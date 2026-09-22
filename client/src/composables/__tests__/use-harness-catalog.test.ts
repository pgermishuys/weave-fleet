import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import { clearHarnessCatalogCache, useHarnessCatalog } from "@/composables/use-harness-catalog";
import type { DomainEvent } from "@/lib/domain-events";
import { HARNESS_CATALOG_CHANGED } from "@/lib/harness-catalog-changes";
import { flushAll, mountComposable } from "./test-utils";

const { mockApi, handlers } = vi.hoisted(() => ({
  mockApi: { GET: vi.fn() },
  handlers: new Set<(event: DomainEvent) => void>(),
}));

vi.mock("@/api/client", () => ({ api: mockApi }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_topic: string, handler: (event: DomainEvent) => void) => {
    handlers.add(handler);
    return () => handlers.delete(handler);
  },
}));

function push(payload: Record<string, unknown>): void {
  const event = { type: HARNESS_CATALOG_CHANGED, payload } as unknown as DomainEvent;
  for (const handler of [...handlers]) handler(event);
}

function catalogWith(agent: string) {
  return {
    supported: true,
    agents: [{ name: agent, mode: "primary", hidden: false }],
    providers: [],
    defaultAgent: agent,
  };
}

function answer(...agents: string[]) {
  const queue = [...agents];
  mockApi.GET.mockImplementation(async () => {
    const agent = queue.length > 1 ? queue.shift()! : queue[0]!;
    if (agent === "fail") {
      return { data: undefined, error: { error: "down" }, response: new Response(null, { status: 502 }) };
    }
    return { data: catalogWith(agent), error: undefined, response: new Response(null, { status: 200 }) };
  });
}

async function mountCatalog(profile?: string) {
  return mountComposable(() => useHarnessCatalog(shallowRef("opencode2"), shallowRef<string | null>("/work/rocket"), shallowRef(profile)));
}

const change = { harnessType: "opencode2", directory: "/work/rocket", quickChat: false, profileIds: ["none"], sessionIds: [] };

describe("useHarnessCatalog pushed changes", () => {
  beforeEach(() => {
    clearHarnessCatalogCache();
    mockApi.GET.mockReset();
    handlers.clear();
  });

  it("asks again when the harness says the folder's catalog changed", async () => {
    answer("build", "reviewer");
    const { result } = await mountCatalog("none");
    expect(result.agents.value.map((agent) => agent.id)).toEqual(["build"]);

    push(change);
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledTimes(2);
    expect(result.agents.value.map((agent) => agent.id)).toEqual(["reviewer"]);
  });

  it("ignores a change to another folder, harness or profile", async () => {
    answer("build");
    await mountCatalog("p1");

    push({ ...change, directory: "/work/comet", profileIds: ["p1"] });
    push({ ...change, harnessType: "opencode", profileIds: ["p1"] });
    push({ ...change, profileIds: ["p2"] });
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledTimes(1);
  });

  it("keeps the catalog it has when asking again fails", async () => {
    answer("build", "fail");
    const { result } = await mountCatalog("none");

    push(change);
    await flushAll();

    expect(result.agents.value.map((agent) => agent.id)).toEqual(["build"]);
    expect(result.error.value).toBe("down");
  });

  it("asks again next time for a cached catalog that changed while it wasn't shown", async () => {
    answer("build", "reviewer");
    const first = await mountCatalog("none");
    first.wrapper.unmount();

    push(change);
    const { result } = await mountCatalog("none");

    expect(mockApi.GET).toHaveBeenCalledTimes(2);
    expect(result.agents.value.map((agent) => agent.id)).toEqual(["reviewer"]);
  });
});
