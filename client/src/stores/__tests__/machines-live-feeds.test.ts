import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { nextTick } from "vue";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, LIVE_MACHINES_PREFERENCE_KEY, loadSessionMachines, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { FeedError, HIDDEN_CLOSE_MS, type FeedRequest, type MachineFeedOptions } from "@/lib/machine-feed";
import { useMachinesStore, type MachineInfo } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

vi.mock("@/lib/machines", async (original) => ({ ...await original<typeof import("@/lib/machines")>(), switchToMachine: vi.fn() }));

const { feeds, FakeFeed } = vi.hoisted(() => {
  const feeds: InstanceType<typeof FakeFeed>[] = [];
  class FakeFeed {
    started = false;
    stopped = false;
    refreshes = 0;
    constructor(readonly options: MachineFeedOptions<object>) {
      feeds.push(this);
    }
    async start() {
      this.started = true;
    }
    async stop() {
      this.stopped = true;
    }
    async refresh() {
      this.refreshes += 1;
    }
    push(snapshot: Record<string, unknown>) {
      this.options.onChange(snapshot as never);
    }
  }
  return { feeds, FakeFeed };
});

vi.mock("@/lib/machine-feed", async (original) => ({ ...await original<typeof import("@/lib/machine-feed")>(), MachineFeed: FakeFeed }));

const homeInfo: MachineInfo = {
  id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  name: "atlas",
  hostName: "atlas",
  os: "linux",
  version: "0.40.0",
  apiVersion: 1,
  authMode: "token",
  remoteReachable: false,
  requiresToken: false,
};

const miniInfo: MachineInfo = { ...homeInfo, id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name: "mini", hostName: "mini", os: "macos" };
const labInfo: MachineInfo = { ...homeInfo, id: "cccccccccccccccccccccccccccccccc", name: "lab-win", hostName: "lab-win", os: "windows" };

