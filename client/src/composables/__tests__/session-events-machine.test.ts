import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { MACHINE_TARGET, targetFor, type MachineTarget } from "@/lib/machine-target";
import { setActiveMachine, type MachineConnection } from "@/lib/machines";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { useSessionStream, _resetKeptStreamsForTesting } from "@/composables/use-session-stream";
import { useSessionRecap } from "@/composables/use-session-recap";
import { useFileLiveUpdates } from "@/composables/use-file-live-updates";
import { useServerCanvases } from "@/composables/use-server-canvases";
import { useSessionTerminals } from "@/composables/use-session-terminals";
import { useAgentBrowser } from "@/composables/use-agent-browser";
import { useSessionPermissions } from "@/composables/use-session-permissions";
import { useSessionProgress } from "@/composables/use-session-progress";
import { useFileBrowser } from "@/composables/use-file-browser";
import { useSessionQueue } from "@/composables/use-session-queue";
import { useDiffs } from "@/composables/use-diffs";
import { useSessionRetry } from "@/composables/use-session-retry";
import { useRunningWork, _resetRunningWorkForTesting } from "@/composables/use-running-work";
import { useModels } from "@/composables/use-models";
import { useAgents } from "@/composables/use-agents";
import { useMachineReachability } from "@/composables/phone/use-machine-reachability";
import { useAppShellStore } from "@/stores/app-shell";

/**
 * Code working on a session hears its events from the session's machine. The event hub's calls take the machine
 * (one connection each, see `use-signalr-socket.ts`); this records every call the session composables make and
 * checks each names the machine provided, or the live one when none is. Running work's app-wide listener feeds the
 * status bar's counter, which is the interface's, so it stays on the live machine.
 */

interface SocketCall {
  call: string;
  machine: MachineTarget;
}

const { socketCalls, snapshots } = vi.hoisted(() => ({
  socketCalls: [] as { call: string; machine: { key: string } }[],
  snapshots: [] as ((snapshot: unknown) => void)[],
}));

vi.mock("@/composables/use-signalr-socket", () => {
  const record = (call: string) => (machine: { key: string }) => {
    socketCalls.push({ call, machine });
    return () => {};
  };
  return {
    useWeaveSocket: (machine: { key: string }) => {
      socketCalls.push({ call: "useWeaveSocket", machine });
      return {
        subscribeV2: (topic: string, onSnapshot: (snapshot: unknown) => void) => {
          socketCalls.push({ call: `subscribeV2 ${topic}`, machine });
          snapshots.push(onSnapshot);
          return () => {};
        },
      };
    },
    onReconnect: record("onReconnect"),
    onDisconnect: record("onDisconnect"),
    onConnectionLost: record("onConnectionLost"),
    onGlobalEvent: (machine: { key: string }, topic: string) => {
      socketCalls.push({ call: `onGlobalEvent ${topic}`, machine });
      return () => {};
    },
    setSessionFocus: record("setSessionFocus"),
    loadSessionHistory: async (machine: { key: string }) => {
      socketCalls.push({ call: "loadSessionHistory", machine });
      return null;
    },
    isWeaveSocketConnected: () => true,
  };
});

vi.mock("@/composables/use-weave-socket", async () => await import("@/composables/use-signalr-socket"));

const falcon: MachineConnection = {
  id: "f0a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5",
  name: "falcon",
  baseUrl: "http://100.64.90.72:2113",
  token: "falcon-token_0123456789-abcdef",
  addedAt: "2026-09-26T00:00:00.000Z",
};

function snapshotWithOlderPages(): SessionSnapshot {
  return {
    session: { id: "s1", title: "Harbor API docs", status: "idle" },
    messages: [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: null,
    hasMore: true,
    cursor: "c1",
    isPartial: false,
  } as unknown as SessionSnapshot;
}

interface SessionUse {
  name: string;
  /** Called in `setup`; what it returns is done once mounted. */
  use: () => (() => unknown) | void;
  /** Calls that are the interface's, so on the live machine whichever machine is provided. */
  live?: string[];
}

const uses: SessionUse[] = [
  {
    name: "streams the conversation and loads older messages",
    use: () => {
      const stream = useSessionStream("s1");
      return () => {
        for (const deliver of snapshots) deliver(snapshotWithOlderPages());
        stream.loadOlder();
      };
    },
  },
  { name: "reports the recap focus", use: () => { useSessionRecap("s1"); } },
  { name: "follows the open file", use: () => { useFileLiveUpdates("s1"); } },
  { name: "follows the session's canvases", use: () => { useServerCanvases("s1"); } },
  {
    name: "follows the session's terminals",
    use: () => {
      const appShell = useAppShellStore();
      appShell.config = { ...appShell.config, terminalEnabled: true };
      useSessionTerminals("s1");
    },
  },
  { name: "follows the agent's browser", use: () => { useAgentBrowser("s1"); } },
  { name: "follows permission asks", use: () => { useSessionPermissions("s1"); } },
  { name: "follows the session's progress", use: () => { useSessionProgress("s1"); } },
  { name: "follows the session's folder", use: () => { const { loadRoot } = useFileBrowser(ref("s1")); return () => loadRoot(); } },
  { name: "follows the queue", use: () => { useSessionQueue("s1"); } },
  { name: "follows the changes", use: () => { useDiffs("s1"); } },
  { name: "follows a scheduled retry", use: () => { useSessionRetry("s1"); } },
  { name: "follows running work", use: () => { useRunningWork("s1"); }, live: ["onGlobalEvent sessions"] },
  { name: "follows changes to the models", use: () => { useModels("s1"); } },
  { name: "follows changes to the agents", use: () => { useAgents("s1"); } },
  { name: "follows whether the machine answers", use: () => { useMachineReachability(() => {}); } },
];

async function run(use: SessionUse, machine: MachineConnection | null | undefined): Promise<SocketCall[]> {
  let act = undefined as (() => unknown) | void;
  const Harness = defineComponent({
    setup() {
      act = use.use();
      return () => h("div");
    },
  });
  const provide = machine === undefined ? {} : { [MACHINE_TARGET]: () => targetFor(machine) };
  const wrapper = mount(Harness, { global: { provide } });
  await flushPromises();
  try {
    await act?.();
  } catch {
    // Only where the calls went matters here.
  }
  await flushPromises();
  wrapper.unmount();
  return socketCalls as SocketCall[];
}

describe("session events come from the session's machine", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    setActiveMachine(null);
    _resetRunningWorkForTesting();
    _resetKeptStreamsForTesting();
    socketCalls.length = 0;
    snapshots.length = 0;
    vi.stubGlobal("fetch", vi.fn(async () => Response.json({})));
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it.each(uses)("$name on the machine provided, while home is live", async (use) => {
    const calls = await run(use, falcon);

    expect(calls.length).toBeGreaterThan(0);
    for (const { call, machine } of calls) {
      const expected = use.live?.includes(call) ? "home" : falcon.id;
      expect(machine.key, call).toBe(expected);
    }
  });

  it.each(uses)("$name on the live machine when none is provided", async (use) => {
    setActiveMachine(falcon);

    const calls = await run(use, undefined);

    expect(calls.length).toBeGreaterThan(0);
    for (const { call, machine } of calls) {
      expect(machine.key, call).toBe(falcon.id);
      expect(machine.isLive, call).toBe(true);
    }
  });

  it("loads older messages from the machine the conversation streams from", async () => {
    const calls = await run(uses[0]!, falcon);

    expect(calls.map(({ call }) => call)).toEqual(expect.arrayContaining(["subscribeV2 session:s1", "loadSessionHistory"]));
  });
});
