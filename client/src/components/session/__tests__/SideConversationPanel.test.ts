import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import SideConversationPanel from "@/components/session/SideConversationPanel.vue";
import { SIDE_DISCARD_UNDO_MS, _resetSideConversationsForTesting, useSideConversation } from "@/composables/use-side-conversation";

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() },
}));

const navigate = vi.fn();
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

import { api } from "@/api/client";

const mockApi = vi.mocked(api);
const side = {
  sessionId: "side-1",
  instanceId: "instance-side",
  title: "btw: what changed?",
  boundaryMessageId: "msg_boundary",
  createdAt: "2026-09-25T00:00:00Z",
  minimized: false,
  seenAnswerId: null as string | null,
};

/** Stands in for the side conversation's stream; tests make it report progress the way the real one does. */
let report: ((progress: { working: boolean; latestAnswer: string | null; latestAnswerId: string | null }) => void) | undefined;
const StreamStub = defineComponent({
  props: { sessionId: String, after: String },
  emits: ["progress"],
  setup(props, { emit }) {
    report = (progress) => emit("progress", progress);
    return () => h("div", { "data-testid": "side-stream", "data-session": props.sessionId, "data-after": props.after });
  },
});

function ok(data: unknown, status = 200) {
  return { data, error: undefined, response: new Response(null, { status }) } as never;
}

function mountPanel() {
  return mount(SideConversationPanel, {
    attachTo: document.body,
    props: { sessionId: "session-1" },
    global: { stubs: { ActivityStream: StreamStub } },
  });
}

function pressCtrlSlash(): KeyboardEvent {
  const event = new KeyboardEvent("keydown", { key: "/", ctrlKey: true, bubbles: true, cancelable: true });
  window.dispatchEvent(event);
  return event;
}

describe("SideConversationPanel", () => {
  beforeEach(() => {
    _resetSideConversationsForTesting();
    vi.resetAllMocks();
    report = undefined;
    mockApi.PUT.mockImplementation((async (url: string, init: { body: { minimized?: boolean; answerId?: string } }) =>
      url.endsWith("/seen") ? ok({ ...side, seenAnswerId: init.body.answerId }) : ok({ ...side, minimized: init.body.minimized })) as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  it("shows nothing when the session has no side conversation", async () => {
    mockApi.GET.mockResolvedValue(ok(undefined, 204));
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
  });

  it("shows the side conversation's own messages, after what it copied", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.get("[data-testid='side-conversation']").text()).toContain("what changed?");
    const stream = wrapper.get("[data-testid='side-stream']");
    expect(stream.attributes("data-session")).toBe("side-1");
    expect(stream.attributes("data-after")).toBe("msg_boundary");
  });

  it("minimizes into a tab, keeps the conversation mounted, and saves it on the server", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-minimize']").trigger("click");
    await flushPromises();

    expect(mockApi.PUT).toHaveBeenCalledWith("/api/sessions/{id}/side/minimized", { params: { path: { id: "session-1" } }, body: { minimized: true } });
    expect(wrapper.get("[data-testid='side-conversation']").attributes("data-minimized")).toBe("true");
    expect(wrapper.get("[data-testid='side-conversation-tab']").text()).toContain("what changed?");
    expect(wrapper.find("[data-testid='side-stream']").exists()).toBe(true);
  });

  it("comes back minimized after a reload", async () => {
    mockApi.GET.mockResolvedValue(ok({ ...side, minimized: true }));
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.find("[data-testid='side-conversation-tab']").exists()).toBe(true);
  });

  it("says Thinking while it works, then New answer when one lands while it's folded, until it's opened", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    const wrapper = mountPanel();
    await flushPromises();
    report!({ working: false, latestAnswer: "The first answer.", latestAnswerId: "a1" });
    await wrapper.get("[data-testid='side-conversation-minimize']").trigger("click");
    await flushPromises();
    const state = () => wrapper.get("[data-testid='side-conversation-state']").text();
    expect(state()).toBe("Answered");

    report!({ working: true, latestAnswer: "The first answer.", latestAnswerId: "a1" });
    await flushPromises();
    expect(state()).toBe("Thinking");

    report!({ working: false, latestAnswer: "The second answer.", latestAnswerId: "a2" });
    await flushPromises();
    expect(state()).toBe("New answer");
    // Landed on this page: the dot pulses.
    expect(wrapper.find(".side-tab__unread--pulse").exists()).toBe(true);
    expect(wrapper.get("[data-testid='side-conversation-peek']").text()).toContain("The second answer.");
    expect(wrapper.get("[data-testid='side-conversation-peek']").text()).toContain("Click the tab to open");

    await wrapper.get("[data-testid='side-conversation-open']").trigger("click");
    await flushPromises();
    expect(mockApi.PUT).toHaveBeenCalledWith("/api/sessions/{id}/side/minimized", { params: { path: { id: "session-1" } }, body: { minimized: false } });
    // Opening it is seeing it: the server keeps that, so a reload doesn't call it new.
    expect(mockApi.PUT).toHaveBeenCalledWith("/api/sessions/{id}/side/seen", { params: { path: { id: "session-1" } }, body: { answerId: "a2" } });

    await wrapper.get("[data-testid='side-conversation-minimize']").trigger("click");
    await flushPromises();
    expect(state()).toBe("Answered");
  });

  it("Ctrl+/ folds it and opens it again", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    const wrapper = mountPanel();
    await flushPromises();

    expect(pressCtrlSlash().defaultPrevented).toBe(true);
    await flushPromises();
    expect(wrapper.get("[data-testid='side-conversation']").attributes("data-minimized")).toBe("true");

    pressCtrlSlash();
    await flushPromises();
    expect(wrapper.get("[data-testid='side-conversation']").attributes("data-minimized")).toBe("false");
  });

  it("leaves Ctrl+/ alone without a side conversation", async () => {
    mockApi.GET.mockResolvedValue(ok(undefined, 204));
    mountPanel();
    await flushPromises();

    expect(pressCtrlSlash().defaultPrevented).toBe(false);
  });

  it("discards at once, offers Undo, and Undo brings it back as it was", async () => {
    mockApi.GET.mockResolvedValue(ok({ ...side, minimized: true }));
    mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
    mockApi.POST.mockResolvedValue(ok({ ...side, minimized: true }));
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-tab-close']").trigger("click");
    await flushPromises();

    expect(mockApi.DELETE).toHaveBeenCalledWith("/api/sessions/{id}/side", { params: { path: { id: "session-1" } } });
    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='side-conversation-undo-toast']").text()).toContain("Side question discarded");

    await wrapper.get("[data-testid='side-conversation-undo']").trigger("click");
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/side/restore", { params: { path: { id: "session-1" } } });
    expect(wrapper.find("[data-testid='side-conversation-tab']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(false);
  });

  it("stops offering Undo after its window", async () => {
    vi.useFakeTimers();
    mockApi.GET.mockResolvedValue(ok(side));
    mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-close']").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(true);

    vi.advanceTimersByTime(SIDE_DISCARD_UNDO_MS + 10);
    await flushPromises();
    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(false);
  });

  it("keeps it as a session and opens it", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    mockApi.POST.mockResolvedValue(ok(side));
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-keep']").trigger("click");
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/side/keep", { params: { path: { id: "session-1" } } });
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "side-1" } }));
    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
  });
});

