import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, nextTick, shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";

const { stream } = vi.hoisted(() => ({
  stream: { messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]> },
}));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => []),
      runningWork: computed(() => []),
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
  const { shallowRef } = await import("vue");
  return { useSessionPermissions: () => ({ asks: shallowRef([]), answer: vi.fn() }) };
});

vi.mock("@/components/session/MessageBubble.vue", () => ({
  default: defineComponent({
    name: "MessageBubbleProbe",
    inheritAttrs: false,
    props: { body: { type: String, required: true } },
    setup(props) {
      return () => h("div", props.body);
    },
  }),
}));

/**
 * Fires one of the commands the conversation answers, the way the command palette, a shortcut or a canvas does.
 * The plumbing lives here only, so the expectations below don't depend on how a command reaches the stream.
 */
async function fire(name: string, detail: Record<string, unknown>): Promise<void> {
  window.dispatchEvent(new CustomEvent(`weave:command-${name}`, { detail }));
  await nextTick();
  await nextTick();
}

function message(id: string, role: "user" | "assistant", text: string, callId?: string): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role,
    createdAt: Number(id.slice(1)),
    agent: role === "assistant" ? "build" : undefined,
    parts: [
      ...(callId
        ? [{ partId: `${id}-t`, type: "tool" as const, tool: "read", callId, state: { status: "completed", output: "x" } }]
        : []),
      { partId: `${id}-p`, type: "text" as const, text },
    ],
  };
}

describe("ActivityStream answering commands", () => {
  let wrapper: VueWrapper;
  let prompt: HTMLTextAreaElement;

  async function open(sessionId = "s1") {
    stream.messages = shallowRef<readonly AccumulatedMessage[]>([
      message("m1", "user", "Rename the helper"),
      message("m2", "assistant", "Renamed it", "call-rename"),
      message("m3", "user", "Thanks"),
    ]);
    const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
    wrapper = mount(ActivityStream, {
      props: { sessionId },
      global: { stubs: { ReasoningBlock: true, WorkingIndicator: true } },
      attachTo: document.body,
    });
    await nextTick();
  }

  beforeEach(() => {
    setActivePinia(createPinia());
    prompt = document.createElement("textarea");
    prompt.setAttribute("data-testid", "prompt-input");
    document.body.append(prompt);
    // jsdom has neither.
    Element.prototype.scrollIntoView = vi.fn();
    Element.prototype.scrollTo = vi.fn() as unknown as typeof Element.prototype.scrollTo;
    vi.stubGlobal("CSS", { escape: (value: string) => value });
  });

  afterEach(() => {
    wrapper?.unmount();
    prompt.remove();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("focus-prompt focuses the composer when it names this session", async () => {
    await open();

    await fire("focus-prompt", { sessionId: "other" });
    expect(document.activeElement).not.toBe(prompt);

    await fire("focus-prompt", { sessionId: "s1" });
    expect(document.activeElement).toBe(prompt);
  });

  it("copy-session-id puts this session's id on the clipboard", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    await open();

    await fire("copy-session-id", { sessionId: "other" });
    expect(writeText).not.toHaveBeenCalled();

    await fire("copy-session-id", { sessionId: "s1" });
    expect(writeText).toHaveBeenCalledExactlyOnceWith("s1");
  });

  it("export-conversation downloads the messages as JSON named after the session", async () => {
    let blob: Blob | undefined;
    URL.createObjectURL = vi.fn((value: Blob) => {
      blob = value;
      return "blob:fake";
    });
    URL.revokeObjectURL = vi.fn();
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download);
    });
    await open();

    await fire("export-conversation", { sessionId: "other" });
    expect(downloads).toEqual([]);

    await fire("export-conversation", { sessionId: "s1" });
    expect(downloads).toEqual(["s1-conversation.json"]);
    const payload = JSON.parse(await blob!.text());
    expect(payload.sessionId).toBe("s1");
    expect(payload.title).toBe("s1");
    expect(payload.messages.map((entry: AccumulatedMessage) => entry.messageId)).toEqual(["m1", "m2", "m3"]);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:fake");
  });

  it("scroll-top scrolls the stream to its top, smoothly, for this session only", async () => {
    await open();

    await fire("scroll-top", { sessionId: "other" });
    expect(Element.prototype.scrollTo).not.toHaveBeenCalled();

    await fire("scroll-top", { sessionId: "s1" });
    expect(Element.prototype.scrollTo).toHaveBeenCalledExactlyOnceWith({ top: 0, behavior: "smooth" });
  });

  it("scroll-bottom jumps the stream to its end, for this session only", async () => {
    await open();
    const element = wrapper.get('[data-testid="activity-stream"]').element;
    Object.defineProperty(element, "scrollHeight", { configurable: true, value: 900 });
    element.scrollTop = 10;

    await fire("scroll-bottom", { sessionId: "other" });
    expect(element.scrollTop).toBe(10);

    await fire("scroll-bottom", { sessionId: "s1" });
    expect(element.scrollTop).toBe(900);
  });

  it("show-message scrolls to a message by id, or by the tool call it holds, and marks it", async () => {
    await open();

    await fire("show-message", { sessionId: "other", messageId: "m1" });
    expect(Element.prototype.scrollIntoView).not.toHaveBeenCalled();

    await fire("show-message", { sessionId: "s1", messageId: "m1" });
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledTimes(1);

    await fire("show-message", { sessionId: "s1", toolCallId: "call-rename" });
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledTimes(2);

    await fire("show-message", { sessionId: "s1", toolCallId: "call-unknown" });
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledTimes(2);
  });

  it("stops answering once it is unmounted", async () => {
    await open();
    wrapper.unmount();

    await fire("focus-prompt", { sessionId: "s1" });
    expect(document.activeElement).not.toBe(prompt);
    // Mounted again so afterEach has something to unmount.
    await open();
  });
});
