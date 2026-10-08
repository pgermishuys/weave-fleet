import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import type { RunningWorkItem } from "@/lib/running-work";
import { LIVE_MACHINES_PREFERENCE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { MACHINE_TARGET, targetFor } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * The "From … · mini" chip on a message another session sent opens the sender as the sidebar opens a session: in place
 * with "Keep every machine live" on, by switching to its machine with it off. A sender with no machine of its own is on
 * the machine the message arrived at, which is this session's, live or not.
 */

const { stream, navigate } = vi.hoisted(() => ({
  stream: {
    messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]>,
    runningWork: null as unknown as import("vue").ShallowRef<readonly RunningWorkItem[]>,
  },
  navigate: vi.fn(),
}));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => []),
      runningWork: computed(() => stream.runningWork.value),
      sessionStatus: computed(() => "idle"),
      isLoading: ref(false),
      hasMore: ref(false),
      isLoadingOlder: ref(false),
      isPartial: ref(false),
      loadOlder: () => undefined,
    }),
  };
});

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-models", async () => {
  const { shallowRef: ref } = await import("vue");
  return { useModels: () => ({ models: ref([]) }) };
});
vi.mock("@/composables/use-send-prompt", async () => {
  const { shallowRef: ref } = await import("vue");
  return {
    useSentPrompts: () => ({ sentPrompts: ref([]) }),
    useSendPrompt: () => ({ canSend: ref(true), retryPrompt: vi.fn() }),
    reconcileSentPrompts: vi.fn(),
    clearSentPrompts: vi.fn(),
  };
});
vi.mock("@/composables/use-server-canvases", () => ({ focusServerCanvas: vi.fn() }));
vi.mock("@/composables/use-agent-browser", () => ({ useAgentBrowser: vi.fn() }));
vi.mock("@/composables/use-session-permissions", async () => {
  const { shallowRef: ref } = await import("vue");
  return { useSessionPermissions: () => ({ asks: ref([]), answer: vi.fn() }) };
});
vi.mock("@/stores/built-in-skills", () => ({ useBuiltInSkillsStore: () => ({ ensureLoaded: vi.fn(), skills: [] }) }));

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://mini.example.test:2113",
  token: "mini-token-0123456789",
  addedAt: "2026-10-08T00:00:00.000Z",
};

function fromPeer(machine?: MachineConnection): AccumulatedMessage {
  const attributes = machine ? ` machine="${machine.id}" machine-name="${machine.name}"` : "";
  return {
    messageId: "m1",
    sessionId: "s1",
    role: "user",
    createdAt: 1,
    parts: [{ partId: "p1", type: "text", text: `<fleet-session-message from="sender-1" title="Hero image sizes"${attributes}>\nThe widths are in.\n</fleet-session-message>` }],
  };
}

function setSwitch(on: boolean): void {
  const preferences = usePreferencesStore();
  preferences.hasFetched = true;
  preferences.preferences = { ...preferences.preferences, [LIVE_MACHINES_PREFERENCE_KEY]: on ? "true" : "false" };
}

async function open(provided?: MachineConnection) {
  const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
  const wrapper = mount(ActivityStream, {
    props: { sessionId: "s1" },
    global: {
      stubs: { ReasoningBlock: true, WorkingIndicator: true, MessageBubble: true },
      provide: provided ? { [MACHINE_TARGET]: () => targetFor(provided) } : {},
    },
  });
  await flushPromises();
  return wrapper;
}

describe("the chip naming the session a message came from", () => {
  beforeAll(async () => {
    await import("@/components/session/ActivityStream.vue");
  }, 30_000);

  beforeEach(() => {
    window.localStorage.clear();
    setActiveMachine(null);
    // The machine list is read when the store is made; the setup file gives each test (and its mounts) a new one.
    saveMachines([mini]);
    navigate.mockReset();
    vi.stubGlobal("fetch", vi.fn(async () => Response.json({})));
    stream.runningWork = shallowRef([]);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("opens a sender on another machine in place with every machine live", async () => {
    setSwitch(true);
    stream.messages = shallowRef([fromPeer(mini)]);
    const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});
    const wrapper = await open();

    await wrapper.get("[data-testid='peer-from']").trigger("click", { button: 0 });

    expect(openOn).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "sender-1" } }));
    expect(useMachinesStore().sessionTarget("sender-1")).toMatchObject({ key: mini.id, isLive: false });
    wrapper.unmount();
  });

  it("switches to the sender's machine with the switch off, as before", async () => {
    setSwitch(false);
    stream.messages = shallowRef([fromPeer(mini)]);
    const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});
    const wrapper = await open();

    await wrapper.get("[data-testid='peer-from']").trigger("click", { button: 0 });

    expect(openOn).toHaveBeenCalledWith(mini.id, "/sessions/sender-1");
    expect(navigate).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("finds a sender with no machine of its own on this session's machine, opened in place", async () => {
    setSwitch(true);
    stream.messages = shallowRef([fromPeer()]);
    const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});
    const wrapper = await open(mini);

    await wrapper.get("[data-testid='peer-from']").trigger("click", { button: 0 });

    expect(openOn).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ params: { id: "sender-1" } }));
    expect(useMachinesStore().sessionTarget("sender-1").key).toBe(mini.id);
    wrapper.unmount();
  });
});
