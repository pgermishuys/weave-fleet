import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SideConversationPanel from "@/components/session/SideConversationPanel.vue";
import { _resetSideConversationsForTesting } from "@/composables/use-side-conversation";

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(), POST: vi.fn(), DELETE: vi.fn() },
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
};

function mountPanel() {
  return mount(SideConversationPanel, {
    props: { sessionId: "session-1" },
    global: {
      stubs: {
        ActivityStream: { props: ["sessionId", "after"], template: "<div data-testid='side-stream' :data-session='sessionId' :data-after='after' />" },
      },
    },
  });
}

describe("SideConversationPanel", () => {
  beforeEach(() => {
    _resetSideConversationsForTesting();
    vi.resetAllMocks();
  });

  it("shows nothing when the session has no side conversation", async () => {
    mockApi.GET.mockResolvedValue({ data: undefined, error: undefined, response: new Response(null, { status: 204 }) } as never);
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
  });

  it("shows the side conversation's own messages, after what it copied", async () => {
    mockApi.GET.mockResolvedValue({ data: side, error: undefined, response: new Response(null, { status: 200 }) } as never);
    const wrapper = mountPanel();
    await flushPromises();

    expect(wrapper.get("[data-testid='side-conversation']").text()).toContain("what changed?");
    const stream = wrapper.get("[data-testid='side-stream']");
    expect(stream.attributes("data-session")).toBe("side-1");
    expect(stream.attributes("data-after")).toBe("msg_boundary");
  });

  it("closes: the panel goes and the server deletes the fork", async () => {
    mockApi.GET.mockResolvedValue({ data: side, error: undefined, response: new Response(null, { status: 200 }) } as never);
    mockApi.DELETE.mockResolvedValue({ data: undefined, error: undefined, response: new Response(null, { status: 204 }) } as never);
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-close']").trigger("click");
    await flushPromises();

    expect(mockApi.DELETE).toHaveBeenCalledWith("/api/sessions/{id}/side", { params: { path: { id: "session-1" } } });
    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
  });

  it("keeps it as a session and opens it", async () => {
    mockApi.GET.mockResolvedValue({ data: side, error: undefined, response: new Response(null, { status: 200 }) } as never);
    mockApi.POST.mockResolvedValue({ data: side, error: undefined, response: new Response(null, { status: 200 }) } as never);
    const wrapper = mountPanel();
    await flushPromises();

    await wrapper.get("[data-testid='side-conversation-keep']").trigger("click");
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/side/keep", { params: { path: { id: "session-1" } } });
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "side-1" } }));
    expect(wrapper.find("[data-testid='side-conversation']").exists()).toBe(false);
  });
});