describe("SideConversationPanel after a reload", () => {
  beforeEach(() => {
    _resetSideConversationsForTesting();
    vi.resetAllMocks();
    report = undefined;
    mockApi.PUT.mockImplementation((async (url: string, init: { body: { minimized?: boolean; answerId?: string } }) =>
      url.endsWith("/seen") ? ok({ ...side, seenAnswerId: init.body.answerId }) : ok({ ...side, minimized: init.body.minimized })) as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  it("says New answer for one newer than the one seen, without the pulse", async () => {
    mockApi.GET.mockResolvedValue(ok({ ...side, minimized: true, seenAnswerId: "a1" }));
    const wrapper = mountPanel();
    await flushPromises();

    report!({ working: false, latestAnswer: "Answered while the page was closed.", latestAnswerId: "a2" });
    await flushPromises();

    expect(wrapper.get("[data-testid='side-conversation-state']").text()).toBe("New answer");
    expect(wrapper.find(".side-tab__unread").exists()).toBe(true);
    expect(wrapper.find(".side-tab__unread--pulse").exists()).toBe(false);
  });

  it("says Answered for the one already seen", async () => {
    mockApi.GET.mockResolvedValue(ok({ ...side, minimized: true, seenAnswerId: "a2" }));
    const wrapper = mountPanel();
    await flushPromises();

    report!({ working: false, latestAnswer: "Seen before.", latestAnswerId: "a2" });
    await flushPromises();

    expect(wrapper.get("[data-testid='side-conversation-state']").text()).toBe("Answered");
  });

  it("offers Undo again for the time the server says is left", async () => {
    mockApi.GET.mockImplementation((async (url: string) => url.endsWith("/discarded")
      ? ok({ sideConversation: { ...side, minimized: true }, undoRemainingMs: 5_000 })
      : ok(undefined, 204)) as never);
    mockApi.POST.mockResolvedValue(ok({ ...side, minimized: true }));
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.get("[data-testid='side-conversation-undo-toast']").text()).toContain("Side question discarded");
    expect(wrapper.get("[data-testid='side-conversation-undo-left']").text()).toBe("5 s");

    await wrapper.get("[data-testid='side-conversation-undo']").trigger("click");
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/side/restore", { params: { path: { id: "session-1" } } });
    expect(wrapper.find("[data-testid='side-conversation-tab']").exists()).toBe(true);
  });

  it("shows nothing once the window has closed", async () => {
    mockApi.GET.mockResolvedValue(ok(undefined, 204));
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(false);
  });

  it("a new side question ends the Undo of a discarded one", async () => {
    mockApi.GET.mockResolvedValue(ok(side));
    mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
    mockApi.POST.mockResolvedValue(ok({ sideConversation: { ...side, sessionId: "side-2" }, correlationId: "c", messageId: "m" }));
    const wrapper = mountPanel();
    await flushPromises();
    await wrapper.get("[data-testid='side-conversation-close']").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(true);

    await useSideConversation("session-1").ask("another one");
    await flushPromises();

    expect(wrapper.find("[data-testid='side-conversation-undo-toast']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='side-stream']").attributes("data-session")).toBe("side-2");
  });
});
