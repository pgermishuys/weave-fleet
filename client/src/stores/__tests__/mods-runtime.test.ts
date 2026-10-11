import { flushPromises } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ModsRuntimeJob, ModsRuntimeView } from "@/lib/mods-runtime";

const { handlers } = vi.hoisted(() => ({ handlers: [] as ((event: { type: string; payload: unknown }) => void)[] }));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, _topic: string, handler: (event: { type: string; payload: unknown }) => void) => {
    handlers.push(handler);
    return () => handlers.splice(handlers.indexOf(handler), 1);
  },
}));

const { useModsRuntimeStore } = await import("@/stores/mods-runtime");

const RUNTIME = "/api/features/mods/runtime";

function job(overrides: Partial<ModsRuntimeJob> = {}): ModsRuntimeJob {
  return { phase: "downloading", version: "1.4.2", bytesReceived: 1_000_000, bytesTotal: 35_000_000, ...overrides };
}

const view = (current: ModsRuntimeJob | null = null) => ({ bun: null, job: current }) as ModsRuntimeView;

let calls: string[];
let answer: ModsRuntimeView;

function emit(payload: { job: ModsRuntimeJob | null }, type = "mods.runtime"): void {
  for (const handler of [...handlers]) handler({ type, payload });
}

describe("mods runtime store", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handlers.length = 0;
    calls = [];
    answer = view();
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const request = input instanceof Request ? input : null;
        const method = (init?.method ?? request?.method ?? "GET").toUpperCase();
        calls.push(`${method} ${new URL(request?.url ?? String(input), "http://localhost").pathname}`);
        return new Response(JSON.stringify(answer));
      }),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("merges a downloading event's job into the view without a request", async () => {
    const store = useModsRuntimeStore();
    answer = view(job());
    await store.load();
    store.listen();
    calls = [];

    emit({ job: job({ bytesReceived: 12_000_000 }) });
    await flushPromises();

    expect(store.view?.job?.bytesReceived).toBe(12_000_000);
    expect(calls).toEqual([]);
  });

  it("refetches the view when the job changes phase", async () => {
    const store = useModsRuntimeStore();
    await store.load();
    store.listen();
    calls = [];
    answer = view(job({ phase: "verifying" }));

    emit({ job: job({ phase: "verifying" }) });
    await flushPromises();

    expect(calls).toEqual([`GET ${RUNTIME}`]);
    expect(store.view?.job?.phase).toBe("verifying");
  });

  it("reads the preferences again when the server turned the switch off after a failed install", async () => {
    const store = useModsRuntimeStore();
    store.listen();

    emit({ reason: "switch" } as never, "mods.changed");
    await flushPromises();

    expect(calls).toContain("GET /api/preferences");
  });

  it("install() and cancel() post, and take the view the server answers", async () => {
    const store = useModsRuntimeStore();
    answer = view(job());

    expect(await store.install()).toBeNull();
    expect(await store.cancel()).toBeNull();

    expect(calls).toEqual([`POST ${RUNTIME}/install`, `POST ${RUNTIME}/cancel`]);
    expect(store.view?.job?.phase).toBe("downloading");
  });
});
