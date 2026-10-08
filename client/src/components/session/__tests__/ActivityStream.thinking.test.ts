import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import type { RunningWorkItem } from "@/lib/running-work";
import { useThemeStore } from "@/stores/theme";

const { stream, apiGet } = vi.hoisted(() => ({
  stream: {
    messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]>,
    runningWork: null as unknown as import("vue").ShallowRef<readonly RunningWorkItem[]>,
    status: "idle",
  },
  apiGet: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet } }));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => []),
      runningWork: computed(() => stream.runningWork.value),
      sessionStatus: computed(() => stream.status),
      isLoading: ref(false),
      hasMore: ref(false),
      isLoadingOlder: ref(false),
      isPartial: ref(false),
      loadOlder: () => undefined,
    }),
  };
});

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
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

const prompt: AccumulatedMessage = {
  messageId: "m1",
  sessionId: "s1",
  role: "user",
  createdAt: 1,
  parts: [{ partId: "p1", type: "text", text: "Page the orders list." }],
};

// The model has only thought so far: no text, no tool call yet.
const thinking: AccumulatedMessage = {
  messageId: "m2",
  sessionId: "s1",
  role: "assistant",
  createdAt: 2,
  agent: "build",
  parts: [{ partId: "r1", type: "reasoning", text: "A cursor on (created_at, id) uses the index." }],
};

async function open() {
  const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
  const wrapper = mount(ActivityStream, {
    props: { sessionId: "s1" },
    global: { stubs: { ReasoningBlock: true, WorkingIndicator: true, MessageBubble: true } },
  });
  await flushPromises();
  return wrapper;
}

describe("ActivityStream and thinking", () => {
  // The first import of the conversation is slow; not part of any one test.
  beforeAll(async () => {
    await import("@/components/session/ActivityStream.vue");
  }, 30_000);

  beforeEach(() => {
    window.localStorage.clear();
    setActivePinia(createPinia());
    apiGet.mockReset();
    stream.messages = shallowRef([prompt, thinking]);
    stream.runningWork = shallowRef([]);
    stream.status = "idle";
  });

  it("shows a message that so far is only thinking, marked live while the turn runs", async () => {
    stream.status = "busy";
    const wrapper = await open();

    const block = wrapper.getComponent({ name: "ReasoningBlock" });
    expect(block.props("text")).toBe("A cursor on (created_at, id) uses the index.");
    expect(block.props("live")).toBe(true);
    wrapper.unmount();
  });

  it("isn't live once the turn has ended, or once something follows the thinking", async () => {
    let wrapper = await open();
    expect(wrapper.getComponent({ name: "ReasoningBlock" }).props("live")).toBe(false);
    wrapper.unmount();

    stream.status = "busy";
    stream.messages = shallowRef([prompt, { ...thinking, parts: [...thinking.parts, { partId: "t1", type: "text", text: "Switching to a cursor." }] }]);
    wrapper = await open();
    expect(wrapper.getComponent({ name: "ReasoningBlock" }).props("live")).toBe(false);
    wrapper.unmount();
  });

  it("leaves out a thinking-only message when thinking is hidden", async () => {
    useThemeStore().setThinking("hidden");
    const wrapper = await open();

    expect(wrapper.find(".activity-message--assistant").exists()).toBe(false);
    wrapper.unmount();
  });
});
