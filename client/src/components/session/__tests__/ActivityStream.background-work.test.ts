import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";

const { stream, apiGet } = vi.hoisted(() => ({
  stream: {
    messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]>,
    runningWork: null as unknown as import("vue").ShallowRef<readonly RunningWorkItem[]>,
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
      sessionStatus: computed(() => "idle"),
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

// OpenCode 2 started a dev server in the background; Fleet stopped it, and V2's notice calls the removed shell an error.
const call: AccumulatedMessage = {
  messageId: "m1",
  sessionId: "s1",
  role: "assistant",
  createdAt: 1,
  agent: "build",
  parts: [{
    partId: "p1",
    type: "tool",
    tool: "shell",
    callId: "call_dev",
    state: { status: "running", background: true, input: { command: "sh scripts/dev.sh" }, metadata: { shellID: "sh_dev" } },
  }],
};
const notice: AccumulatedMessage = {
  messageId: "m2",
  sessionId: "s1",
  role: "user",
  createdAt: 2,
  parts: [{ partId: "p2", type: "text", text: '<shell id="sh_dev" state="error" command="sh scripts/dev.sh">\nShell.NotFoundError\n</shell>' }],
};
const stoppedPayload = {
  id: "w1", sessionId: "s1", workId: "sh_dev", kind: "shell", title: "shell", label: "sh scripts/dev.sh", status: "cancelled",
  background: true, toolCallId: "call_dev", canStop: true, startedAt: "2026-10-04T10:00:00Z", endedAt: "2026-10-04T10:05:00Z",
  endedReason: "cancelled", detail: "stopped",
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

/** What the notice's bubble shows under its line. */
function bodies(wrapper: Awaited<ReturnType<typeof open>>): string[] {
  return wrapper.findAllComponents({ name: "MessageBubble" }).map((bubble) => String(bubble.props("body") ?? ""));
}

describe("ActivityStream and work Fleet stopped", () => {
  // The first import of the conversation is slow; not part of any one test.
  beforeAll(async () => {
    await import("@/components/session/ActivityStream.vue");
  }, 30_000);

  beforeEach(() => {
    setActivePinia(createPinia());
    apiGet.mockReset();
    stream.messages = shallowRef([call, notice]);
    stream.runningWork = shallowRef([]);
  });

  it("says stopped, not error, for a shell Fleet stopped", async () => {
    stream.runningWork = shallowRef([toRunningWorkItem(stoppedPayload)!]);
    const wrapper = await open();

    const note = wrapper.get("[data-testid='background-note']");
    expect(note.text()).toContain("stopped");
    expect(note.classes()).toContain("background-note--cancelled");
    expect(bodies(wrapper).join()).not.toContain("Shell.NotFoundError");
    expect(apiGet).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("asks Fleet for older work when the stream no longer has it", async () => {
    apiGet.mockResolvedValue({ data: [stoppedPayload], response: { ok: true } });
    const wrapper = await open();

    expect(apiGet).toHaveBeenCalledWith("/api/sessions/{id}/work", { params: { path: { id: "s1" }, query: { all: true } } });
    expect(wrapper.get("[data-testid='background-note']").text()).toContain("stopped");
    wrapper.unmount();
  });

  it("keeps a real failure a failure", async () => {
    apiGet.mockResolvedValue({ data: [{ ...stoppedPayload, status: "error", endedReason: "error", detail: "exit 1" }], response: { ok: true } });
    const wrapper = await open();

    const note = wrapper.get("[data-testid='background-note']");
    expect(note.text()).toContain("error");
    expect(bodies(wrapper).join()).toContain("Shell.NotFoundError");
    wrapper.unmount();
  });
});
