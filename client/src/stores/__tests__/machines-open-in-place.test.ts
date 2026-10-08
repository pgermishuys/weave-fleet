import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionListItem } from "@/api/client";
import {
  HOME_MACHINE_KEY,
  LIVE_MACHINES_PREFERENCE_KEY,
  loadLiveMachinesHint,
  saveLiveMachinesHint,
  saveMachines,
  setActiveMachine,
  type MachineConnection,
} from "@/lib/machines";
import type { MachineFeedOptions } from "@/lib/machine-feed";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";
import { useSessionsStore } from "@/stores/sessions";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useAbortSession } from "@/composables/use-session-actions";

/**
 * With "Keep every machine live" on, another machine's session opens in place: its views ask the machine it's on
 * (`sessionTarget`), and each machine's live feed shares its event hub with them. Off, everything asks the live
 * machine, as before.
 */

vi.mock("@/lib/machines", async (original) => ({ ...await original<typeof import("@/lib/machines")>(), switchToMachine: vi.fn() }));

const { feeds, feedHubFor, retryConnection } = vi.hoisted(() => ({
  feeds: [] as { options: MachineFeedOptions<object> }[],
  feedHubFor: vi.fn((machine: { key: string }) => ({ sharedHubOf: machine.key })),
  retryConnection: vi.fn(),
}));

vi.mock("@/lib/machine-feed", async (original) => ({
  ...await original<typeof import("@/lib/machine-feed")>(),
  MachineFeed: class {
    constructor(readonly options: MachineFeedOptions<object>) {
      feeds.push(this);
    }
    async start() {}
    async stop() {}
    async refresh() {}
  },
}));

vi.mock("@/composables/use-signalr-socket", async (original) => ({
  ...await original<typeof import("@/composables/use-signalr-socket")>(),
  feedHubFor,
  retryConnection,
}));

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://mini.example.test:2113",
  token: "mini-token-0123456789",
  os: "macos",
  addedAt: "2026-10-08T00:00:00.000Z",
};

const lab: MachineConnection = { ...mini, id: "cccccccccccccccccccccccccccccccc", name: "lab-win", baseUrl: "http://lab.example.test:2113" };

function setSwitch(on: boolean): void {
  const preferences = usePreferencesStore();
  preferences.hasFetched = true;
  preferences.preferences = { ...preferences.preferences, [LIVE_MACHINES_PREFERENCE_KEY]: on ? "true" : "false" };
}

function row(id: string, title: string): SessionListItem {
  return { session: { id, title, time: { created: 1, updated: 2 } }, instanceId: `inst-${id}` } as SessionListItem;
}

