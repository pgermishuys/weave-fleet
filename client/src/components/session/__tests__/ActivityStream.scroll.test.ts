import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { nextTick, shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import { runSessionCommand } from "@/lib/session-commands";

const { stream, loadOlder } = vi.hoisted(() => ({
  stream: {
    messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]>,
    hasMore: null as unknown as import("vue").ShallowRef<boolean>,
    isLoadingOlder: null as unknown as import("vue").ShallowRef<boolean>,
  },
  loadOlder: vi.fn(),
}));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  stream.hasMore = ref(false);
  stream.isLoadingOlder = ref(false);
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => []),
      runningWork: computed(() => []),
      sessionStatus: computed(() => "idle"),
      isLoading: ref(false),
      hasMore: stream.hasMore,
      isLoadingOlder: stream.isLoadingOlder,
      isPartial: ref(false),
      loadOlder,
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

function message(index: number): AccumulatedMessage {
  const id = `m${String(index).padStart(2, "0")}`;
  return {
    messageId: id,
    sessionId: "s1",
    role: index % 2 ? "user" : "assistant",
    createdAt: index,
    agent: index % 2 ? undefined : "build",
    parts: [{ partId: `${id}-p`, type: "text" as const, text: `Message ${index}` }],
  };
}

function conversation(count: number, from = 1): AccumulatedMessage[] {
  return Array.from({ length: count }, (_, offset) => message(from + offset));
}

/** The scroll box's geometry, which jsdom doesn't lay out: the test moves it by hand. */
interface Geometry {
  scrollHeight: number;
  clientHeight: number;
  scrollTop: number;
}

let wrapper: Pick<VueWrapper, "find" | "findAll" | "setProps" | "unmount"> | null = null;

async function open(messages: AccumulatedMessage[], geometry: Geometry) {
  stream.messages = shallowRef<readonly AccumulatedMessage[]>(messages);
  const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
  const mounted = mount(ActivityStream, {
    props: { sessionId: "s1" },
    global: { stubs: { ReasoningBlock: true, WorkingIndicator: true, MessageBubble: true } },
    attachTo: document.body,
  });
  wrapper = mounted;
  const box = mounted.find<HTMLElement>("[data-testid='activity-stream']").element;
  Object.defineProperty(box, "scrollHeight", { get: () => geometry.scrollHeight, configurable: true });
  Object.defineProperty(box, "clientHeight", { get: () => geometry.clientHeight, configurable: true });
  Object.defineProperty(box, "scrollTop", {
    get: () => geometry.scrollTop,
    set: (value: number) => { geometry.scrollTop = value; },
    configurable: true,
  });
  // The mount's own scroll to the bottom.
  await nextTick();
  await nextTick();
  const scrollTo = async (top: number) => {
    geometry.scrollTop = top;
    box.dispatchEvent(new Event("scroll"));
    await nextTick();
  };
  const frame = async () => {
    vi.advanceTimersToNextFrame();
    await nextTick();
  };
  const shown = () => wrapper!.findAll("[data-message-id]").map((node) => node.attributes("data-message-id"));
  return { box, geometry, scrollTo, frame, shown };
}

