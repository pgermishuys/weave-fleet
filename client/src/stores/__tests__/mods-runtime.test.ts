import { flushPromises } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ModsRuntimeJob, ModsRuntimeView } from "@/lib/mods-runtime";

const { handlers } = vi.hoisted(() => ({ handlers: [] as ((event: { type: string; payload: unknown }) => void)[] }));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: { type: string; payload: unknown }) => void) => {
    if (topic === "sessions") handlers.push(handler);
    return () => handlers.splice(handlers.indexOf(handler), 1);
  },
}));
vi.mock("@/api/client", () => ({ api: { GET: vi.fn().mockResolvedValue({ data: {} }), PUT: vi.fn().mockResolvedValue({}) } }));

const { useModsRuntimeStore } = await import("@/stores/mods-runtime");

const RUNTIME = "/api/features/mods/runtime";

function view(job: ModsRuntimeJob | null = null): ModsRuntimeView {
  return {
    bun: null,
    release: { version: "1.4.2", hasBuild: true, size: 35_000_000, installFolder: "~/.weave/runtimes/bun/1.4.2", source: "github.com/oven-sh/bun" },
    installedSize: 0,
    job,
  };
}

function job(overrides: Partial<ModsRuntimeJob> = {}): ModsRuntimeJob {
  return { phase: "downloading", kind: "install", version: "1.4.2", bytesReceived: 1_000_000, bytesTotal: 35_000_000, ...overrides };
}

let calls: { method: string; path: string; body?: unknown }[];
let answer: ModsRuntimeView;
let failWith: { status: number; error: string } | null;

function emit(payload: { job: ModsRuntimeJob | null; reason: string }): void {
  for (const handler of [...handlers]) handler({ type: "mods.runtime", payload });
}

describe("mods runtime store", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handlers.length = 0;
    calls = [];
    answer = view();
    failWith = null;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = new URL(String(input), "http://localhost");
        calls.push({ method: (init?.method ?? "GET").toUpperCase(), path: url.pathname, body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined });
        if (failWith && calls.at(-1)!.method !== "GET") {
          return new Response(JSON.stringify({ error: failWith.error }), { status: failWith.status });
        }
        if (url.pathname === `${RUNTIME}/found`) return new Response(JSON.stringify({ candidates: [{ path: "/b", status: "usable", version: "1.4.5" }] }));
        return new Response(JSON.stringify(answer));
      }),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("load() fetches the view once, and nothing else", async () => {
    const store = useModsRuntimeStore();
    answer = view(job());

    await store.load();

    expect(store.view?.job?.phase).toBe("downloading");
    expect(calls.map((call) => `${call.method} ${call.path}`)).toEqual([`GET ${RUNTIME}`]);
  });

  it("findBuns() asks for candidates only when called", async () => {
    const store = useModsRuntimeStore();
    await store.load();
    expect(calls.some((call) => call.path.endsWith("/found"))).toBe(false);

    const found = await store.findBuns();

    expect(found).toEqual([{ path: "/b", status: "usable", version: "1.4.5" }]);
  });

  it("merges a downloading event's job into the view without a request", async () => {
    const store = useModsRuntimeStore();
    await store.load();
    store.listen();
    calls = [];

    emit({ job: job({ bytesReceived: 12_000_000 }), reason: "job" });
    await flushPromises();

    expect(store.view?.job?.bytesReceived).toBe(12_000_000);
    expect(calls).toHaveLength(0);
  });

  it("refetches the view when the job changes phase", async () => {
    const store = useModsRuntimeStore();
    await store.load();
    store.listen();
    calls = [];
    answer = view(job({ phase: "verifying" }));

    emit({ job: job({ phase: "verifying" }), reason: "job" });
    await flushPromises();

    expect(calls.map((call) => `${call.method} ${call.path}`)).toEqual([`GET ${RUNTIME}`]);
    expect(store.view?.job?.phase).toBe("verifying");
  });

  it("refetches on an event with no job (the user's own Bun changed)", async () => {
    const store = useModsRuntimeStore();
    await store.load();
    store.listen();
    calls = [];

    emit({ job: null, reason: "bun-path" });
    await flushPromises();

    expect(calls).toHaveLength(1);
  });

  it("listens once, however often it's asked, and stops", async () => {
    const store = useModsRuntimeStore();

    store.listen();
    store.listen();

    expect(handlers).toHaveLength(1);
    store.stop();
    expect(handlers).toHaveLength(0);
  });

  it("install() posts and takes the view the server answers", async () => {
    const store = useModsRuntimeStore();
    answer = view(job());

    const error = await store.install();

    expect(error).toBeNull();
    expect(calls).toEqual([{ method: "POST", path: `${RUNTIME}/install`, body: undefined }]);
    expect(store.view?.job?.phase).toBe("downloading");
  });

  it("install() returns the server's message when it refuses", async () => {
    const store = useModsRuntimeStore();
    failWith = { status: 409, error: "Fleet uses your own Bun at /opt/bun. Clear it to use Fleet's own." };

    expect(await store.install()).toBe("Fleet uses your own Bun at /opt/bun. Clear it to use Fleet's own.");
  });

  it("cancel() posts cancel", async () => {
    const store = useModsRuntimeStore();

    await store.cancel();

    expect(calls).toEqual([{ method: "POST", path: `${RUNTIME}/cancel`, body: undefined }]);
  });

  it("setBunPath() puts the path, or null to clear it, and returns the server's 400", async () => {
    const store = useModsRuntimeStore();

    expect(await store.setBunPath("/opt/bun")).toBeNull();
    expect(await store.setBunPath(null)).toBeNull();
    expect(calls.map((call) => call.body)).toEqual([{ path: "/opt/bun" }, { path: null }]);

    failWith = { status: 400, error: "That Bun is older than 1.4." };
    expect(await store.setBunPath("/opt/old")).toBe("That Bun is older than 1.4.");
  });
});