describe("opening another machine's session in place", () => {
  beforeEach(() => {
    feeds.length = 0;
    feedHubFor.mockClear();
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([mini, lab]);
    vi.stubGlobal("fetch", vi.fn(async () => Response.json([])));
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("asks the live machine for every session while the switch is off", () => {
    const store = useMachinesStore();
    setSwitch(false);
    store.rememberSessions(mini.id, ["mini-1"]);

    expect(store.opensInPlace).toBe(false);
    expect(store.sessionTarget("mini-1")).toMatchObject({ key: HOME_MACHINE_KEY, isLive: true });
  });

  it("asks the machine a session is on with the switch on; the live machine for its own and unknown ones", () => {
    const store = useMachinesStore();
    setSwitch(true);
    store.rememberSessions(mini.id, ["mini-1"]);
    store.rememberSessions(HOME_MACHINE_KEY, ["home-1"]);

    expect(store.sessionTarget("mini-1")).toMatchObject({ key: mini.id, isLive: false, connection: mini });
    expect(store.sessionTarget("home-1")).toMatchObject({ key: HOME_MACHINE_KEY, isLive: true });
    expect(store.sessionTarget("nobody-knows")).toMatchObject({ key: HOME_MACHINE_KEY, isLive: true });
    expect(store.sessionTarget(null)).toMatchObject({ isLive: true });
  });

  it("asks home for home's sessions while another machine is live", () => {
    setActiveMachine(mini);
    const store = useMachinesStore();
    setSwitch(true);
    store.rememberSessions(HOME_MACHINE_KEY, ["home-1"]);

    expect(store.sessionTarget("home-1")).toMatchObject({ key: HOME_MACHINE_KEY, isLive: false, connection: null });
  });

  it("knows the machine of a session started or forked there in place before any list has it", () => {
    const store = useMachinesStore();
    setSwitch(true);
    store.rememberSessions(mini.id, ["moved"]);

    useSessionsStore().upsertElsewhere(lab.id, row("moved", "Dark mode contrast pass"));

    expect(store.sessionTarget("moved").key).toBe(lab.id);
  });

  it("asks the live machine again for the sessions of a machine that's forgotten, and drops their rows", () => {
    const store = useMachinesStore();
    setSwitch(true);
    store.rememberSessions(mini.id, ["mini-1"]);
    useSessionsStore().upsertElsewhere(mini.id, row("mini-1", "Hero image sizes"));

    store.forgetMachine(mini.id);

    expect(store.sessionTarget("mini-1")).toMatchObject({ isLive: true });
    expect(useSessionsStore().sessionById("mini-1")).toBeNull();
  });

  it("goes by how the page started until the preferences load, then remembers the switch for the next start", async () => {
    saveLiveMachinesHint(true);
    const store = useMachinesStore();

    expect(store.opensInPlace).toBe(true);

    setSwitch(false);
    await Promise.resolve();
    expect(store.opensInPlace).toBe(false);
    expect(loadLiveMachinesHint()).toBe(false);

    setSwitch(true);
    await Promise.resolve();
    expect(loadLiveMachinesHint()).toBe(true);
  });

  it("says a machine its feed can't read isn't answering, with the switch on only", () => {
    const store = useMachinesStore();
    store.others = { [mini.id]: { sessions: [], projects: [], error: "Can't reach mini.", loadedAt: 1, loading: false } };

    setSwitch(false);
    expect(store.isAnswering(mini.id)).toBe(true);

    setSwitch(true);
    expect(store.isAnswering(mini.id)).toBe(false);
    expect(store.isAnswering(lab.id)).toBe(true);
    expect(store.isAnswering(HOME_MACHINE_KEY)).toBe(true);

    store.others = { [mini.id]: { sessions: [], projects: [], error: null, loadedAt: 2, loading: false } };
    expect(store.isAnswering(mini.id)).toBe(true);
  });

  it("archives and stops a session opened in place on its own machine, from outside its views", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      urls.push(input instanceof Request ? input.url : String(input));
      return Response.json({});
    }));
    const store = useMachinesStore();
    setSwitch(true);
    store.rememberSessions(mini.id, ["mini-1"]);

    const queue = useArchiveQueueStore();
    queue.archive(["mini-1"]);
    await queue.flush();
    // The command palette's Interrupt.
    await useAbortSession((id) => store.sessionTarget(id)).abortSession("mini-1");

    expect(urls.filter((url) => url.includes("/api/sessions/"))).toEqual([
      `${mini.baseUrl}/api/sessions/mini-1/retention`,
      `${mini.baseUrl}/api/sessions/mini-1/abort`,
    ]);
  });

  it("has a machine's sessions connect again at once when its feed hears from it after it stopped answering", () => {
    retryConnection.mockClear();
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();
    const miniFeed = feeds.find((feed) => feed.options.target.machineId === mini.id)!;
    const read = (lastHeardAt: number) => ({ sessions: [], projects: null, identity: null, status: "live", lastHeardAt, error: null });

    miniFeed.options.onChange(read(1) as never);
    expect(retryConnection).not.toHaveBeenCalled();
    miniFeed.options.onChange({ ...read(1), status: "unreachable", error: "Can't reach mini." } as never);
    expect(store.isAnswering(mini.id)).toBe(false);
    miniFeed.options.onChange(read(2) as never);

    expect(store.isAnswering(mini.id)).toBe(true);
    expect(retryConnection).toHaveBeenCalledOnce();
    expect(retryConnection.mock.calls[0]![0]).toMatchObject({ key: mini.id });
    stop();
  });

  it("gives each machine's live feed that machine's event hub, shared with its open sessions", () => {
    const store = useMachinesStore();
    setSwitch(true);
    const stop = store.startPolling();

    const hubs = feeds.map((feed) => feed.options.createHub?.(`${feed.options.target.baseUrl}/hubs/session-events`, feed.options.target.token));

    expect(hubs).toEqual([{ sharedHubOf: mini.id }, { sharedHubOf: lab.id }]);
    expect(feedHubFor.mock.calls.map(([machine]) => machine)).toEqual([
      expect.objectContaining({ key: mini.id, connection: mini }),
      expect.objectContaining({ key: lab.id, connection: lab }),
    ]);
    stop();
  });
});