describe("ActivityStream scrolling and incremental mount", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    if (stream.hasMore) stream.hasMore.value = false;
    if (stream.isLoadingOlder) stream.isLoadingOlder.value = false;
    loadOlder.mockClear();
    vi.useFakeTimers({ toFake: ["requestAnimationFrame", "cancelAnimationFrame", "setTimeout", "clearTimeout"] });
    // jsdom has neither.
    Element.prototype.scrollIntoView = vi.fn();
    vi.stubGlobal("CSS", { escape: (value: string) => value });
  });

  afterEach(() => {
    wrapper?.unmount();
    wrapper = null;
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("starts at the bottom, without the jump button", async () => {
    const { box, geometry } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

    expect(geometry.scrollTop).toBe(2000);
    expect(box).toBeTruthy();
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(false);
  });

  it("offers Jump to latest once scrolled up past the threshold, and scrolls to the bottom when clicked", async () => {
    const { geometry, scrollTo } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

    // Within 80px of the bottom still counts as at the bottom.
    await scrollTo(1500 - 70);
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(false);

    await scrollTo(1500 - 90);
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(true);

    await wrapper!.find(".jump-to-latest").trigger("click");
    expect(geometry.scrollTop).toBe(2000);
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(false);
  });

  it("keeps the view pinned to the bottom as content grows", async () => {
    const { geometry, frame } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

    geometry.scrollHeight = 2100;
    stream.messages.value = [...stream.messages.value, message(7)];
    await nextTick();
    await nextTick();
    await frame();

    expect(geometry.scrollTop).toBe(2100);
  });

  it("leaves the view where it is when scrolled up and content grows", async () => {
    const { geometry, scrollTo, frame } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
    await scrollTo(600);

    geometry.scrollHeight = 2100;
    stream.messages.value = [...stream.messages.value, message(7)];
    await nextTick();
    await nextTick();
    await frame();
    await frame();

    expect(geometry.scrollTop).toBe(600);
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(true);
  });

  it("pins again for a session switch", async () => {
    const { geometry, scrollTo } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
    await scrollTo(600);

    await wrapper!.setProps({ sessionId: "s2" });
    await nextTick();
    await nextTick();

    expect(geometry.scrollTop).toBe(2000);
    expect(wrapper!.find(".jump-to-latest").exists()).toBe(false);
  });

  describe("a long conversation", () => {
    it("shows the newest 20 first, then mounts older ones a batch a frame", async () => {
      const geometry = { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 };
      const opened = await open([], geometry);
      stream.messages.value = conversation(65);
      await nextTick();

      expect(opened.shown()).toHaveLength(20);
      expect(opened.shown()[0]).toBe("m46");

      // Two frames before the batches start, then 20 a frame.
      await opened.frame();
      expect(opened.shown()).toHaveLength(20);
      await opened.frame();
      expect(opened.shown()).toHaveLength(40);
      await opened.frame();
      expect(opened.shown()).toHaveLength(60);
      await opened.frame();
      expect(opened.shown()).toHaveLength(65);
      expect(opened.shown()[0]).toBe("m01");
    });

    it("mounts every message at once on reaching the top", async () => {
      const geometry = { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 };
      const opened = await open([], geometry);
      stream.messages.value = conversation(65);
      await nextTick();
      expect(opened.shown()).toHaveLength(20);

      await opened.scrollTo(50);

      expect(opened.shown()).toHaveLength(65);
      // Mounting the rest is the answer; nothing is fetched yet.
      expect(loadOlder).not.toHaveBeenCalled();
    });

    it("mounts one message at a time as it arrives", async () => {
      const geometry = { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 };
      const opened = await open(conversation(25), geometry);
      expect(opened.shown()).toHaveLength(25);

      stream.messages.value = [...stream.messages.value, message(26)];
      await nextTick();

      expect(opened.shown()).toHaveLength(26);
    });
  });

  describe("older messages from the server", () => {
    it("asks for them when scrolled near the top, and not before", async () => {
      stream.hasMore.value = true;
      const { scrollTo } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

      await scrollTo(600);
      expect(loadOlder).not.toHaveBeenCalled();

      await scrollTo(100);
      expect(loadOlder).toHaveBeenCalledTimes(1);
    });

    it("does not ask again while they are loading, or when there are none", async () => {
      const { scrollTo } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
      await scrollTo(50);
      expect(loadOlder).not.toHaveBeenCalled();

      stream.hasMore.value = true;
      stream.isLoadingOlder.value = true;
      await nextTick();
      await scrollTo(40);
      expect(loadOlder).not.toHaveBeenCalled();
    });

    it("keeps the reading position when they arrive above", async () => {
      stream.hasMore.value = true;
      const { geometry, scrollTo } = await open(
        conversation(6, 10),
        { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 },
      );
      await scrollTo(60);

      stream.isLoadingOlder.value = true;
      await nextTick();
      // Older messages are prepended and the content gets 900px taller.
      geometry.scrollHeight = 2900;
      stream.messages.value = [...conversation(4, 6), ...stream.messages.value];
      stream.isLoadingOlder.value = false;
      await nextTick();
      await nextTick();
      await nextTick();

      expect(geometry.scrollTop).toBe(60 + 900);
    });
  });

  describe("showing a message", () => {
    it("scrolls to it, highlights it for a moment and keeps every message laid out meanwhile", async () => {
      const { box, shown } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
      expect(shown()).toContain("m02");

      runSessionCommand("show-message", "s1", { messageId: "m02" });
      await nextTick();
      await nextTick();

      expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({ block: "center", behavior: "smooth" });
      const target = () => wrapper!.find("[data-message-id='m02']");
      expect(target().classes()).toContain("activity-message--shown");
      expect(box.classList.contains("activity-stream--laid-out")).toBe(true);

      vi.advanceTimersByTime(1599);
      await nextTick();
      expect(target().classes()).toContain("activity-message--shown");

      vi.advanceTimersByTime(1);
      await nextTick();
      expect(target().classes()).not.toContain("activity-message--shown");
      // The layout stays until the scroll ends, or two seconds.
      expect(box.classList.contains("activity-stream--laid-out")).toBe(true);

      vi.advanceTimersByTime(400);
      expect(box.classList.contains("activity-stream--laid-out")).toBe(false);
    });

    it("ends the laid-out layout when the scroll ends", async () => {
      const { box } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

      runSessionCommand("show-message", "s1", { messageId: "m03" });
      await nextTick();
      await nextTick();
      expect(box.classList.contains("activity-stream--laid-out")).toBe(true);

      box.dispatchEvent(new Event("scrollend"));
      expect(box.classList.contains("activity-stream--laid-out")).toBe(false);
    });

    it("stops pinning to the bottom once it has scrolled to a message", async () => {
      const { geometry, frame } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });

      runSessionCommand("show-message", "s1", { messageId: "m02" });
      await nextTick();
      await nextTick();
      geometry.scrollTop = 300;

      geometry.scrollHeight = 2100;
      stream.messages.value = [...stream.messages.value, message(7)];
      await nextTick();
      await nextTick();
      await frame();
      await frame();

      expect(geometry.scrollTop).toBe(300);
    });

    it("mounts the unmounted older messages first when the target is among them", async () => {
      const opened = await open([], { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
      stream.messages.value = conversation(65);
      await nextTick();
      expect(opened.shown()).not.toContain("m05");

      runSessionCommand("show-message", "s1", { messageId: "m05" });
      await nextTick();
      await nextTick();
      await nextTick();
      await nextTick();

      expect(opened.shown()).toHaveLength(65);
      expect(wrapper!.find("[data-message-id='m05']").classes()).toContain("activity-message--shown");
    });
  });

  it("scrolls to the top and the bottom on the session commands", async () => {
    const { box, geometry, scrollTo } = await open(conversation(6), { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
    const scrollToTop = vi.fn();
    box.scrollTo = scrollToTop as unknown as typeof box.scrollTo;

    runSessionCommand("scroll-top", "s1");
    expect(scrollToTop).toHaveBeenCalledWith({ top: 0, behavior: "smooth" });

    await scrollTo(600);
    runSessionCommand("scroll-bottom", "s1");
    expect(geometry.scrollTop).toBe(2000);
  });

  it("disconnects its observers and cancels its work on unmount", async () => {
    const resizeDisconnect = vi.fn();
    vi.stubGlobal("ResizeObserver", class {
      observe = vi.fn();
      unobserve = vi.fn();
      disconnect = resizeDisconnect;
    });
    const mutationDisconnect = vi.spyOn(MutationObserver.prototype, "disconnect");
    const cancelFrame = vi.spyOn(window, "cancelAnimationFrame");

    const opened = await open([], { scrollHeight: 2000, clientHeight: 500, scrollTop: 0 });
    // Batches pending, and a scroll to the bottom pending.
    stream.messages.value = conversation(65);
    await nextTick();
    await nextTick();

    wrapper!.unmount();
    wrapper = null;

    expect(resizeDisconnect).toHaveBeenCalled();
    expect(mutationDisconnect).toHaveBeenCalled();
    expect(cancelFrame).toHaveBeenCalled();
    mutationDisconnect.mockRestore();
    cancelFrame.mockRestore();

    // The commands go with it.
    expect(() => runSessionCommand("scroll-bottom", "s1")).not.toThrow();
    expect(opened.geometry.scrollTop).toBeGreaterThanOrEqual(0);
  });
});