const mini: MachineConnection = {
  id: miniInfo.id,
  name: "mini",
  baseUrl: "http://mini.example.test:2113",
  token: "mini-token-0123456789",
  os: "macos",
  addedAt: "2026-10-08T00:00:00.000Z",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function session(id: string, title: string, sessionStatus = "idle") {
  return {
    session: { id, title, time: { created: 1, updated: 2 } },
    instanceId: `inst-${id}`,
    sessionStatus,
    activityStatus: sessionStatus === "active" ? "busy" : "idle",
    retentionStatus: "active",
    parentSessionId: null,
  };
}

function live(read: { sessions: unknown[]; projects?: unknown[] | null; identity?: MachineInfo | null }, lastHeardAt: number) {
  return { projects: null, identity: null, ...read, status: "live", lastHeardAt, error: null };
}

describe("machines store with every machine live", () => {
  let routes: Record<string, (init: RequestInit) => Response>;
  let fetchMock: ReturnType<typeof vi.fn>;
  let visibility: DocumentVisibilityState;

  const sessionReads = (base: string) => fetchMock.mock.calls.filter(([url]) => String(url).startsWith(`${base}/api/sessions`)).length;

  function setSwitch(on: boolean): void {
    const preferences = usePreferencesStore();
    preferences.hasFetched = true;
    preferences.preferences = { ...preferences.preferences, [LIVE_MACHINES_PREFERENCE_KEY]: on ? "true" : "false" };
  }

  function setVisibility(state: DocumentVisibilityState): void {
    visibility = state;
    document.dispatchEvent(new Event("visibilitychange"));
  }

  beforeEach(() => {
    vi.useFakeTimers();
    feeds.length = 0;
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([mini]);
    visibility = "visible";
    vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
    routes = {
      "/api/machine": () => json(homeInfo),
      "/api/sessions": () => json([session("home-1", "Rate limit headers")]),
      [`${mini.baseUrl}/api/machine`]: () => json(miniInfo),
      [`${mini.baseUrl}/api/sessions`]: () => json([session("mini-1", "Hero image sizes")]),
      "http://lab.example.test:2113/api/machine": () => json(labInfo),
    };
    fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const path = String(input).split("?")[0];
      const route = routes[path];
      return route ? route(init) : json({ error: `no route ${path}` }, 404);
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("polls as before, with no feeds, while the switch is off", async () => {
    const store = useMachinesStore();
    setSwitch(false);
    const stop = store.startPolling();
    await vi.advanceTimersByTimeAsync(0);
    expect(sessionReads(mini.baseUrl)).toBe(1);

    await vi.advanceTimersByTimeAsync(15_000);

    expect(sessionReads(mini.baseUrl)).toBe(2);
    expect(feeds).toHaveLength(0);
    stop();
  });

  it("opens one feed per machine that isn't live, and stops polling them", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await vi.advanceTimersByTimeAsync(15_000);

    expect(feeds).toHaveLength(1);
    expect(feeds[0].started).toBe(true);
    expect(feeds[0].options.target).toEqual({ machineId: mini.id, baseUrl: mini.baseUrl, token: mini.token });
    expect(sessionReads(mini.baseUrl)).toBe(0);
    stop();
  });

  it("shows what a feed reads as it reads it", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();
    expect(store.others[mini.id].loading).toBe(true);

    feeds[0].push(live({ sessions: [session("mini-1", "Hero image sizes", "active")], projects: [{ id: "p1", name: "lumen-site", type: "folder", position: 0 }], identity: miniInfo }, 1));
    expect(store.others[mini.id]).toMatchObject({ error: null, loading: false, projects: [{ name: "lumen-site" }] });
    expect(store.others[mini.id].sessions[0].sessionStatus).toBe("active");
    expect(loadSessionMachines()["mini-1"]).toBe(mini.id);

    feeds[0].push(live({ sessions: [session("mini-1", "Hero image sizes", "waiting_input")] }, 2));
    expect(store.others[mini.id].sessions[0].sessionStatus).toBe("waiting_input");
    // A read without projects keeps the ones it had.
    expect(store.others[mini.id].projects).toHaveLength(1);
    stop();
  });

  it("keeps the last list and says why when a feed can't reach its machine", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();
    feeds[0].push(live({ sessions: [session("mini-1", "Hero image sizes")] }, 1));

    feeds[0].push({ ...feeds[0].options.initial, status: "unreachable", lastHeardAt: 1, error: null });
    expect(store.others[mini.id].error).toBe("Can't reach mini.");
    expect(store.others[mini.id].sessions).toHaveLength(1);

    feeds[0].push({ ...feeds[0].options.initial, status: "unreachable", lastHeardAt: 1, error: "mini didn't accept the token." });
    expect(store.others[mini.id].error).toBe("mini didn't accept the token.");

    feeds[0].push(live({ sessions: [session("mini-1", "Hero image sizes")] }, 2));
    expect(store.others[mini.id].error).toBeNull();
    stop();
  });

  it("reads sessions, projects and what the machine can run through the feed's own request", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();
    const asked: string[] = [];
    const request: FeedRequest = async (path) => {
      asked.push(path);
      if (path.startsWith("/api/sessions")) return json([session("mini-1", "Hero image sizes")]);
      if (path === "/api/projects") return json([{ id: "p1", name: "lumen-site", type: "folder", position: 0 }]);
      return json(miniInfo);
    };

    const read = await feeds[0].options.read(request) as { sessions: unknown[]; projects: unknown[]; identity: MachineInfo };

    expect(asked).toEqual(["/api/sessions?limit=100&offset=0", "/api/projects", "/api/machine"]);
    expect(read.sessions).toHaveLength(1);
    expect(read.projects).toHaveLength(1);
    expect(read.identity.name).toBe("mini");
    await expect(feeds[0].options.read(async () => json({}, 401))).rejects.toEqual(new FeedError("mini didn't accept the token."));
    stop();
  });

  it("gives home a same-origin feed when another machine is live, and the live machine none", async () => {
    setActiveMachine(mini);
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();

    expect(feeds.map((feed) => feed.options.target)).toEqual([{ machineId: HOME_MACHINE_KEY, baseUrl: "", token: null }]);
    stop();
  });

  it("opens, restarts and closes feeds as machines are added, get a new token, and are forgotten", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();
    const [first] = feeds;

    await store.addMachine("http://lab.example.test:2113", "lab-token-0123456789");
    await nextTick();
    expect(feeds.map((feed) => feed.options.target.machineId)).toEqual([mini.id, labInfo.id]);
    // Adding asks the new machine's feed to read, rather than polling it besides.
    expect(feeds[1].refreshes).toBe(1);

    await store.updateToken(mini.id, "mini-token-new-0123456789");
    await nextTick();
    expect(first.stopped).toBe(true);
    expect(feeds[2].options.target).toEqual({ machineId: mini.id, baseUrl: mini.baseUrl, token: "mini-token-new-0123456789" });

    first.push(live({ sessions: [session("stale", "From the old feed")] }, 9));
    expect(store.others[mini.id].sessions.map((item) => item.session.id)).not.toContain("stale");

    store.forgetMachine(labInfo.id);
    await nextTick();
    expect(feeds[1].stopped).toBe(true);
    expect(feeds[2].stopped).toBe(false);
    stop();
  });

  it("closes the feeds and polls again when the switch goes off", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();

    setSwitch(false);
    await vi.advanceTimersByTimeAsync(0);

    expect(feeds[0].stopped).toBe(true);
    expect(sessionReads(mini.baseUrl)).toBe(1);
    stop();
  });

  it("closes the feeds with the last caller", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stopSidebar = store.startPolling();
    const stopSettings = store.startPolling();
    await nextTick();

    stopSettings();
    expect(feeds[0].stopped).toBe(false);
    stopSidebar();
    expect(feeds[0].stopped).toBe(true);
  });

  it("closes the feeds after five minutes off screen, and opens them again on return", async () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    await nextTick();

    setVisibility("hidden");
    setVisibility("visible");
    expect(feeds[0].stopped).toBe(false);
    expect(feeds[0].refreshes).toBe(1);

    setVisibility("hidden");
    await vi.advanceTimersByTimeAsync(HIDDEN_CLOSE_MS);
    expect(feeds[0].stopped).toBe(true);

    setVisibility("visible");
    expect(feeds).toHaveLength(2);
    expect(feeds[1].started).toBe(true);
    stop();
  });
});
