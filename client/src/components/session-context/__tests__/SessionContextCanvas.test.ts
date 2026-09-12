import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SmartLinkWire } from "@/lib/smart-links";

const { apiFetchMock, navigateMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigateMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }) }));

import SessionContextCanvas from "@/components/session-context/SessionContextCanvas.vue";
import { useSmartLinksStore } from "@/stores/smart-links";

function wire(id: string, relationship: string, overrides: Partial<SmartLinkWire> = {}): SmartLinkWire {
  return {
    id,
    sessionId: "s1",
    url: `https://github.com/o/r/pull/${id}`,
    providerId: "github",
    resourceType: "pull_request",
    resourceId: `o/r#${id}`,
    title: `o/r #${id}: Link ${id}`,
    status: "open",
    statusLabel: "Open",
    metadataJson: JSON.stringify({
      owner: "o",
      repo: "r",
      number: Number(id),
      ci: { headSha: "abc1234", ciStatus: "failure", checkRuns: [{ id: 1, name: "client-tests", status: "completed", conclusion: "failure", htmlUrl: "", workflowName: null, startedAt: null, completedAt: null }] },
    }),
    isDismissed: false,
    isTerminal: false,
    createdAt: "",
    updatedAt: "",
    relationship,
    enrichmentStatus: "resolved",
    lastCheckedAt: null,
    ...overrides,
  };
}

function mountCanvas() {
  return mount(SessionContextCanvas, { props: { sessionId: "s1" }, attachTo: document.body });
}

describe("SessionContextCanvas", () => {
  // test-setup.ts installs a fresh Pinia per test for both the test and mounted components.
  beforeEach(() => {
    apiFetchMock.mockReset();
    navigateMock.mockReset();
    apiFetchMock.mockImplementation(() => Promise.resolve(new Response("[]", { status: 200 })));
  });

  it("groups links by how they relate to the session", async () => {
    useSmartLinksStore().setLinks("s1", [
      wire("42", "origin", { resourceType: "issue", url: "https://github.com/o/r/issues/42", metadataJson: null }),
      wire("187", "own"),
      wire("7", "mentioned"),
    ]);

    const wrapper = mountCanvas();
    await flushPromises();

    const labels = wrapper.findAll(".context-section__label").map((label) => label.text());
    expect(labels).toEqual(["Started from", "This session's pull request", "Mentioned in conversation"]);
    expect(wrapper.text()).toContain("1 failing");
    wrapper.unmount();
  });

  it("sends a failing check to the agent from a visible button", async () => {
    useSmartLinksStore().setLinks("s1", [wire("187", "own")]);
    const wrapper = mountCanvas();
    await flushPromises();

    const send = wrapper.findAll("button").find((button) => button.text().includes("Send to agent"));
    expect(send).toBeDefined();
    await send!.trigger("click");
    await flushPromises();

    const call = apiFetchMock.mock.calls.find(([path]) => path === "/api/sessions/s1/prompt");
    expect(call).toBeDefined();
    expect(JSON.parse((call![1] as RequestInit).body as string).text).toContain("Workflow: client-tests");
    expect(wrapper.text()).toContain("Sent");
    wrapper.unmount();
  });

  it("asks once to connect GitHub when links can't be checked", async () => {
    useSmartLinksStore().setLinks("s1", [
      wire("1", "own", { enrichmentStatus: "not_connected", metadataJson: null }),
      wire("2", "mentioned", { enrichmentStatus: "not_connected", metadataJson: null }),
    ]);
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.findAll(".context-notice")).toHaveLength(1);
    await wrapper.get(".context-notice button").trigger("click");
    expect(navigateMock).toHaveBeenCalledWith({ to: "/settings/plugins/$pluginId", params: { pluginId: "github" } });
    wrapper.unmount();
  });

  it("explains an empty session", async () => {
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.get(".context-empty").text()).toContain("Nothing attached yet");
    wrapper.unmount();
  });
});
