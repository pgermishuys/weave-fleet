import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, nextTick, onBeforeUpdate, shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";

const { stream, rendered } = vi.hoisted(() => ({
  stream: { messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]> },
  rendered: [] as string[],
}));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => []),
      sessionStatus: computed(() => "busy"),
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

// Records every render of a bubble by the text it shows.
vi.mock("@/components/session/MessageBubble.vue", () => ({
  default: defineComponent({
    name: "MessageBubbleProbe",
    // The rest of a bubble's props arrive as attrs, which Vue compares the same way when deciding to re-render.
    inheritAttrs: false,
    props: { body: { type: String, required: true } },
    setup(props) {
      rendered.push(props.body);
      onBeforeUpdate(() => {
        rendered.push(props.body);
      });
      return () => h("div", props.body);
    },
  }),
}));

function message(id: string, role: "user" | "assistant", text: string, withTool = false): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role,
    createdAt: Number(id.slice(1)),
    agent: role === "assistant" ? "build" : undefined,
    parts: [
      ...(withTool
        ? [{ partId: `${id}-t`, type: "tool" as const, tool: "read", callId: `${id}-c`, state: { status: "completed", output: "line 1\nline 2" } }]
        : []),
      { partId: `${id}-p`, type: "text" as const, text },
    ],
  };
}

describe("ActivityStream re-rendering while a reply streams", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    rendered.length = 0;
  });

  it("re-renders only the bubble whose message changed", async () => {
    const first = message("m1", "user", "Why is it idle?");
    const second = message("m2", "assistant", "Because the event is dropped.", true);
    const third = message("m3", "user", "And the fix?");
    const streaming = message("m4", "assistant", "Register the listener");
    stream.messages = shallowRef([first, second, third, streaming]);

    const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
    mount(ActivityStream, {
      props: { sessionId: "s1" },
      global: { stubs: { ReasoningBlock: true, WorkingIndicator: true } },
    });
    await nextTick();
    rendered.length = 0;

    // The reducer hands back the same objects for every message a token didn't touch.
    stream.messages.value = [first, second, third, message("m4", "assistant", "Register the listener first")];
    await nextTick();

    expect(rendered).toEqual(["Register the listener first"]);
  });
});

describe("ActivityStream opening a long conversation", () => {
  const conversation = Array.from({ length: 50 }, (_, index) =>
    message(`m${String(index + 1).padStart(2, "0")}`, index % 2 ? "assistant" : "user", `Message ${index + 1}`));

  async function open() {
    stream.messages = shallowRef<readonly AccumulatedMessage[]>([]);
    const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
    const wrapper = mount(ActivityStream, {
      props: { sessionId: "s1" },
      global: { stubs: { ReasoningBlock: true, WorkingIndicator: true } },
      attachTo: document.body,
    });
    await nextTick();
    // The snapshot arrives.
    stream.messages.value = conversation;
    await nextTick();
    const shown = () => wrapper.findAll("[data-message-id]").map((node) => node.attributes("data-message-id"));
    return { wrapper, shown };
  }

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers({ toFake: ["requestAnimationFrame", "cancelAnimationFrame"] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("mounts the newest messages first and the older ones a batch a frame after", async () => {
    const { wrapper, shown } = await open();

    expect(shown()).toHaveLength(20);
    expect(shown()[0]).toBe("m31");
    expect(shown().at(-1)).toBe("m50");

    for (let frame = 0; frame < 4; frame += 1) {
      vi.advanceTimersToNextFrame();
      await nextTick();
    }
    expect(shown()).toHaveLength(50);
    expect(shown()[0]).toBe("m01");
    wrapper.unmount();
  });

  it("mounts the rest at once when asked to show a message that isn't mounted yet", async () => {
    // jsdom has neither.
    Element.prototype.scrollIntoView = vi.fn();
    vi.stubGlobal("CSS", { escape: (value: string) => value });
    const { wrapper, shown } = await open();

    window.dispatchEvent(new CustomEvent("weave:command-show-message", { detail: { sessionId: "s1", messageId: "m02" } }));
    await nextTick();
    await nextTick();

    expect(shown()).toHaveLength(50);
    expect(Element.prototype.scrollIntoView).toHaveBeenCalled();
    wrapper.unmount();
  });
});
